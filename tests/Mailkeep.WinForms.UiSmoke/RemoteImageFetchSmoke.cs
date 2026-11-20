using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

internal static class RemoteImageFetchSmoke
{
    public static async Task Run(string app)
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var origin = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port;
        var requests = 0;
        var serving = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await server.AcceptTcpClientAsync();
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                    Interlocked.Increment(ref requests);
                    var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {png.Length}\r\nConnection: close\r\n\r\n"));
                    await stream.WriteAsync(png);
                }
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        });
        try
        {
            var assembly = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(app))!, "Mailkeep.WinForms.dll"));
            var fetcher = assembly.GetType("Mailkeep.WinForms.RemoteImageFetcher", true)!;
            var inline = fetcher.GetMethod("InlineAsync", BindingFlags.Public | BindingFlags.Static)!;
            var html = $"<img src='{origin}/src.png'><picture><source srcset='{origin}/small.png 1x, {origin}/small.png?large=1 2x'></picture><table background='{origin}/background.png'><td style='background-image:url({origin}/css.png)'></td></table><img src='{origin}/query.png?a=1&amp;b=2,3'>";
            var blocked = await Apply(html, []);
            Check(blocked == html && requests == 0, "Blocked images were fetched or changed.");
            var result = await Apply(html, [origin]);
            Check(requests == 6, $"Expected six different image requests, got {requests}.");
            Check(!result.Contains(origin), "An allowed src/srcset/background/CSS image was not inlined.");
            Check(result.Contains(" 1x, data:image/png;base64,") && result.Contains(" 2x'"), "Srcset descriptors were damaged.");
            Check(result.Contains("data:image/png;base64,"), "No image data was inlined.");
            Check(!result.Contains("?large=1") && !result.Contains("&amp;b=2,3"), "An image URL was only partially replaced.");
            Check(await Apply(html, [origin]) == result && requests == 6, "Cached pictures were fetched again.");
            Console.WriteLine("PASS image-fetch blocked=no-requests src=srcset=background=css=encoded-query=inlined cache=reused");

            Task<string> Apply(string source, string[] allowed) => (Task<string>)inline.Invoke(null, [source, allowed, CancellationToken.None])!;
        }
        finally
        {
            server.Stop();
            await serving;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
