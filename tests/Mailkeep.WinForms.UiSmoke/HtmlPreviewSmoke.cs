using System.Diagnostics;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

/// <summary>Verify the actual script-disabled WebView2 reader and capture its hovered URL.</summary>
internal static class HtmlPreviewSmoke
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);

    internal static void Run(string app, string root, string? screenshots)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
                using var tracking = new TcpListener(IPAddress.Loopback, 0);
                tracking.Start();
                var trackingPort = ((IPEndPoint)tracking.LocalEndpoint).Port;
                var contacted = tracking.AcceptTcpClientAsync();
                _ = contacted.ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully) task.Result.Dispose(); else _ = task.Exception;
                }, TaskScheduler.Default);
                SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
                var directory = Path.GetDirectoryName(Path.GetFullPath(app))!;
                var loader = Path.Combine(directory, "WebView2Loader.dll");
                if (!File.Exists(loader)) loader = Path.Combine(directory, "runtimes", "win-x64", "native", "WebView2Loader.dll");
                NativeLibrary.Load(loader);
                var assembly = Assembly.LoadFrom(Path.Combine(directory, "Mailkeep.WinForms.dll"));
                var type = assembly.GetType("Mailkeep.WinForms.HtmlView", true)!;
                var opened = 0;
                using var view = (Forms.Control)Activator.CreateInstance(type, [new Action<string?>(_ => opened++)])!;
                using var form = new Forms.Form { Text = "Isolated Mailkeep HTML preview", Width = 1000, Height = 700, StartPosition = Forms.FormStartPosition.Manual, Location = new System.Drawing.Point(80, 140) };
                form.Controls.Add(view);
                form.Show();
                var start = (Task)type.GetMethod("StartAsync")!.Invoke(view, [Path.Combine(root, "webview-fixture")])!;
                Until(() => start.IsCompleted);
                start.GetAwaiter().GetResult();
                Check(!(bool)type.GetProperty("Failed")!.GetValue(view)!, "WebView2 initialization failed");
                var core = type.GetProperty("CoreWebView2")!.GetValue(view)!;
                var settings = core.GetType().GetProperty("Settings")!.GetValue(core)!;
                Check((bool)settings.GetType().GetProperty("IsStatusBarEnabled")!.GetValue(settings)!, "hover status disabled");
                Check(!(bool)settings.GetType().GetProperty("IsScriptEnabled")!.GetValue(settings)!, "email scripts enabled");
                string Script(string script)
                {
                    var task = (Task<string>)core.GetType().GetMethod("ExecuteScriptAsync")!.Invoke(core, [script])!;
                    Until(() => task.IsCompleted);
                    return task.GetAwaiter().GetResult();
                }
                string Safe(string html) => (string)assembly.GetType("Mailkeep.WinForms.MainForm", true)!.GetMethod("SafeHtml", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(null, [html, null])!;
                var body = "<body style='background:#dedede;font:16px Tahoma'><a id='link' href='https://destination.example.invalid/jobs/123'>Hover this job link</a><p>Readable before pictures finish.</p><img id='picture' width='80' height='80' src='http://127.0.0.1:1/picture'><div style='height:1400px'>Long message</div></body>"
                    + $"<link rel=dns-prefetch href='//127.0.0.1:{trackingPort}'><link rel=preconnect href='http://127.0.0.1:{trackingPort}'><link rel=stylesheet href='http://127.0.0.1:{trackingPort}/tracker.css'>";
                type.GetMethod("Show", [typeof(string)])!.Invoke(view, [Safe(body)]);
                view.Visible = true;
                Until(() => (bool)type.GetProperty("DocumentLoaded")!.GetValue(view)!);
                Script("window.savedPicture=document.getElementById('picture'); window.scrollTo(0,150); window.savedScroll=scrollY");
                const string image = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
                type.GetMethod("UpdatePictures")!.Invoke(view, [Safe(body.Replace("http://127.0.0.1:1/picture", image))]);
                Until(() => Script("document.getElementById('picture').src.startsWith('data:')") == "true");
                Check(Script("savedPicture === document.getElementById('picture') && scrollY === savedScroll") == "true", "picture update replaced page or scroll");
                Script("window.scrollTo(0,0)");
                var point = view.PointToScreen(new System.Drawing.Point(70, 18));
                SetCursorPos(point.X, point.Y);
                var hold = Stopwatch.StartNew();
                while (hold.ElapsedMilliseconds < 1500) { Forms.Application.DoEvents(); Thread.Sleep(20); }
                Check(opened == 0, "hover opened an external link");
                Check(Script("document.querySelectorAll('link').length") == "0", "resource-hint elements survived sanitization");
                Check(Script("document.querySelector('meta[http-equiv=\"x-dns-prefetch-control\"]').content") == "\"off\"", "DNS prefetch was not disabled");
                Check(!contacted.IsCompleted, "a stripped resource hint contacted the loopback tracker");
                if (screenshots is not null)
                {
                    Directory.CreateDirectory(screenshots);
                    using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
                    using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.CopyFromScreen(form.Location, System.Drawing.Point.Empty, form.Size);
                    bitmap.Save(Path.Combine(screenshots, "html-hover.png"));
                }
                void Show(string html) => type.GetMethod("Show", [typeof(string)])!.Invoke(view, [Safe(html)]);
                bool Loaded() => (bool)type.GetProperty("DocumentLoaded")!.GetValue(view)!;
                // Large mail must stay readable. A newer selection
                // supersedes any pending navigation, including the large message just submitted.
                Script("window.previousLetterMarker = true");
                Show("<h1>Large letter</h1><!--" + new string('x', 2_200_000) + "-->");
                Until(() => Loaded() && Script("document.body.innerText.includes('Large letter')") == "true");
                Check(Script("typeof previousLetterMarker") == "\"undefined\"", "new letter retained previous document state");
                Show("<h1>Superseded letter</h1>");
                Show("<h1>Latest letter</h1>");
                Until(() => Loaded() && Script("document.body.innerText.includes('Latest letter')") == "true");
                var samples = new List<double>();
                var domSamples = new List<double>();
                for (var i = 0; i < 8; i++)
                {
                    var clock = Stopwatch.StartNew();
                    Show("<p>Selection " + i + "</p>");
                    Until(() => Script("document.body.innerText === 'Selection " + i + "'") == "true");
                    domSamples.Add(clock.Elapsed.TotalMilliseconds);
                    Until(Loaded);
                    samples.Add(clock.Elapsed.TotalMilliseconds);
                }
                var median = samples.Order().ElementAt(samples.Count / 2);
                Console.WriteLine("READER LATENCY " + System.Text.Json.JsonSerializer.Serialize(new { samplesMs = samples, medianMs = median, domSamplesMs = domSamples }));
                Check(opened == 0, "an internal refresh opened an external browser");
                Console.WriteLine("PASS html-preview runtime=WebView2 email-scripts=disabled pictures=in-place scroll=preserved hover-status=enabled external-opens=0");

                void Until(Func<bool> condition)
                {
                    var wait = Stopwatch.StartNew();
                    while (!condition()) { Check(wait.ElapsedMilliseconds < 20000, "WebView2 fixture timed out"); Forms.Application.DoEvents(); Thread.Sleep(10); }
                }
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(60)), "HTML fixture timed out");
        if (failure is not null) throw new InvalidOperationException("HTML preview failed", failure);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
