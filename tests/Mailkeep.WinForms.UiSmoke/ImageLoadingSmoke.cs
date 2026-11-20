using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

/// <summary>Actual saved permissions and decoded WebView pictures, including a held permitted download.</summary>
internal static class ImageLoadingSmoke
{
    internal static void Run(string app, string root, MailkeepServices services, string? screenshots)
    {
        using var allowed = new ImageServer();
        using var ask = new ImageServer();
        using var never = new ImageServer();
        var html = $"<body style='background:#eeeeee;font:12px Tahoma'><h3>Saved image permissions</h3><p>Allowed picture loads before the slow picture finishes.</p><img id='fast' width='60' height='60' src='{allowed.Origin}/fast'><img id='slow' width='60' height='60' src='{allowed.Origin}/slow'><p>Ask waits for a click; Never remains blocked.</p><img id='ask' src='{ask.Origin}/fast'><img id='never' src='{never.Origin}/fast'></body>";
        services.Accounts.Save([]);
        services.Database.SetSetting("sync.auto", "off");
        var id = services.Database.Upsert(new MessageInput("live", MailContract.LocalCopyAccount, "inbox", "copy:image-loading", null, 0,
            "Successful image loading fixture", "fixture@example.invalid", "Fixture", [], DateTimeOffset.UtcNow, 100, true, 0, "Image fixture", html, null));
        var content = services.Database.GetMessage(id)!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
                Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
                SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
                var directory = Path.GetDirectoryName(Path.GetFullPath(app))!;
                var loader = Path.Combine(directory, "WebView2Loader.dll");
                if (!File.Exists(loader)) loader = Path.Combine(directory, "runtimes", "win-x64", "native", "WebView2Loader.dll");
                System.Runtime.InteropServices.NativeLibrary.Load(loader);
                var assembly = Assembly.LoadFrom(Path.Combine(directory, "Mailkeep.WinForms.dll"));
                var type = assembly.GetType("Mailkeep.WinForms.MainForm", true)!;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                using var main = (Forms.Form)Activator.CreateInstance(type, [root])!;
                main.Show();
                Until(() => (bool)type.GetField("_ready", flags)!.GetValue(main)!);
                type.GetField("_currentContent", flags)!.SetValue(main, content);
                type.GetMethod("ShowBody", flags)!.Invoke(main, [content]);
                var permissions = type.GetField("_imagePermissions", flags)!.GetValue(main)!;
                permissions.GetType().GetMethod("Save")!.Invoke(permissions, [new Dictionary<string, string>
                { [allowed.Origin] = "Always allow", [ask.Origin] = "Ask", [never.Origin] = "Never allow" }]);
                var view = type.GetField("_html", flags)!.GetValue(main)!;
                Until(() => (bool)view.GetType().GetProperty("DocumentLoaded")!.GetValue(view)!);
                var core = view.GetType().GetProperty("CoreWebView2")!.GetValue(view)!;
                string Script(string expression)
                {
                    var task = (Task<string>)core.GetType().GetMethod("ExecuteScriptAsync")!.Invoke(core, [expression])!;
                    Until(() => task.IsCompleted);
                    return task.GetAwaiter().GetResult();
                }
                bool Loaded(string name) => Script($"document.getElementById('{name}').naturalWidth > 0") == "true";
                Until(() => Loaded("fast"));
                Check(!allowed.Release.Task.IsCompleted, "held allowed image unexpectedly released");
                Check(!Loaded("slow") && !Loaded("ask") && !Loaded("never"), "undecided or held images loaded without permission");
                Check(ask.Requests == 0 && never.Requests == 0, "blocked origins were contacted");
                if (screenshots is not null)
                {
                    Directory.CreateDirectory(screenshots);
                    using var bitmap = new System.Drawing.Bitmap(main.Width, main.Height);
                    using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.CopyFromScreen(main.Location, System.Drawing.Point.Empty, bitmap.Size);
                    bitmap.Save(Path.Combine(screenshots, "image-auto-allowed.png"));
                }
                allowed.Release.TrySetResult();
                Until(() => Loaded("slow"));
                var bar = (Forms.Control)type.GetField("_remoteImages", flags)!.GetValue(main)!;
                ((Forms.Button)bar.GetType().GetField("_load", flags)!.GetValue(bar)!).PerformClick();
                Until(() => Loaded("ask"));
                Check(!Loaded("never") && never.Requests == 0, "one-time load overrode Never");
                Check(services.Database.GetSetting("images.site_policies")!.Contains("Ask"), "one-time load saved Allow");
                Console.WriteLine("PASS image-loading saved-Allow=decoded-before-slow Ask=click-only Never=no-requests image-by-image=rendered sends=0");

                void Until(Func<bool> condition)
                {
                    var watch = Stopwatch.StartNew();
                    while (!condition()) { Check(watch.ElapsedMilliseconds < 8000, "Image loading fixture timed out"); Forms.Application.DoEvents(); Thread.Sleep(10); }
                }
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(50)), "Image loading fixture timed out");
        if (failure is not null) throw new InvalidOperationException("Image loading failed", failure);
    }

    private sealed class ImageServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public string Origin { get; }

        public ImageServer()
        {
            _listener.Start();
            Origin = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        _ = Task.Run(async () =>
                        {
                            using (client)
                            {
                                try
                                {
                                    await using var stream = client.GetStream();
                                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                                    var request = await reader.ReadLineAsync();
                                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                                    Interlocked.Increment(ref _requests);
                                    if (request?.Contains("/slow ") == true) await Release.Task;
                                    var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
                                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {png.Length}\r\nConnection: close\r\n\r\n"));
                                    await stream.WriteAsync(png);
                                }
                                catch (IOException) { }
                            }
                        });
                    }
                }
                catch (OperationCanceledException) { }
                catch (SocketException) { }
            });
        }

        public void Dispose() { Release.TrySetResult(); _stop.Cancel(); _listener.Stop(); }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
