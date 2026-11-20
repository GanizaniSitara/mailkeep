using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;

/// <summary>How long Delete and Move make the reader wait, measured on a fixture whose imported archive is the size
/// of the real one. Two numbers per action: how long until the rows leave the list, and the longest the window's
/// thread stops answering in the seconds after — which includes reading the next message, since marking it read
/// recounts the folders. No mouse and no real mail: the key is posted to the list and the Move dialog is driven by
/// its window messages.</summary>
internal static class LatencySmoke
{
    public static int Run(string app, string root, MailkeepServices services, int archiveRows, int limitMs)
    {
        SeedArchive(services, archiveRows);
        var start = new ProcessStartInfo(Path.GetFullPath(app), $"--root \"{root}\"")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(app))!
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch Mailkeep.");
        try
        {
            var window = WaitValue(() => { process.Refresh(); return process.MainWindowHandle == IntPtr.Zero ? null : (IntPtr?)process.MainWindowHandle; },
                TimeSpan.FromSeconds(30), "main window did not appear");
            var list = WaitValue(() =>
            {
                var element = AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "Message list"));
                return element is null ? null : (IntPtr?)new IntPtr(element.Current.NativeWindowHandle);
            }, TimeSpan.FromSeconds(30), "message list did not appear");
            WaitValue(() => Count(list) == 2 ? (bool?)true : null, TimeSpan.FromSeconds(60), "Inbox fixture did not load");
            // Let start-up settle: the first row is shown and, after its pause, marked read.
            Thread.Sleep(4000);

            var delete = Measure(window, list, 1, TimeSpan.FromSeconds(4),
                () => PostMessage(list, 0x0100, new IntPtr(0x2E), IntPtr.Zero)); // WM_KEYDOWN VK_DELETE
            WaitValue(() => services.Database.ListMessages().Count(message => message.Account == "local-fixture"
                && message.Folder == LocalFolderKinds.Deleted) == 1 ? (bool?)true : null, TimeSpan.FromSeconds(10), "Delete did not reach the store");

            var target = services.Database.ListLocalFolders().Single(folder => folder.Name == "Filed fixture");
            var toolbar = AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "Main toolbar"));
            var moveButton = Wait(() =>
            {
                var button = toolbar.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Move"));
                return button is { Current.IsEnabled: true } ? button : null;
            }, TimeSpan.FromSeconds(10), "Move is not enabled for the remaining message");
            var opening = Task.Run(() => ((InvokePattern)moveButton.GetCurrentPattern(InvokePattern.Pattern)).Invoke());
            var chooser = WaitValue(() => { var handle = FindWindow(null, "Move to folder"); return handle == IntPtr.Zero ? null : (IntPtr?)handle; },
                TimeSpan.FromSeconds(10), "Move dialog did not open");
            var folders = Children(chooser).Single(child => child.Class.Contains("LISTBOX", StringComparison.OrdinalIgnoreCase)).Handle;
            if ((int)SendText(folders, 0x018c, new IntPtr(-1), target.Name) < 0) throw new InvalidOperationException("Filed fixture missing from Move dialog");
            SendMessage(GetParent(folders), 0x0111, new IntPtr((1 << 16) | (GetDlgCtrlID(folders) & 0xffff)), folders);
            var confirm = Children(chooser).First(child => child.Class.Contains("BUTTON", StringComparison.OrdinalIgnoreCase) && child.Title == "Move").Handle;
            var move = Measure(window, list, 0, TimeSpan.FromSeconds(4), () => PostMessage(confirm, 0x00F5, IntPtr.Zero, IntPtr.Zero)); // BM_CLICK
            opening.Wait(5000);
            WaitValue(() => services.Database.ListMessages().Count(message => message.Account == "local-fixture" && message.Folder == target.Key) == 1
                ? (bool?)true : null, TimeSpan.FromSeconds(10), "Move did not reach the store");

            PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
            process.WaitForExit(5000);
            var ok = delete.RowsMs <= limitMs && delete.BlockMs <= limitMs && move.RowsMs <= limitMs && move.BlockMs <= limitMs;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} latency archive_rows={archiveRows} limit_ms={limitMs} "
                + $"delete_rows_ms={delete.RowsMs} delete_max_ui_block_ms={delete.BlockMs} "
                + $"move_rows_ms={move.RowsMs} move_max_ui_block_ms={move.BlockMs}");
            return ok ? 0 : 1;
        }
        finally
        {
            if (!process.HasExited) process.Kill();
        }
    }

    /// <summary>Acts, then times the list reaching its new size and, for the whole window, the longest single wait
    /// for the window's thread to answer a do-nothing message — which is what a key press or a repaint waits on.</summary>
    private static (long RowsMs, long BlockMs) Measure(IntPtr window, IntPtr list, int expected, TimeSpan watch, Action act)
    {
        long worst = 0;
        var running = true;
        var monitor = new Thread(() =>
        {
            while (Volatile.Read(ref running))
            {
                var clock = Stopwatch.StartNew();
                SendMessageTimeout(window, 0, IntPtr.Zero, IntPtr.Zero, 0x0002, 10000, out _); // WM_NULL, SMTO_ABORTIFHUNG
                worst = Math.Max(worst, clock.ElapsedMilliseconds);
                Thread.Sleep(10);
            }
        }) { IsBackground = true };
        monitor.Start();
        var timer = Stopwatch.StartNew();
        act();
        long rows = -1;
        while (timer.Elapsed < watch)
        {
            if (rows < 0 && Count(list) == expected) rows = timer.ElapsedMilliseconds;
            Thread.Sleep(5);
        }
        Volatile.Write(ref running, false);
        monitor.Join();
        if (rows < 0) throw new TimeoutException($"list did not reach {expected} rows within {watch.TotalSeconds:N0}s");
        return (rows, worst);
    }

    /// <summary>An import the size of the real one: a quarter of a million rows, most of them never opened, spread
    /// over the folders a long-kept mailbox has. Written in one transaction; the search index is not needed.</summary>
    private static void SeedArchive(MailkeepServices services, int rows)
    {
        using var connection = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO messages(source,account,folder,source_key,subject,sender,sender_name,date_utc,size,is_read,body_text)
            VALUES($source,'Fixture archive',$folder,$key,$subject,'bulk@example.invalid','Bulk',$date,1024,$read,'')
            """;
        insert.Parameters.AddWithValue("$source", MailContract.ArchiveSource);
        var folder = insert.Parameters.Add("$folder", SqliteType.Text);
        var key = insert.Parameters.Add("$key", SqliteType.Text);
        var subject = insert.Parameters.Add("$subject", SqliteType.Text);
        var date = insert.Parameters.Add("$date", SqliteType.Text);
        var read = insert.Parameters.Add("$read", SqliteType.Integer);
        var origin = new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < rows; index++)
        {
            folder.Value = $"Bulk/Folder {index % 60:00}";
            key.Value = "bulk-" + index;
            subject.Value = "Bulk archived message " + index;
            date.Value = origin.AddMinutes(index).UtcDateTime.ToString("O");
            read.Value = index % 3 == 0 ? 1 : 0;
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static int Count(IntPtr list) => (int)SendMessage(list, 0x1004, IntPtr.Zero, IntPtr.Zero); // LVM_GETITEMCOUNT

    private static T WaitValue<T>(Func<T?> probe, TimeSpan timeout, string failure) where T : struct
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (probe() is { } value) return value;
            Thread.Sleep(50);
        }
        throw new TimeoutException(failure);
    }

    private static T Wait<T>(Func<T?> probe, TimeSpan timeout, string failure) where T : class
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (probe() is { } value) return value;
            Thread.Sleep(50);
        }
        throw new TimeoutException(failure);
    }

    private static List<(IntPtr Handle, string Class, string Title)> Children(IntPtr parent)
    {
        var found = new List<(IntPtr, string, string)>();
        EnumChildWindows(parent, (child, _) =>
        {
            var name = new StringBuilder(256);
            var text = new StringBuilder(512);
            GetClassName(child, name, name.Capacity);
            GetWindowText(child, text, text.Capacity);
            found.Add((child, name.ToString(), text.ToString().Replace("&", "")));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, WindowCallback callback, IntPtr state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr word, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] private static extern IntPtr SendText(IntPtr window, uint message, IntPtr word, string text);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr word, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string windowName);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr word, IntPtr value, uint flags, uint timeout, out IntPtr result);
}
