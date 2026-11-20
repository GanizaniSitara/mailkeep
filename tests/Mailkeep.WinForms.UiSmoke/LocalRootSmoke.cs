using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

/// <summary>Proves the Local Mail root is a container, while ordinary folder contents survive selection and restart.</summary>
internal static class LocalRootSmoke
{
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr device, uint flags);

    internal static int Run(string app, string? screenshots)
    {
        if (!Path.GetFullPath(app).StartsWith(@"C:\mk-smoke\app\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("local-root runs only in the disposable VM deployment.");
        var root = Path.Combine(Path.GetTempPath(), "mailkeep-local-root", Guid.NewGuid().ToString("N"));
        var services = new MailkeepServices(root);
        services.Database.Initialize();
        services.Database.SetSetting("sync.auto", "off");
        services.Accounts.Save([new MailAccount("local-fixture", "custom", "fixture@example.invalid", "INBOX", "inbox",
            "example.invalid", null, "secret", new KeepAliveSettings(), MailboxModes.LocalKeep,
            new IncomingServerSettings(MailProtocols.Pop3, "example.invalid", 995))]);
        var filed = services.Database.CreateLocalFolder("Filed fixture", null);
        foreach (var folder in new[] { LocalFolderKinds.Inbox, LocalFolderKinds.Sent, LocalFolderKinds.Deleted, filed.Key })
            services.Database.Upsert(new MessageInput(MailContract.LiveSource, "local-fixture", folder, "fixture:" + folder, null, 0,
                "Fixture " + folder, "sender@example.invalid", "Fixture", ["fixture@example.invalid"],
                DateTimeOffset.Parse("2026-01-02T03:04:05Z"), 128, true, 0, "Preserved fixture body", null, null));
        services.Identities.Save([new SendingIdentity("fixture", null, "Fixture", "sender@example.invalid", null, true, "custom",
            new OutgoingServerSettings("127.0.0.1", 1), null, null, "fixture", SendingIdentityStates.Ready)]);
        services.Send.SaveDraft("fixture", ["recipient@example.invalid"], "Draft fixture", "Unsent draft");
        var before = System.Text.Json.JsonSerializer.Serialize(services.Database.ListMessages(limit: 100));
        Exception? failure = null;
        var thread = new Thread(() => { try { RunUi(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(75)), "local-root timed out");
        if (failure is not null) throw new InvalidOperationException("local-root failed", failure);
        Check(before == System.Text.Json.JsonSerializer.Serialize(services.Database.ListMessages(limit: 100)), "Folder messages changed");
        Check(services.Database.ListOutgoing(OutgoingStates.Draft).Count == 1, "Draft changed");
        Console.WriteLine("PASS local-root empty on selection/refresh/restart; preview cleared; Inbox/Sent/Deleted/filed/draft retained");
        return 0;

        void RunUi()
        {
            Forms.Application.EnableVisualStyles();
            Forms.Application.SetCompatibleTextRenderingDefault(false);
            Forms.Application.SetDefaultFont(new System.Drawing.Font("Tahoma", 8.25f));
            Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
            Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            var directory = Path.GetDirectoryName(Path.GetFullPath(app))!;
            var loader = Path.Combine(directory, "WebView2Loader.dll");
            if (!File.Exists(loader)) loader = Path.Combine(directory, "runtimes", "win-x64", "native", "WebView2Loader.dll");
            NativeLibrary.Load(loader);
            var type = Assembly.LoadFrom(Path.Combine(directory, "Mailkeep.WinForms.dll")).GetType("Mailkeep.WinForms.MainForm", true)!;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            for (var run = 0; run < 2; run++)
            {
                using var main = (Forms.Form)Activator.CreateInstance(type, [root])!;
                T Field<T>(string name) => (T)type.GetField(name, flags)!.GetValue(main)!;
                void Until(Func<bool> condition)
                {
                    var timer = Stopwatch.StartNew();
                    while (!condition()) { Check(timer.Elapsed.TotalSeconds < 15, "UI did not settle"); Forms.Application.DoEvents(); Thread.Sleep(10); }
                }
                main.Show();
                Until(() => Field<bool>("_ready"));
                var tree = Field<Forms.TreeView>("_localTree");
                var list = Field<Forms.ListView>("_messages");
                if (run == 1) AssertEmpty();
                foreach (var key in new[] { "inbox", "sent", "deleted", filed.Key, "drafts" })
                {
                    tree.SelectedNode = tree.Nodes.Find("LocalFolder:" + key, true).Single();
                    Until(() => list.VirtualListSize == 1 && Field<Forms.Control>("_folderBand").Text != "All Local Mail");
                    tree.SelectedNode = tree.Nodes.Find("AllLocalMail", true).Single();
                    Until(() => Field<Forms.Control>("_folderBand").Text == "All Local Mail");
                    AssertEmpty();
                }
                type.GetMethod("LoadMessages", flags)!.Invoke(main, null);
                var paint = Stopwatch.StartNew();
                while (paint.ElapsedMilliseconds < 500) { Forms.Application.DoEvents(); Thread.Sleep(10); }
                AssertEmpty();
                main.Refresh();
                if (screenshots is not null)
                {
                    Directory.CreateDirectory(screenshots);
                    using var bitmap = new System.Drawing.Bitmap(main.Width, main.Height);
                    using var graphics = System.Drawing.Graphics.FromImage(bitmap);
                    var device = graphics.GetHdc();
                    try { Check(PrintWindow(main.Handle, device, 2), "capture failed"); }
                    finally { graphics.ReleaseHdc(device); }
                    bitmap.Save(Path.Combine(screenshots, "local-root-empty-" + run + ".png"));
                }
                main.Close();
                void AssertEmpty()
                {
                    Check(list.VirtualListSize == 0 && Field<IReadOnlyList<object>>("_rows").Count == 0, "Root shows messages");
                    Check(string.IsNullOrEmpty(Field<Forms.RichTextBox>("_body").Text), "Root retained message body");
                    Check(Field<Forms.Label>("_subject").Text == "Select a message to read it", "Root retained message subject");
                }
            }
        }
    }
    private static void Check(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
}
