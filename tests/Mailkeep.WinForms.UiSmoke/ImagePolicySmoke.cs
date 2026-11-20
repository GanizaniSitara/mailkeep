using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

/// <summary>Per-site permissions and redirects use disposable mail and three loopback HTTP origins.</summary>
internal static class ImagePolicySmoke
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static void Run(string app, string root, MailkeepServices services)
    {
        using var pictures = new Images(); using var ask = new Images(); using var tracker = new Images();
        services.Accounts.Save(services.Accounts.Load().Where(account => account.Name == "local-fixture").ToArray());
        services.Database.SetSetting("sync.auto", "off");
        var legacy = "https://old-tracker.example.invalid";
        services.Database.SetSetting("images.allowed_origins", JsonSerializer.Serialize(new[] { pictures.Origin, legacy }));
        Exception? failure = null;
        var thread = new Thread(() => { try { CheckUi(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(90)), "image-policy smoke timed out");
        if (failure is not null) throw new InvalidOperationException("image-policy smoke failed", failure);
        Console.WriteLine("PASS image-policies legacy=preserved/prunable choices=allow,ask,never cancel=no-write persisted=yes main=auto/once reader=auto/once never=no-requests redirects=blocked cache=policy-aware");

        void CheckUi()
        {
            Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
            Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            var directory = Path.GetDirectoryName(Path.GetFullPath(app))!;
            var loader = Path.Combine(directory, "WebView2Loader.dll");
            if (!File.Exists(loader)) loader = Path.Combine(directory, "runtimes", "win-x64", "native", "WebView2Loader.dll");
            System.Runtime.InteropServices.NativeLibrary.Load(loader);
            var assembly = Assembly.LoadFrom(Path.Combine(directory, "Mailkeep.WinForms.dll"));
            var mainType = assembly.GetType("Mailkeep.WinForms.MainForm", true)!;
            var permissionType = assembly.GetType("Mailkeep.WinForms.RemoteImagePermissions", true)!;
            var formType = assembly.GetType("Mailkeep.WinForms.ImageSitesForm", true)!;
            var fetch = assembly.GetType("Mailkeep.WinForms.RemoteImageFetcher", true)!.GetMethod("InlineAsync", BindingFlags.Static | BindingFlags.Public)!;
            var permissions = Activator.CreateInstance(permissionType, [services.Database])!;
            string Policy(string origin) => (string)permissionType.GetMethod("Policy")!.Invoke(permissions, [origin])!;
            Check(Policy(pictures.Origin) == "Always allow" && Policy(legacy) == "Always allow", "legacy allow list lost");
            Check(Policy(ask.Origin) == "Ask", "new sites not Ask by default");
            var suggestions = assembly.GetType("Mailkeep.WinForms.TrackerSuggestions", true)!.GetMethod("Match", BindingFlags.Static | BindingFlags.Public)!;
            string? Match(string origin) => (string?)suggestions.Invoke(null, [origin]);
            Check(Match("https://DOUBLECLICK.net") == "doubleclick.net", "bundled tracker list missing known domain");
            Check(Match("https://pixel.doubleclick.net.:443") == "doubleclick.net", "subdomain/trailing-dot match failed");
            Check(Match("https://notdoubleclick.net") is null && Match("https://doubleclick.net.example.invalid") is null && Match(pictures.Origin) is null, "tracker matcher crossed domain boundaries or matched IP");
            var listed = "https://doubleclick.net";
            using (var preview = (Forms.Form)Activator.CreateInstance(formType, [permissions, new[] { listed, ask.Origin }])!)
            {
                string Selected(string site) => Controls(preview).OfType<Forms.RadioButton>().Single(choice => choice.AccessibleName?.EndsWith(" pictures from " + site) == true && choice.Checked).Text;
                void Click(string name) => Controls(preview).OfType<Forms.Button>().Single(button => button.AccessibleName == name).PerformClick();
                preview.Show();
                Check(Selected(listed) == "Never allow" && Policy(listed) == "Ask", "suggestion persisted without Save or default missing");
                Check(!Controls(preview).OfType<Forms.RadioButton>().Any(choice => choice.AccessibleName?.EndsWith(" pictures from " + pictures.Origin) == true), "another message's saved site leaked into editor");
                Choose(preview, ask.Origin, "Always allow"); Check(Selected(listed) == "Never allow", "radio groups not independent");
                Click("Permit all image sites"); Check(Controls(preview).OfType<Forms.RadioButton>().Where(choice => choice.Checked).All(choice => choice.Text == "Always allow"), "Permit all failed");
                Click("Use tracker suggestions"); Check(Selected(listed) == "Never allow" && Selected(ask.Origin) == "Always allow", "suggestions changed unlisted site");
                Click("Reject all image sites"); Check(Controls(preview).OfType<Forms.RadioButton>().Where(choice => choice.Checked).All(choice => choice.Text == "Never allow"), "Reject all failed");
                preview.Close(); Check(Policy(pictures.Origin) == "Always allow" && Policy(listed) == "Ask", "bulk Cancel persisted");
            }
            using (var cancel = (Forms.Form)Activator.CreateInstance(formType, [permissions, new[] { pictures.Origin, ask.Origin, tracker.Origin }])!)
            {
                cancel.Show(); Choose(cancel, pictures.Origin, "Never allow");
                cancel.DialogResult = Forms.DialogResult.Cancel; cancel.Close();
                Check(Policy(legacy) == "Always allow", "Cancel saved a choice");
            }
            using var main = (Forms.Form)Activator.CreateInstance(mainType, [root])!;
            main.Show(); Wait(() => (bool)Get(main, "_ready")!, "fixture main not ready");
            var shared = Get(main, "_imagePermissions")!;
            Edit(main, shared, [listed], dialog => Controls(dialog).OfType<Forms.Button>().Single(button => button.AccessibleName == "Permit all image sites").PerformClick());
            Check(Policy(listed) == "Always allow", "bulk Save not persisted");
            using (var saved = (Forms.Form)Activator.CreateInstance(formType, [permissions, new[] { listed }])!)
                Check(Controls(saved).OfType<Forms.RadioButton>().Single(choice => choice.AccessibleName == "Always allow pictures from " + listed).Checked, "tracker suggestion overwrote saved Allow");
            Edit(main, shared, [legacy], dialog => Choose(dialog, legacy, "Never allow"), save: false);
            Check(Policy(legacy) == "Always allow", "Cancel in site manager persisted a change");
            Edit(main, shared, new[] { pictures.Origin, ask.Origin, tracker.Origin, legacy }, dialog =>
            {
                Choose(dialog, pictures.Origin, "Always allow");
                Choose(dialog, ask.Origin, "Ask");
                Choose(dialog, tracker.Origin, "Never allow");
                Choose(dialog, legacy, "Never allow");
            });
            var fresh = Activator.CreateInstance(permissionType, [services.Database])!;
            Check((string)permissionType.GetMethod("Policy")!.Invoke(fresh, [legacy])! == "Never allow", "previously allowed tracker was not pruned");
            Check(Policy(ask.Origin) == "Ask" && Policy(tracker.Origin) == "Never allow", "per-site choices not persisted");
            var html = $"<p>Policy fixture</p><img src='{pictures.Origin}/auto.png'><img src='{ask.Origin}/ask.png'><img src='{tracker.Origin}/tracker.png'>";
            services.Database.Upsert(new MessageInput(MailContract.LiveSource, "local-fixture", LocalFolderKinds.Inbox, "policy-fixture", null, 0,
                "Image policy fixture", "sender@example.invalid", "Sender", [], DateTimeOffset.UtcNow, 123, false, 0, "Policy fixture", html, null));
            var summary = services.Database.ListMessages("local-fixture").Single(message => message.Subject == "Image policy fixture");
            var content = services.Database.GetMessage(summary.Id)!;
            mainType.GetField("_currentContent", Flags)!.SetValue(main, content);
            mainType.GetMethod("ShowBody", Flags)!.Invoke(main, [content]);
            var view = (Forms.Control)Get(main, "_html")!;
            Wait(() => pictures.Count("/auto.png") == 1 && Document(view).Contains("data:image/png"), "Always allow did not auto-load");
            Check(ask.Total == 0 && tracker.Total == 0, "Ask/Never made automatic requests");
            var bar = (Forms.Control)Get(main, "_remoteImages")!;
            Controls(bar).OfType<Forms.Button>().Single(button => button.AccessibleName == "Load blocked pictures").PerformClick();
            Wait(() => ask.Count("/ask.png") == 1, "Load once did not fetch Ask");
            Check(tracker.Total == 0 && Policy(ask.Origin) == "Ask", "Load once fetched Never or persisted Ask as Allow");
            Check(!bar.Visible, "Load once should dismiss the picture prompt; saved permissions remain in Options");
            Await((Task)mainType.GetMethod("OpenMessageAsync", Flags)!.Invoke(main, [summary.Id])!);
            using var reader = main.OwnedForms.Single(form => form.GetType().Name == "MessageWindow");
            var readerView = Controls(reader).Single(control => control.GetType().Name == "HtmlView");
            Wait(() => (bool)readerView.GetType().GetProperty("DocumentLoaded")!.GetValue(readerView)!, "reader not loaded");
            var readerLoad = Controls(reader).OfType<Forms.Button>().Single(button => button.AccessibleName == "Load blocked pictures");
            Check(readerLoad.Visible, "reader auto-loaded an Ask site"); readerLoad.PerformClick(); Pump(200);
            Check(tracker.Total == 0, "reader Load once requested Never");
            Edit(reader, shared, new[] { pictures.Origin, ask.Origin, tracker.Origin }, dialog => Choose(dialog, pictures.Origin, "Never allow"));
            Wait(() => Loaded(view) && Loaded(readerView) && !Document(view).Contains("data:image/png") && !Document(readerView).Contains("data:image/png"), "new Never policy did not refresh open views");
            Check(pictures.Count("/auto.png") == 1 && tracker.Total == 0, "revoked sites fetched again"); reader.Close();
            // A fetch already in flight must not restore pictures after a Never decision.
            Edit(main, shared, [pictures.Origin], dialog => Choose(dialog, pictures.Origin, "Always allow"));
            pictures.HoldPath = "/slow.png";
            var slow = content with { BodyHtml = $"<img src='{pictures.Origin}/slow.png'>" };
            mainType.GetField("_currentContent", Flags)!.SetValue(main, slow);
            mainType.GetMethod("ShowBody", Flags)!.Invoke(main, [slow]);
            Wait(() => pictures.Count("/slow.png") == 1, "slow fixture fetch not started");
            Edit(main, shared, [pictures.Origin], dialog => Choose(dialog, pictures.Origin, "Never allow"));
            pictures.Release.TrySetResult(); Wait(() => pictures.Responded.ContainsKey("/slow.png"), "slow response not released"); Pump(200);
            Wait(() => Loaded(view), "revoked slow message not rendered");
            Check(!Document(view).Contains("data:image/png"), "in-flight fetch restored newly blocked image");
            var optionsType = assembly.GetType("Mailkeep.WinForms.OptionsForm", true)!;
            using (var options = (Forms.Form)Activator.CreateInstance(optionsType, [services, false, "Alphabetical", null])!)
                Check(Controls(options).OfType<Forms.Button>().Any(button => button.AccessibleName == "Manage saved image sites"), "Options cannot manage saved sites");
            pictures.Redirect = tracker.Origin + "/redirected.png";
            var redirectHtml = $"<img src='{pictures.Origin}/redirect'>";
            Task<string> Inline(string source, string[] allowed) => (Task<string>)fetch.Invoke(null, [source, allowed, CancellationToken.None, null])!;
            Check(Await(Inline(redirectHtml, [pictures.Origin])) == redirectHtml && tracker.Total == 0, "redirect contacted an unallowed site");
            Check(Await(Inline(redirectHtml, [pictures.Origin, tracker.Origin])).Contains("data:image/png") && tracker.Total == 1, "allowed redirect not fetched");
            Check(Await(Inline(redirectHtml, [pictures.Origin])) == redirectHtml && tracker.Total == 1, "cached redirect ignored new policy");
            Check(Await(Inline($"<img src='{tracker.Origin}/direct.png'>", [pictures.Origin])).Contains(tracker.Origin) && tracker.Total == 1, "direct unallowed origin fetched");
            main.Close(); Pump(150);

            void Edit(Forms.Form owner, object permission, string[] sites, Action<Forms.Form> choose, bool save = true)
            {
                using var timer = new Forms.Timer { Interval = 40 }; Exception? error = null;
                timer.Tick += (_, _) =>
                {
                    var dialog = Forms.Application.OpenForms.Cast<Forms.Form>().FirstOrDefault(form => form.GetType() == formType && form.Visible);
                    if (dialog is null) return; timer.Stop();
                    try { choose(dialog); dialog.DialogResult = save ? Forms.DialogResult.OK : Forms.DialogResult.Cancel; }
                    catch (Exception failure) { error = failure; dialog.DialogResult = Forms.DialogResult.Cancel; }
                };
                timer.Start();
                Check((bool)formType.GetMethod("Edit", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, [owner, permission, sites])! == save, "site editor ignored Save/Cancel");
                if (error is not null) throw error;
            }
        }
    }
    private static string Document(Forms.Control view)
    {
        if (!(bool)view.GetType().GetProperty("DocumentLoaded")!.GetValue(view)!) return "pending";
        dynamic core = view.GetType().GetProperty("CoreWebView2")!.GetValue(view)!;
        return Await((Task<string>)core.ExecuteScriptAsync("document.documentElement.outerHTML"));
    }
    private static bool Loaded(Forms.Control view) => (bool)view.GetType().GetProperty("DocumentLoaded")!.GetValue(view)!;
    private static void Choose(Forms.Form dialog, string site, string policy) => Controls(dialog).OfType<Forms.RadioButton>().Single(control => control.AccessibleName == policy + " pictures from " + site).Checked = true;
    private static IEnumerable<Forms.Control> Controls(Forms.Control parent) => ComposeToolsSmoke.Controls(parent);
    private static object? Get(object target, string field) => target.GetType().GetField(field, Flags)!.GetValue(target);
    private static void Await(Task task) { Wait(() => task.IsCompleted, "async images timed out"); task.GetAwaiter().GetResult(); }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Pump(int ms) { var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < ms) { Forms.Application.DoEvents(); Thread.Sleep(10); } }
    private static void Wait(Func<bool> done, string error) { var clock = Stopwatch.StartNew(); while (!done()) { if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(error); Pump(10); } }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Images : IDisposable
    {
        private readonly TcpListener _server = new(IPAddress.Loopback, 0);
        private readonly Task _serving;
        private readonly ConcurrentDictionary<string, int> _requests = new();
        public string Origin { get; }
        public string? Redirect { get; set; }
        public string? HoldPath { get; set; }
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentDictionary<string, bool> Responded { get; } = new();
        public int Total => _requests.Values.Sum();
        public int Count(string path) => _requests.GetValueOrDefault(path);
        public Images()
        {
            _server.Start(); Origin = "http://127.0.0.1:" + ((IPEndPoint)_server.LocalEndpoint).Port;
            _serving = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using var client = await _server.AcceptTcpClientAsync(); await using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                        var first = await reader.ReadLineAsync(); while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                        var path = first!.Split(' ')[1]; _requests.AddOrUpdate(path, 1, (_, count) => count + 1);
                        if (path == "/redirect" && Redirect is { } target)
                            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: {target}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                        else
                        {
                            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
                            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {png.Length}\r\nConnection: close\r\n\r\n"));
                            if (path == HoldPath) await Release.Task;
                            await stream.WriteAsync(png); Responded[path] = true;
                        }
                    }
                }
                catch (ObjectDisposedException) { }
                catch (SocketException) { }
            });
        }
        public void Dispose() { Release.TrySetResult(); _server.Stop(); _serving.GetAwaiter().GetResult(); }
    }
}
