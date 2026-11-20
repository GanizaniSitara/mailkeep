using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using Mailkeep.Core;
using Mailkeep.Infrastructure;

// Full native UI round trips. Deliberately restricted to the disposable guest deployment.
internal static class ArchiveWorkflowSmoke
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint command);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    private delegate bool EnumWindow(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr handle, IntPtr device, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int size);
    private static readonly Condition All = Condition.TrueCondition;
    private const TreeScope Desc = TreeScope.Descendants;

    public static int Run(string app, string? screenshots)
    {
        var isolated = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MailkeepTestRig", "bundle", "app") + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(app).StartsWith(@"C:\mk-smoke\app\", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFullPath(app).StartsWith(isolated, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("archive-workflow requires a disposable guest test deployment.");
        var root = Path.Combine(Path.GetTempPath(), "mailkeep-archive-workflow", Guid.NewGuid().ToString("N"));
        var shots = screenshots ?? Path.Combine(root, "shots");
        Directory.CreateDirectory(shots);
        var services = new MailkeepServices(root);
        services.Database.Initialize();
        var parent = services.Database.CreateLocalFolder("Archive_2023", null);
        services.Database.CreateLocalFolder("MyStuff_2023", parent.Key);
        services.Database.CreateLocalFolder("MyStuff_2019", parent.Key);
        var location = Path.Combine(root, "archive");
        var expected = new Dictionary<string, (byte[] Raw, bool Read, string Date)>();
        string Bundle(string path, int count)
        {
            var file = Path.Combine(location, path.Replace('/', Path.DirectorySeparatorChar), FolderBundle.FileName);
            using (var bundle = FolderBundle.Create(file, path.Split('/')[^1], "local"))
            {
                bundle.RecordEvent(ArchiveManifest.LayoutEvent,
                    JsonSerializer.Serialize(new ArchiveManifest.Layout(path, "user", "archive", "fixture|" + path)));
                for (var i = 0; i < count; i++)
                {
                    var subject = path + " message " + i;
                    var raw = Encoding.UTF8.GetBytes($"From: fixture@example.test\r\nTo: other@example.test\r\nSubject: {subject}\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=b\r\n\r\n--b\r\nContent-Type: text/plain\r\n\r\nSynthetic retained body {i}\r\n--b\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=proof.bin\r\nContent-Transfer-Encoding: base64\r\n\r\nAAECA//+\r\n--b--\r\n");
                    var date = "2020-01-02T03:04:05Z";
                    expected.Add(subject, (raw, i % 2 == 0, date));
                    bundle.Add(raw, i % 2 == 0, "fixture:" + subject, date, 100 + i);
                }
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            ArchiveManifest.Register(location, file);
            return file;
        }
        Bundle("Archive_2023/MyStuff_2022", 3);
        Bundle("Company@Example/2023/Team/A", 2);
        Bundle("Company@Example/2024/Team/A", 2);
        Bundle("Company@Example/2024/Team/Empty", 0);
        var untouched = Bundle("Keep/Other", 1);
        var untouchedHash = SHA256.HashData(File.ReadAllBytes(untouched));
        var store = new ArchiveStore(services.Paths);
        store.Add(location);
        using var process = Process.Start(new ProcessStartInfo(app)
        {
            UseShellExecute = false,
            ArgumentList = { "--root", root, "--show-archive" }
        })!;
        var steps = new List<string>();
        try
        {
            var main = Wait(() => Window("MailkeepWinForms"), "main window");
            var archive = Wait(() => Window("MailkeepArchive"), "Archive window");
            Thread.Sleep(3000);
            Check(GetWindow(Handle(archive), 4) == IntPtr.Zero, "Archive is still owned by Main");
            ShowWindow(Handle(main), 6);
            Thread.Sleep(500);
            Check(IsIconic(Handle(main)) && !IsIconic(Handle(archive)), "minimizing Main also minimized Archive");
            ShowWindow(Handle(main), 4);
            ShowWindow(Handle(archive), 6);
            Thread.Sleep(500);
            Check(!IsIconic(Handle(main)) && IsIconic(Handle(archive)), "minimizing Archive also minimized Main");
            ShowWindow(Handle(archive), 4);
            steps.Add("independent minimize in both directions");

            Transfer("MyStuff_2022", copy: true, tree: false, "copy-folder");
            VerifyLocal(3);
            Check(Active("Archive_2023/MyStuff_2022"), "Copy removed archive source");
            Transfer("MyStuff_2022", copy: false, tree: false, "restore-existing-copy");
            VerifyLocal(3);
            Check(!Active("Archive_2023/MyStuff_2022"), "Restore retained active archive source");
            AssertOrder();
            ArchiveFolder("MyStuff_2022", "rearchive");
            VerifyLocal(0);
            Check(Active("Archive_2023/MyStuff_2022"), "Re-archive did not create active source");
            Transfer("MyStuff_2022", copy: false, tree: false, "restore-again");
            VerifyLocal(3);
            AssertOrder();

            Transfer("Company@Example", copy: false, tree: true, "restore-tree");
            VerifyLocal(7);
            ShowForInput(main);
            var paths = new MailFiling(services).Folders().Select(folder => folder.Path).ToArray();
            foreach (var path in new[] { "Company@Example/2023/Team/A", "Company@Example/2024/Team/A", "Company@Example/2024/Team/Empty" })
            {
                Check(paths.Contains(path), "Tree restore lost " + path);
                var segments = path.Split('/');
                var current = Node(Find(main, "Local mail folders"), segments[0]);
                foreach (var segment in segments.Skip(1))
                {
                    if (current.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expansion))
                        ((ExpandCollapsePattern)expansion).Expand();
                    var parentNode = current;
                    current = Wait(() => parentNode.FindAll(TreeScope.Children, All).Cast<AutomationElement>()
                        .FirstOrDefault(child => child.Current.ControlType == ControlType.TreeItem
                            && child.Current.Name.Split("  (")[0] == segment), "restored tree node " + path);
                }
            }
            Check(!ArchiveManifest.Read(location).Folders.Any(folder => folder.OriginalPath.StartsWith("Company@Example/")),
                "Tree restore left selected archive entries");
            Check(untouchedHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(untouched))), "Unselected archive bytes changed");
            Check(ArchiveManifest.Read(location).Folders.Single().OriginalPath == "Keep/Other", "Unselected archive inventory changed");
            archive = Wait(() => Window("MailkeepArchive"), "final Archive window");
            ShowForInput(archive);
            Shot(archive, "archive-workflow-final-archive");
            ShowForInput(main);
            Shot(main, "archive-workflow-final-local");
            steps.Add("selected tree: @ name, nested years, duplicate leaves, empty folder, unrelated archive unchanged");
            File.WriteAllText(Path.Combine(shots, "archive-workflow-result.json"), JsonSerializer.Serialize(new { root, steps, localMessages = 7, pass = true }));
            Console.WriteLine("PASS archive-workflow " + string.Join("; ", steps));
            return 0;
        }
        catch
        {
            foreach (var w in Windows()) { try { Shot(w, "archive-workflow-failure-" + w.Current.NativeWindowHandle); } catch { } }
            throw;
        }
        finally
        {
            if (!process.HasExited)
            {
                foreach (var w in Windows()) SendMessage(Handle(w), 0x10, IntPtr.Zero, IntPtr.Zero);
                if (!process.WaitForExit(5000)) process.Kill(); // Only this owned disposable fixture process.
            }
        }

        IEnumerable<AutomationElement> Windows()
        {
            var result = new List<AutomationElement>();
            EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out var pid);
                if (pid == process.Id && IsWindowVisible(handle)) result.Add(AutomationElement.FromHandle(handle));
                return true;
            }, IntPtr.Zero);
            return result;
        }
        AutomationElement? Window(string id) => Windows().FirstOrDefault(w => w.Current.AutomationId == id);
        AutomationElement? Dialog(string title) => Windows().FirstOrDefault(w =>
        {
            var text = new StringBuilder(512);
            GetWindowText(Handle(w), text, text.Capacity);
            return text.ToString() == title;
        });
        void ShowForInput(AutomationElement target)
        {
            foreach (var other in Windows())
                if (Handle(other) != Handle(target) && other.Current.AutomationId is "MailkeepWinForms" or "MailkeepArchive")
                    ShowWindow(Handle(other), 6);
            ShowWindow(Handle(target), 4);
            Thread.Sleep(200);
        }
        AutomationElement Find(AutomationElement parentWindow, string name) => Wait(() => parentWindow.FindAll(Desc, All)
            .Cast<AutomationElement>().FirstOrDefault(e => e.Current.Name == name), name);
        // UIA Invoke can hold its provider channel until a modal dialog closes. Click within this guest instead.
        void Invoke(AutomationElement element)
        {
            var bounds = element.Current.BoundingRectangle;
            SetCursorPos((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
            mouse_event(2, 0, 0, 0, UIntPtr.Zero);
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
        }
        IEnumerable<AutomationElement> Items(AutomationElement scope) => scope.FindAll(Desc,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem)).Cast<AutomationElement>();
        AutomationElement Node(AutomationElement scope, string name) => Wait(() =>
        {
            foreach (var item in Items(scope).ToArray())
                if (item.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern)
                    && ((ExpandCollapsePattern)pattern).Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                    ((ExpandCollapsePattern)pattern).Expand();
            return Items(scope).FirstOrDefault(e => e.Current.Name == name || e.Current.Name.StartsWith(name + "  ("));
        }, "folder " + name);
        void Menu(AutomationElement window, string treeName, string nodeName, string command)
        {
            ShowForInput(window);
            var tree = Find(window, treeName);
            var node = Node(tree, nodeName);
            ((SelectionItemPattern)node.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            if (node.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll)) ((ScrollItemPattern)scroll).ScrollIntoView();
            var bounds = node.Current.BoundingRectangle;
            // Native TreeView hit-testing also consults the actual cursor position. Input stays in the guest.
            SetCursorPos((int)bounds.Left + 20, (int)(bounds.Top + bounds.Height / 2));
            mouse_event(8, 0, 0, 0, UIntPtr.Zero);
            mouse_event(16, 0, 0, 0, UIntPtr.Zero);
            var menu = Wait(() => AutomationElement.RootElement.FindAll(Desc,
                new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id)).Cast<AutomationElement>()
                .FirstOrDefault(e => e.Current.ControlType == ControlType.MenuItem
                    && (e.Current.Name == command || e.Current.Name.StartsWith(command + "…"))), command);
            Invoke(menu);
        }
        string Text(AutomationElement dialog) => string.Join("\n", dialog.FindAll(Desc,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>().Select(e => e.Current.Name));
        void Transfer(string name, bool copy, bool tree, string label)
        {
            var archive = Wait(() => Window("MailkeepArchive"), "Archive");
            Menu(archive, "Archive folders", name, (copy ? "Copy" : "Restore") + (tree ? " Tree" : " Folder") + " to Local Mail");
            var picker = Wait(() => Dialog(copy ? "Copy archived folders to Local Mail" : "Restore from Archive"), "destination picker");
            Shot(picker, "archive-workflow-" + label + "-picker");
            Invoke(Find(picker, copy ? "Copy archived folders" : "Restore archived folders"));
            var confirm = Wait(() =>
            {
                var candidate = Dialog(copy ? "Copy from Archive" : "Restore from Archive");
                return candidate?.FindFirst(Desc, new PropertyCondition(AutomationElement.NameProperty, "Yes")) is not null ? candidate : null;
            }, "transfer confirmation");
            var confirmText = Text(confirm);
            Check(confirmText.Contains(copy ? "originals will stay in Archive" : "leave Archive"), "Transfer confirmation has wrong semantics");
            Shot(confirm, "archive-workflow-" + label + "-confirm");
            Invoke(Find(confirm, "Yes"));
            var result = Wait(() => Dialog(copy ? "Copied to Local Mail" : "Restored to Local Mail"), "transfer result", 90);
            Check(!Text(result).Contains("0 messages were already"), "Result includes misleading zero counter");
            Check(Text(result).Contains(copy ? "originals are still in Archive" : tree ? "These folders are no longer in Archive" : "This folder is no longer in Archive"), "Result has wrong semantics");
            Shot(result, "archive-workflow-" + label + "-result");
            Invoke(Find(result, "OK"));
            steps.Add(label);
        }
        void ArchiveFolder(string name, string label)
        {
            var main = Wait(() => Window("MailkeepWinForms"), "Main");
            Menu(main, "Local mail folders", name, "Move Folder to Archive");
            var confirm = Wait(() => Dialog("Move to Archive"), "archive confirmation");
            var backup = Find(confirm, "Back up the whole mailbox first");
            Check(((TogglePattern)backup.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On,
                "Archive backup is not checked by default");
            Shot(confirm, "archive-workflow-" + label + "-confirm");
            Invoke(Find(confirm, "Move to archive"));
            var done = Wait(() => Dialog("Moved to Archive"), "archive result", 90);
            Shot(done, "archive-workflow-" + label + "-result");
            Invoke(Find(done, "OK"));
            // Reopen/refresh through the real toolbar; an existing Archive window may retain its prior tree.
            SendMessage(Handle(Wait(() => Window("MailkeepArchive"), "Archive")), 0x10, IntPtr.Zero, IntPtr.Zero);
            var button = Find(main, "Open Archive");
            Invoke(button);
            Wait(() => Window("MailkeepArchive"), "reopened Archive");
            steps.Add(label + " (checked backup)");
        }
        bool Active(string path) => ArchiveManifest.Read(location).Folders.Any(folder => folder.OriginalPath == path);
        void VerifyLocal(int count)
        {
            var messages = services.Database.ListMessages(MailContract.LocalCopyAccount, limit: 100);
            Check(messages.Count == count, $"Expected {count} local messages, got {messages.Count}");
            Check(messages.Select(m => m.Subject).Distinct().Count() == count, "Duplicate local messages");
            foreach (var message in messages)
            {
                var original = expected[message.Subject];
                var content = services.Database.GetMessage(message.Id)!;
                Check(File.ReadAllBytes(content.RawPath!).SequenceEqual(original.Raw), "MIME bytes changed: " + message.Subject);
                Check(message.IsRead == original.Read && message.Date == DateTimeOffset.Parse(original.Date), "State/date changed");
                Check(File.ReadAllBytes(services.Database.ExtractAttachment(message.Id, 0)).SequenceEqual(new byte[] { 0, 1, 2, 3, 255, 254 }), "Attachment bytes changed");
            }
        }
        void AssertOrder()
        {
            var main = Wait(() => Window("MailkeepWinForms"), "Main");
            ShowForInput(main);
            var branch = Node(Find(main, "Local mail folders"), "Archive_2023");
            var names = branch.FindAll(TreeScope.Children, All).Cast<AutomationElement>()
                .Where(e => e.Current.ControlType == ControlType.TreeItem).Select(e => e.Current.Name.Split("  (")[0]).ToArray();
            Check(names.SequenceEqual(new[] { "MyStuff_2019", "MyStuff_2022", "MyStuff_2023" }), "Restored folder is not alphabetically placed: " + string.Join(",", names));
            Shot(main, "archive-workflow-alphabetical-" + steps.Count);
        }
        void Shot(AutomationElement window, string name)
        {
            var r = window.Current.BoundingRectangle;
            using var bitmap = new Bitmap((int)r.Width, (int)r.Height);
            using var g = Graphics.FromImage(bitmap);
            var device = g.GetHdc();
            try { Check(PrintWindow(Handle(window), device, 2), "Native window capture failed: " + name); }
            finally { g.ReleaseHdc(device); }
            bitmap.Save(Path.Combine(shots, name + ".png"));
        }
    }
    private static IntPtr Handle(AutomationElement element) => new(element.Current.NativeWindowHandle);
    private static void Check(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    private static T Wait<T>(Func<T?> probe, string what, int seconds = 30) where T : class
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < seconds)
        {
            try { if (probe() is { } result) return result; } catch (ElementNotAvailableException) { }
            Thread.Sleep(200);
        }
        throw new TimeoutException("Timed out: " + what);
    }
}
