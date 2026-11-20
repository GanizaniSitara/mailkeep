using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

/// <summary>Held loopback pictures must not delay submitting readable mail or replace a newer message.</summary>
internal static class PreviewLatencySmoke
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static async Task Run(string app, string root, MailkeepServices services)
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var origin = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlers = new List<Task>();
        var requested = 0;
        var replied = 0;
        var serving = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var client = await server.AcceptTcpClientAsync();
                    handlers.Add(Task.Run(async () =>
                    {
                        using (client)
                        {
                            await using var stream = client.GetStream();
                            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                            Interlocked.Increment(ref requested);
                            await release.Task;
                            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
                            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {png.Length}\r\nConnection: close\r\n\r\n"));
                            await stream.WriteAsync(png);
                            Interlocked.Increment(ref replied);
                        }
                    }));
                }
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        });
        var directory = Path.GetDirectoryName(Path.GetFullPath(app))!;
        var loader = Path.Combine(directory, "WebView2Loader.dll");
        if (!File.Exists(loader)) loader = Path.Combine(directory, "runtimes", "win-x64", "native", "WebView2Loader.dll");
        System.Runtime.InteropServices.NativeLibrary.Load(loader);
        var assembly = Assembly.LoadFrom(Path.Combine(directory, "Mailkeep.WinForms.dll"));
        var mainType = assembly.GetType("Mailkeep.WinForms.MainForm", true)!;
        var htmlType = assembly.GetType("Mailkeep.WinForms.HtmlView", true)!;
        Exception? failure = null;
        long elapsed = 0;
        services.Database.SetSetting("images.site_policies", JsonSerializer.Serialize(new Dictionary<string, string> { [origin] = "Always allow" }));
        services.Database.SetSetting("sync.auto", "off");
        var thread = new Thread(() =>
        {
            try
            {
                Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
                Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
                SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
                using var main = (Forms.Form)Activator.CreateInstance(mainType, [root])!;
                var view = mainType.GetField("_html", Flags)!.GetValue(main)!;
                string? Pending() => (string?)htmlType.GetField("_pending", Flags)!.GetValue(view);
                var content = services.Database.GetMessage(services.Database.ListMessages(account: "local-fixture", limit: 1)[0].Id)!;
                var html = "<p>Readable immediately</p>" + new string(' ', 60_000)
                    + string.Concat(Enumerable.Range(0, 12).Select(index => $"<img src='{origin}/{index}.png'>"));
                content = content with { BodyHtml = html };
                mainType.GetField("_currentContent", Flags)!.SetValue(main, content);
                var clock = Stopwatch.StartNew();
                mainType.GetMethod("ShowBody", Flags)!.Invoke(main, [content]);
                elapsed = clock.ElapsedMilliseconds;
                Check(Pending()?.Contains("Readable immediately") == true, "message waits for remote pictures before submitting its body");
                Check(elapsed < 150, "picture-rich body submission took " + elapsed + " ms");
                Until(() => Volatile.Read(ref requested) > 0);
                Check(!release.Task.IsCompleted && replied == 0, "slow picture fixture did not hold downloads");
                var next = content with { BodyHtml = "<p>Newer message stays visible</p>" };
                mainType.GetField("_currentContent", Flags)!.SetValue(main, next);
                mainType.GetMethod("ShowBody", Flags)!.Invoke(main, [next]);
                release.TrySetResult();
                Until(() => Volatile.Read(ref replied) == 12);
                for (var turn = 0; turn < 10; turn++) { Forms.Application.DoEvents(); Thread.Sleep(10); }
                Check(Pending()?.Contains("Newer message stays visible") == true, "late pictures replaced newer mail");
                mainType.GetField("_currentContent", Flags)!.SetValue(main, content);
                mainType.GetMethod("ShowBody", Flags)!.Invoke(main, [content]);
                Until(() => Pending()?.Contains("data:image/png;base64,") == true);
                Check(requested == 12, "cached pictures fetched again");

                void Until(Func<bool> condition)
                {
                    var wait = Stopwatch.StartNew();
                    while (!condition()) { Check(wait.ElapsedMilliseconds < 8000, "preview fixture did not settle"); Forms.Application.DoEvents(); Thread.Sleep(10); }
                }
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        try
        {
            thread.Start();
            Check(thread.Join(TimeSpan.FromSeconds(25)), "preview latency fixture timed out");
            if (failure is not null) throw new InvalidOperationException("preview latency failed", failure);
            Console.WriteLine($"PASS preview-submit body-submitted={elapsed}ms downloads=held stale=ignored cache=reused");
            await CheckPictureDom(htmlType, root);
            Console.WriteLine($"PASS preview-latency body-submitted={elapsed}ms downloads=held stale=ignored cache=reused DOM=in-place scroll=preserved navigation=guarded");
        }
        finally
        {
            release.TrySetResult(); server.Stop(); await serving; await Task.WhenAll(handlers);
        }
    }

    private static async Task CheckPictureDom(Type htmlType, string root)
    {
        var brave = new[] { @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe", @"C:\Program Files (x86)\BraveSoftware\Brave-Browser\Application\brave.exe" }.FirstOrDefault(File.Exists);
        Check(brave is not null, "existing Brave browser required for isolated headless DOM verification");
        var path = Path.Combine(root, "picture-dom.html");
        var uri = new Uri(path).AbsoluteUri;
        const string data = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        var body = "<body><a href='https://destination.example.invalid/path'>A link</a><div id='reader' style='height:40px;overflow:auto'><div style='height:1000px'>Readable mail</div></div>"
            + "<img id='image' src='http://127.0.0.1:1/image'><source srcset='http://127.0.0.1:1/image 1x'><table background='http://127.0.0.1:1/image'></table><div style='background-image:url(http://127.0.0.1:1/image)'></div></body>";
        var updated = body.Replace("http://127.0.0.1:1/image", data, StringComparison.Ordinal);
        string Script(string document, string target) => (string)htmlType.GetMethod("PictureUpdateScript", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [document, target])!;
        var checks = "const held = document.getElementById('reader'); held.scrollTop = 120; const position = held.scrollTop; const image = document.getElementById('image');"
            + Script(updated, uri)
            + "if (image !== document.getElementById('image') || !image.src.startsWith('data:') || document.getElementById('reader').scrollTop !== position || document.querySelector('a').getAttribute('href') !== 'https://destination.example.invalid/path' || !document.querySelector('source').getAttribute('srcset').endsWith(' 1x')) throw new Error('DOM/scroll/link update failed');"
            + Script(body, "file:///different-message.html")
            + "if (!image.src.startsWith('data:')) throw new Error('stale navigation changed pictures'); document.body.dataset.pictureUpdateResult = 'passed';";
        // Only the fixture's trusted verification script is executable. External resources stay blocked.
        await File.WriteAllTextAsync(path, "<!doctype html><meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; script-src 'unsafe-inline'\">" + body + "<script>" + checks + "</script>");
        var start = new ProcessStartInfo(brave!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var profile = Path.Combine(root, "headless-profile");
        foreach (var argument in new[] { "--headless=new", "--disable-gpu", "--disable-crash-reporter", "--disable-breakpad", "--no-first-run", "--no-default-browser-check", "--disable-background-networking", "--disable-component-update", "--disable-sync", "--disable-extensions", "--user-data-dir=" + profile, "--remote-debugging-port=0", uri }) start.ArgumentList.Add(argument);
        using var browser = Process.Start(start)!;
        var output = browser.StandardOutput.ReadToEndAsync();
        var errors = browser.StandardError.ReadToEndAsync();
        try
        {
            var portFile = Path.Combine(profile, "DevToolsActivePort");
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(portFile))
            {
                Check(!browser.HasExited && deadline.ElapsedMilliseconds < 15000, "headless debugger did not start");
                await Task.Delay(100);
            }
            var port = (await File.ReadAllLinesAsync(portFile))[0];
            using var http = new HttpClient();
            using var pages = JsonDocument.Parse(await http.GetStringAsync("http://127.0.0.1:" + port + "/json/list"));
            var page = pages.RootElement.EnumerateArray().First(entry => entry.GetProperty("type").GetString() == "page");
            using var socket = new ClientWebSocket();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(new Uri(page.GetProperty("webSocketDebuggerUrl").GetString()!), timeout.Token);
            var id = 0;
            while (true)
            {
                var command = JsonSerializer.Serialize(new { id = ++id, method = "Runtime.evaluate", @params = new { expression = "document.body && document.body.dataset.pictureUpdateResult", returnByValue = true } });
                await socket.SendAsync(Encoding.UTF8.GetBytes(command), WebSocketMessageType.Text, true, timeout.Token);
                JsonDocument response;
                while (true)
                {
                    using var message = new MemoryStream();
                    var buffer = new byte[4096]; WebSocketReceiveResult part;
                    do { part = await socket.ReceiveAsync(buffer, timeout.Token); message.Write(buffer, 0, part.Count); } while (!part.EndOfMessage);
                    response = JsonDocument.Parse(message.ToArray());
                    if (response.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id) break;
                    response.Dispose();
                }
                using (response)
                {
                    var result = response.RootElement.GetProperty("result").GetProperty("result");
                    if (result.TryGetProperty("value", out var value) && value.GetString() == "passed") break;
                }
                await Task.Delay(100, timeout.Token);
            }
        }
        finally
        {
            if (!browser.HasExited) { browser.Kill(entireProcessTree: true); await browser.WaitForExitAsync(); }
        }
        await output; await errors;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
