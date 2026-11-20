using System.Diagnostics;
using System.Reflection;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using MimeKit;
using Forms = System.Windows.Forms;

/// <summary>Exercise saved domain decisions in the real reader, using isolated mail and no remote service.</summary>
internal static class ImagePermissionBarSmoke
{
    internal static async Task Run(string app, string root, MailkeepServices services, string? screenshots)
    {
        const string first = "http://127.0.0.1:1", second = "http://127.0.0.1:2";
        using var mime = new MimeMessage
        {
            Subject = "Image permission fixture",
            Body = new TextPart("html") { Text = $"<body style='background:#eeeeee;font:12px Tahoma'><p>Decided image domains should leave this message unobstructed.</p><img src='{first}/logo.png' alt='Allowed logo'><img src='{second}/pixel.png' alt='Blocked image'><p>Message content stays in its normal reading space.</p></body>" }
        };
        mime.From.Add(MailboxAddress.Parse("sender@example.invalid"));
        using var raw = new MemoryStream(); mime.WriteTo(raw); raw.Position = 0;
        var id = await services.Database.UpsertMimeMessageAsync(MailContract.LiveSource, "Local inbox", LocalFolderKinds.Inbox,
            "image-permission-fixture", null, 0, mime, raw, true);
        var content = services.Database.GetMessage(id)!;
        services.Database.SetSetting("sync.auto", "off");
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
                var bar = (Forms.Control)type.GetField("_remoteImages", flags)!.GetValue(main)!;
                var html = (Forms.Control)type.GetField("_html", flags)!.GetValue(main)!;
                var permissions = type.GetField("_imagePermissions", flags)!.GetValue(main)!;
                void Save(string a, string b)
                {
                    permissions.GetType().GetMethod("Save")!.Invoke(permissions,
                        [new Dictionary<string, string> { [first] = a, [second] = b }]);
                    Forms.Application.DoEvents();
                }
                Save("Ask", "Never allow");
                Check(bar.Visible, "unresolved Ask prompt missing");
                Save("Always allow", "Never allow");
                Check(!bar.Visible, "mixed saved decisions still show permissions prompt");
                var resolvedTop = html.PointToScreen(System.Drawing.Point.Empty).Y;
                Capture("image-domains-decided");
                Save("Never allow", "Never allow");
                Check(!bar.Visible, "all blocked domains still show prompt");
                Check(html.PointToScreen(System.Drawing.Point.Empty).Y == resolvedTop, "resolved domains changed reading position");
                Save("Always allow", "Always allow");
                Check(!bar.Visible, "all allowed domains still show prompt");
                type.GetMethod("ShowBody", flags)!.Invoke(main, [content]);
                Check(!bar.Visible && html.PointToScreen(System.Drawing.Point.Empty).Y == resolvedTop, "reopened decided email shifts reader");
                Save("Ask", "Never allow");
                Check(bar.Visible, "explicit Ask cannot be loaded once");
                var load = (Forms.Button)bar.GetType().GetField("_load", flags)!.GetValue(bar)!;
                load.PerformClick();
                Check(!bar.Visible, "load-once prompt did not dismiss");
                Check(services.Database.GetSetting("images.site_policies")!.Contains("Ask"), "one-time load changed saved permission");
                type.GetMethod("ShowBody", flags)!.Invoke(main, [content]);
                Check(bar.Visible, "reopening Ask email should require permission again");
                Capture("image-domains-ask");
                Console.WriteLine("PASS image-permission-bar allow=hidden never=hidden mixed=hidden ask=visible once=dismissed reopen=correct position=stable sends=0");

                void Capture(string name)
                {
                    if (screenshots is null) return;
                    Until(() => (bool)html.GetType().GetProperty("DocumentLoaded")!.GetValue(html)!);
                    Forms.Application.DoEvents();
                    Directory.CreateDirectory(screenshots);
                    using var bitmap = new System.Drawing.Bitmap(main.Width, main.Height);
                    using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.CopyFromScreen(main.Location, System.Drawing.Point.Empty, bitmap.Size);
                    bitmap.Save(Path.Combine(screenshots, name + ".png"));
                }
                void Until(Func<bool> condition)
                {
                    var clock = Stopwatch.StartNew();
                    while (!condition()) { Check(clock.ElapsedMilliseconds < 20000, "Image permissions fixture timed out"); Forms.Application.DoEvents(); Thread.Sleep(10); }
                }
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(75)), "Image permissions fixture timed out");
        if (failure is not null) throw new InvalidOperationException("Image permissions UI failed", failure);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
