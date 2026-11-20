using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

/// <summary>Sidebar divider and mailbox selection survive resize, restart and a removed account in an isolated store.</summary>
internal static class SidebarStateSmoke
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(string app, string root, MailkeepServices services)
    {
        services.Database.SetSetting("sync.auto", "off");
        Exception? failure = null;
        var thread = new Thread(() => { try { RunUi(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(60)), "sidebar smoke timed out");
        if (failure is not null) throw new InvalidOperationException("sidebar smoke failed", failure);
        Console.WriteLine("PASS sidebar-state divider=resize/restart retained selection=connected/local retained removed-account=Inbox fallback save=before-close");

        void RunUi()
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
            using (var main = Open())
            {
                var split = Field<Forms.SplitContainer>(main, "_folderSplit");
                split.SplitterDistance = 240; main.Height += 100; Pump(100);
                Check(split.SplitterDistance == 240, "divider moved when window resized");
                Field<Dictionary<string, DateTimeOffset>>(main, "_accountChecked")["connected-fixture"] = DateTimeOffset.UtcNow;
                var accounts = Field<Forms.ListView>(main, "_accounts");
                accounts.Items.Cast<Forms.ListViewItem>().Single(item => ((MailAccount)item.Tag!).Name == "connected-fixture").Selected = true;
                Pump(1300);
                using var saved = JsonDocument.Parse(services.Database.GetSetting("window.layout")!);
                Check(saved.RootElement.GetProperty("LocalSplit").GetInt32() == 240, "divider not saved before close");
                Check(saved.RootElement.GetProperty("SelectedAccount").GetString() == "connected-fixture", "account not saved before close");
                main.Close();
            }
            using (var restored = Open())
            {
                Check(Field<Forms.SplitContainer>(restored, "_folderSplit").SplitterDistance == 240, "divider changed on restart");
                Check(Field<MailAccount>(restored, "_selectedAccount").Name == "connected-fixture", "connected account not restored");
                var accounts = Field<Forms.ListView>(restored, "_accounts");
                Check(accounts.SelectedItems.Count == 1 && ((MailAccount)accounts.SelectedItems[0].Tag!).Name == "connected-fixture", "connected row not highlighted");
                var tree = Field<Forms.TreeView>(restored, "_localTree");
                tree.SelectedNode = tree.Nodes.Find("LocalFolder:" + LocalFolderKinds.Sent, true).Single();
                Pump(1300); restored.Close();
            }
            using (var restored = Open())
            {
                Check(Field<Forms.TreeView>(restored, "_localTree").SelectedNode?.Name == "LocalFolder:" + LocalFolderKinds.Sent, "local folder not restored");
                Check(Field<Forms.ListView>(restored, "_accounts").SelectedItems.Count == 0, "ghost connected selection");
                restored.Close();
            }
            var layout = System.Text.Json.Nodes.JsonNode.Parse(services.Database.GetSetting("window.layout")!)!;
            layout["SelectedAccount"] = "removed-fixture"; layout["SelectedFolder"] = "local:removed-folder";
            services.Database.SetSetting("window.layout", layout.ToJsonString());
            using (var restored = Open())
            {
                Check(Field<Forms.TreeView>(restored, "_localTree").SelectedNode?.Name == "LocalFolder:" + LocalFolderKinds.Inbox, "missing mailbox did not fall back to Inbox");
                restored.Close();
            }

            Forms.Form Open()
            {
                var form = (Forms.Form)Activator.CreateInstance(type, [root])!; form.Show();
                var clock = Stopwatch.StartNew();
                while (!Field<bool>(form, "_ready")) { Check(clock.Elapsed < TimeSpan.FromSeconds(12), "sidebar main not ready"); Pump(10); }
                Pump(150); return form;
            }
            T Field<T>(Forms.Form form, string name) => (T)type.GetField(name, Flags)!.GetValue(form)!;
        }
    }
    private static void Pump(int ms) { var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < ms) { Forms.Application.DoEvents(); Thread.Sleep(10); } }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
