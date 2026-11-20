using System.Diagnostics;
using System.Reflection;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

/// <summary>Filing shortcuts end to end, in process: chosen in Tools > Options, pressed as Alt+digit through the
/// main window's own key handling, and taken back with Ctrl+Z — on a throwaway store, with no keyboard or mouse
/// of the desktop's.</summary>
internal static class FilingShortcutSmoke
{
    public static void Run(string app, string root, MailkeepServices services)
    {
        var projects = services.Database.CreateLocalFolder("Projects", null);
        var receipts = services.Database.CreateLocalFolder("Receipts", projects.Key);
        var filed = services.Database.ListLocalFolders().Single(folder => folder.Name == "Filed fixture");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Check(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(90))) throw new TimeoutException("filing-shortcuts did not finish");
        if (failure is not null) throw new InvalidOperationException("filing-shortcuts failed", failure);

        void Check()
        {
            Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
            Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            var assembly = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(app))!, "Mailkeep.WinForms.dll"));
            var mainType = assembly.GetType("Mailkeep.WinForms.MainForm", true)!;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var main = (Forms.Form)Activator.CreateInstance(mainType, [root])!;
            main.Show();
            var clock = Stopwatch.StartNew();
            while (!(bool)mainType.GetField("_ready", flags)!.GetValue(main)! || clock.Elapsed < TimeSpan.FromSeconds(3)) Pump();
            var list = (Forms.ListView)mainType.GetField("_messages", flags)!.GetValue(main)!;
            var status = (Forms.ToolStripStatusLabel)mainType.GetField("_status", flags)!.GetValue(main)!;
            Wait(() => list.VirtualListSize == 2, "the Inbox fixture did not load");

            // Options: Alt+1 to Projects/Receipts, Alt+2 to Filed fixture, through the dialog's own drop-downs.
            var optionsType = assembly.GetType("Mailkeep.WinForms.OptionsForm", true)!;
            var options = (Forms.Form)Activator.CreateInstance(optionsType, [mainType.GetField("_services", flags)!.GetValue(main)!, false, "New", null])!;
            options.Show(main);
            Pump();
            var choices = All(options).OfType<Forms.ComboBox>().Where(box => box.AccessibleName?.EndsWith(" folder") == true).ToArray();
            if (choices.Length != 10 || choices[0].AccessibleName != "Alt+1 folder" || choices[9].AccessibleName != "Alt+0 folder")
                throw new InvalidOperationException($"Options lists {choices.Length} shortcuts, not Alt+1..Alt+0");
            Choose(choices[0], "Projects/Receipts");
            Choose(choices[1], "Filed fixture");
            if (choices[0].Items.Cast<object>().Any(item => Name(item) is "Drafts" or "Outbox")) throw new InvalidOperationException("Drafts or Outbox offered as a filing folder");
            var tabs = All(options).OfType<Forms.TabControl>().Single();
            tabs.SelectedIndex = 1;
            Pump();
            if (Environment.GetEnvironmentVariable("MAILKEEP_SMOKE_SHOTS") is { } shots)
            {
                using var image = new System.Drawing.Bitmap(options.Width, options.Height);
                options.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, options.Width, options.Height));
                image.Save(Path.Combine(shots, "filing-shortcuts-options.png"));
            }
            OptionsSaveSmoke.Save(options);
            Pump();
            if (services.Database.GetSetting("shortcut.file.1") != receipts.Key || services.Database.GetSetting("shortcut.file.2") != filed.Key)
                throw new InvalidOperationException("Options did not save the shortcuts");

            // Alt+1 on the first message, Alt+2 on the second.
            Select(0);
            var timer = Stopwatch.StartNew();
            Press(Forms.Keys.Alt | Forms.Keys.D1);
            Wait(() => list.VirtualListSize == 1, "Alt+1 did not take the message out of the Inbox");
            var removedMs = timer.ElapsedMilliseconds;
            Wait(() => InFolder(receipts.Key) == 1, "Alt+1 did not file into Projects/Receipts");
            Select(0);
            Press(Forms.Keys.Alt | Forms.Keys.D2);
            Wait(() => InFolder(filed.Key) == 1 && list.VirtualListSize == 0, "Alt+2 did not file into Filed fixture");
            Press(Forms.Keys.Alt | Forms.Keys.D3);
            Wait(() => status.Text.Contains("Alt+3 has no folder"), "an unset key did not say so");

            // Ctrl+Z twice: each filing put back, newest first; a third says there is nothing left. The first is
            // pressed straight after a filing, before its write has necessarily finished.
            Press(Forms.Keys.Control | Forms.Keys.Z);
            Wait(() => InFolder(filed.Key) == 0 && InFolder(LocalFolderKinds.Inbox) == 1 && list.VirtualListSize == 1, $"the first Ctrl+Z did not return the Alt+2 message; status: {status.Text}; inbox={InFolder(LocalFolderKinds.Inbox)} filed={InFolder(filed.Key)} rows={list.VirtualListSize}; errors: {(File.Exists(Path.Combine(root, "errors.log")) ? File.ReadAllText(Path.Combine(root, "errors.log")) : "none")}");
            Press(Forms.Keys.Control | Forms.Keys.Z);
            Wait(() => InFolder(receipts.Key) == 0 && InFolder(LocalFolderKinds.Inbox) == 2 && list.VirtualListSize == 2, "the second Ctrl+Z did not return the Alt+1 message");
            Wait(() => status.Text == "Moved 1 message back out of Projects/Receipts", $"undo did not say what it put back: {status.Text}");
            Press(Forms.Keys.Control | Forms.Keys.Z);
            Wait(() => status.Text == "Nothing to undo", "a third Ctrl+Z did not say there was nothing to undo");
            if (!status.Text.Equals("Nothing to undo")) throw new InvalidOperationException("status lost");
            main.Close();
            main.Dispose();
            Pump();
            Console.WriteLine($"PASS filing-shortcuts options=alt1..alt0 saved=2 alt1=nested-folder alt2=filed rows_gone_ms={removedMs} unset=explained ctrl-z=twice-in-order nothing-left=said");

            void Press(Forms.Keys keys)
            {
                var message = Forms.Message.Create(list.Handle, (keys & Forms.Keys.Alt) != 0 ? 0x0104 : 0x0100, (IntPtr)(int)(keys & Forms.Keys.KeyCode), IntPtr.Zero);
                var arguments = new object[] { message, keys };
                var handled = (bool)mainType.GetMethod("ProcessCmdKey", flags)!.Invoke(main, arguments)!;
                if (!handled) throw new InvalidOperationException("the main window did not take " + keys);
            }

            void Select(int index)
            {
                list.SelectedIndices.Clear();
                list.SelectedIndices.Add(index);
                Pump();
            }

            int InFolder(string key) => services.Database.ListMessages().Count(message => message.Account == "local-fixture" && message.Folder == key);
        }

        static void Choose(Forms.ComboBox box, string name)
        {
            var item = box.Items.Cast<object>().First(candidate => Name(candidate) == name);
            box.SelectedItem = item;
        }

        static string? Name(object item) => item.GetType().GetProperty("Name")?.GetValue(item) as string;

        static IEnumerable<Forms.Control> All(Forms.Control parent)
        {
            foreach (Forms.Control child in parent.Controls)
            {
                yield return child;
                foreach (var nested in All(child)) yield return nested;
            }
        }

        static void Pump() { for (var pass = 0; pass < 5; pass++) { Forms.Application.DoEvents(); Thread.Sleep(10); } }

        static void Wait(Func<bool> done, string failure)
        {
            var clock = Stopwatch.StartNew();
            while (!done())
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(failure);
                Forms.Application.DoEvents();
                Thread.Sleep(10);
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int capacity);
}
