using System.Diagnostics;
using System.Reflection;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;
using Forms = System.Windows.Forms;

/// <summary>Slow private reads and writes must leave sidebar, folder commands and addressing responsive.</summary>
internal static class UiBlockingSmoke
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    internal static void Run(string app, string root, MailkeepServices services)
    {
        services.Database.SetSetting("sync.auto", "off");
        Exception? failure = null;
        var thread = new Thread(() => { try { RunUi(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(90)), "UI blocking test timed out");
        if (failure is not null) throw new InvalidOperationException("UI blocking test failed", failure);
        Console.WriteLine("PASS ui-blocking sidebar-read folder-create folder-rename address-book-read autocomplete-current-text close-pending-load");

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
            var mainType = assembly.GetType("Mailkeep.WinForms.MainForm", true)!;
            using var main = (Forms.Form)Activator.CreateInstance(mainType, [root])!;
            T MainField<T>(string name) => (T)mainType.GetField(name, Flags)!.GetValue(main)!;
            main.Show(); Until(() => MainField<bool>("_ready") && MainField<MessageContent?>("_currentContent") is not null);
            // A real exclusive transaction blocks database work without production hooks.
            using (var c = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False"))
            {
                c.Open(); using var q = c.CreateCommand(); q.CommandText = "PRAGMA journal_mode=DELETE"; q.ExecuteScalar();
            }
            HoldDatabase("sidebar read", () =>
            {
                var method = mainType.GetMethod("ReloadAccountsAsync", Flags);
                if (method is not null) return (Task)method.Invoke(main, [])!;
                mainType.GetMethod("ReloadAccounts", Flags)!.Invoke(main, [null]);
                return null; // older build: the read was synchronous
            });
            var html = MainField<Forms.Control>("_html");
            Until(() => (bool)html.GetType().GetProperty("IsReady", Flags)!.GetValue(html)!);
            var htmlContent = MainField<MessageContent>("_currentContent") with { BodyHtml = "<p>Responsive HTML fixture</p>" };
            mainType.GetField("_currentContent", Flags)!.SetValue(main, htmlContent);
            HoldDatabase("HTML permission read", () =>
            {
                mainType.GetMethod("ShowBody", Flags, null, [typeof(MessageContent)], null)!.Invoke(main, [htmlContent]);
                return null;
            });
            var sortType = mainType.GetNestedType("AccountSort", BindingFlags.NonPublic)!;
            var settings = assembly.GetType("Mailkeep.WinForms.SettingsWrites", true)!;
            HoldDatabase("preference writes", () =>
            {
                foreach (var name in new[] { "Volume", "New", "Alphabetical" })
                    mainType.GetMethod("SetAccountSort", Flags)!.Invoke(main, [Enum.Parse(sortType, name)]);
                return (Task)settings.GetMethod("DrainAsync", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [MainField<MailkeepServices>("_services").Database])!;
            });
            Check(services.Database.GetSetting("AccountSort") == "Alphabetical", "queued preference writes did not preserve the newest order");
            var optionsType = assembly.GetType("Mailkeep.WinForms.OptionsForm", true)!;
            using (var options = (Forms.Form)Activator.CreateInstance(optionsType, [services, false, "Alphabetical", null])!)
            {
                options.Show(main);
                HoldDatabase("Options save", () =>
                {
                    var ok = ComposeToolsSmoke.Controls(options).OfType<Forms.Button>().Single(button => button.Text == "OK");
                    typeof(Forms.Button).GetMethod("OnClick", Flags)!.Invoke(ok, [EventArgs.Empty]);
                    options.Close();
                    Check(!options.IsDisposed, "Options closed while its save was still pending");
                    return null;
                });
                Until(() => options.DialogResult == Forms.DialogResult.OK);
            }
            HoldDatabase("folder create", () => { mainType.GetMethod("NewFolder", Flags)!.Invoke(main, []); return null; });
            Until(() => !MainField<bool>("_folderChanging"));
            var tree = MainField<Forms.TreeView>("_localTree");
            var created = Walk(tree.Nodes).Single(node => node.Tag is LocalFolder folder && folder.Name == "New Folder");
            created.EndEdit(true);
            HoldDatabase("folder rename", () => { mainType.GetMethod("ApplyFolderRename", Flags)!.Invoke(main, [created, "Renamed while busy"]); return null; });
            Until(() => !MainField<bool>("_folderChanging"));
            Check(Walk(tree.Nodes).Any(node => node.Tag is LocalFolder folder && folder.Name == "Renamed while busy"), "folder write or refresh lost the rename");

            services.Database.SaveContact(new Contact(0, "Beta Contact", "beta@example.invalid"));
            var bookType = assembly.GetType("Mailkeep.WinForms.AddressBookForm", true)!;
            using (var book = (Forms.Form)Activator.CreateInstance(bookType, [services.Database, false, "", "", ""])!)
            {
                HoldDatabase("address book read", () => { book.Show(); return null; });
                Console.WriteLine("UI BLOCKING awaiting contacts");
                var contacts = (Forms.ListView)bookType.GetField("_contacts", Flags)!.GetValue(book)!;
                Until(() => contacts.Items.Count == 1);
                Check(contacts.Items[0].Text == "Beta Contact", "address book did not apply its background result");
                book.Close();
            }

            var seedType = assembly.GetType("Mailkeep.WinForms.ComposeSeed", true)!;
            var seed = Activator.CreateInstance(seedType, ["New Message", "fixture-3", "", "", "", "Typing fixture", "Body", null, null, null, null, null])!;
            var opening = (Task)mainType.GetMethod("ComposeAsync", Flags)!.Invoke(main, [seed])!;
            Until(() => opening.IsCompleted); opening.GetAwaiter().GetResult();
            using var compose = Forms.Application.OpenForms.Cast<Forms.Form>().Single(form => form.GetType().Name == "ComposeForm");
            var composeType = compose.GetType();
            var editor = composeType.GetField("_editor", Flags)!.GetValue(compose)!;
            Until(() => (bool)editor.GetType().GetField("_ready", Flags)!.GetValue(editor)!);
            Console.WriteLine("UI BLOCKING autocomplete editor ready");
            var to = (Forms.TextBox)composeType.GetField("_to", Flags)!.GetValue(compose)!;
            var heldContacts = new TaskCompletionSource<(IReadOnlyList<Contact>, Dictionary<string, string>)>(TaskCreationOptions.RunContinuationsAsynchronously);
            composeType.GetField("_contacts", Flags)!.SetValue(compose, null);
            composeType.GetField("_loading", Flags)!.SetValue(compose, heldContacts.Task);
            var eventual = Task.Run(async () =>
            {
                await Task.Delay(3000);
                heldContacts.TrySetResult(([new Contact(1, "Alpha", "alpha@example.invalid"), new Contact(2, "Beta", "beta@example.invalid")], new()));
            });
            try
            {
                to.Focus();
                var clock = Stopwatch.StartNew(); to.Text = "al";
                Check(clock.ElapsedMilliseconds < 500, $"autocomplete blocked typing for {clock.ElapsedMilliseconds}ms");
                to.Text = "be"; to.SelectionStart = to.Text.Length;
                // TextChanged precedes native caret placement; explicitly repeat the real suggestion action.
                composeType.GetMethod("Suggest", Flags)!.Invoke(compose, [to]);
                var suggestions = (Forms.ListBox)composeType.GetField("_suggestions", Flags)!.GetValue(compose)!;
                Check(!suggestions.Visible, "suggestions appeared before the held read completed");
                heldContacts.TrySetResult(([new Contact(1, "Alpha", "alpha@example.invalid"), new Contact(2, "Beta", "beta@example.invalid")], new()));
                Until(() => suggestions.Visible);
                Check(suggestions.Items.Count == 1 && suggestions.Items[0]!.ToString()!.Contains("beta@example.invalid"), "late suggestions used obsolete typed text");
                Check(to.Text == "be" && to.Focused, "suggestion loading changed text or focus");
                // A late result after closing must not address disposed controls.
                var afterClose = new TaskCompletionSource<(IReadOnlyList<Contact>, Dictionary<string, string>)>(TaskCreationOptions.RunContinuationsAsynchronously);
                composeType.GetField("_contacts", Flags)!.SetValue(compose, null);
                composeType.GetField("_loading", Flags)!.SetValue(compose, afterClose.Task);
                composeType.GetMethod("Contacts", Flags)!.Invoke(compose, []);
                composeType.GetField("_discard", Flags)!.SetValue(compose, true);
                compose.Close();
                afterClose.SetResult(([], new())); Pump(100);
            }
            finally
            {
                heldContacts.TrySetResult(([], new())); eventual.GetAwaiter().GetResult();
                if (!compose.IsDisposed) { composeType.GetField("_discard", Flags)!.SetValue(compose, true); compose.Close(); }
            }
            main.Close();

            void HoldDatabase(string name, Func<Task?> action)
            {
                using var locked = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                var blocking = Task.Run(() =>
                {
                    using var c = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False");
                    c.Open(); using var q = c.CreateCommand(); q.CommandText = "BEGIN EXCLUSIVE"; q.ExecuteNonQuery();
                    locked.Set(); try { release.Wait(TimeSpan.FromSeconds(3)); }
                    finally { q.CommandText = "ROLLBACK"; q.ExecuteNonQuery(); }
                });
                try
                {
                    Check(locked.Wait(TimeSpan.FromSeconds(5)), "could not acquire private fixture lock");
                    var clock = Stopwatch.StartNew(); var pending = action(); var elapsed = clock.ElapsedMilliseconds;
                    Check(elapsed < 500, $"{name} blocked the UI for {elapsed}ms");
                    var pulses = 0; using var timer = new Forms.Timer { Interval = 20 };
                    timer.Tick += (_, _) => pulses++; timer.Start(); Pump(200);
                    Check(pulses >= 3, name + " stopped UI events");
                    release.Set();
                    if (pending is not null) { Until(() => pending.IsCompleted); pending.GetAwaiter().GetResult(); }
                    Console.WriteLine($"UI BLOCKING {name}: invoke_ms={elapsed} pulses={pulses}");
                }
                finally { release.Set(); blocking.GetAwaiter().GetResult(); }
            }
        }
    }
    private static IEnumerable<Forms.TreeNode> Walk(Forms.TreeNodeCollection nodes)
    {
        foreach (Forms.TreeNode node in nodes) { yield return node; foreach (var child in Walk(node.Nodes)) yield return child; }
    }
    private static void Pump(int ms) { var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < ms) { Forms.Application.DoEvents(); Thread.Sleep(10); } }
    private static void Until(Func<bool> done) { var clock = Stopwatch.StartNew(); while (!done()) { Check(clock.ElapsedMilliseconds < 15000, "UI fixture did not settle"); Pump(10); } }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
