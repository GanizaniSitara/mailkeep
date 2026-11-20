using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

/// <summary>Native Tab/Shift+Tab and arrow keys on the guest desktop, with invented mail only.</summary>
internal static class PaneNavigationSmoke
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    // Capability probe only: creates no Form, WebView control, controller or visible window.
    public static int ProbeInputOption(string app, string output)
    {
        Directory.CreateDirectory(output);
        object? result = null;
        var thread = new Thread(() =>
        {
            try
            {
                Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
                var directory = Path.GetDirectoryName(Path.GetFullPath(app))!;
                var loader = Path.Combine(directory, "WebView2Loader.dll");
                if (!File.Exists(loader)) loader = Path.Combine(directory, "runtimes", "win-x64", "native", "WebView2Loader.dll");
                NativeLibrary.Load(loader);
                var assembly = Assembly.LoadFrom(Path.Combine(directory, "Microsoft.Web.WebView2.Core.dll"));
                var type = assembly.GetType("Microsoft.Web.WebView2.Core.CoreWebView2Environment", true)!;
                var task = (Task)type.GetMethod("CreateAsync")!.Invoke(null, [null, Path.Combine(output, "probe-profile"), null])!;
                Wait(() => task.IsCompleted, "environment probe timed out");
                task.GetAwaiter().GetResult();
                var environment = task.GetType().GetProperty("Result")!.GetValue(task)!;
                var options = type.GetMethod("CreateCoreWebView2ControllerOptions")!.Invoke(environment, null)!;
                options.GetType().GetProperty("AllowHostInputProcessing")!.SetValue(options, true);
                result = new { supportsHostInput = true, runtime = type.GetProperty("BrowserVersionString")!.GetValue(environment) };
            }
            catch (Exception error)
            {
                var failure = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
                result = new { supportsHostInput = false, error = failure.GetType().Name, message = failure.Message };
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(30)), "capability probe timed out");
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        File.WriteAllText(Path.Combine(output, "browser-input-compat.json"), json);
        Console.WriteLine(json);
        return 0;
    }

    public static void BackgroundLaunch(string app, string root, MailkeepServices services, string? screenshots)
    {
        services.Database.SetSetting("sync.auto", "off");
        services.Database.SetSetting("window.layout", "{\"Bounds\":{\"X\":80,\"Y\":90,\"Width\":1100,\"Height\":720},\"Maximized\":true,\"Dpi\":96}");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var sentinel = new Forms.Form { Text = "Foreground sentinel", Width = 500, Height = 200 };
                sentinel.Show(); sentinel.Activate(); Pump(200);
                Check(GetForegroundWindow() == sentinel.Handle, "guest sentinel did not receive focus");
                var startup = new ProcessStartInfo(app) { UseShellExecute = false };
                startup.ArgumentList.Add("--root"); startup.ArgumentList.Add(root);
                startup.ArgumentList.Add("--no-activate");
                using var process = Process.Start(startup)!;
                try
                {
                    var clock = Stopwatch.StartNew();
                    var shown = false;
                    while (clock.ElapsedMilliseconds < 20000)
                    {
                        Check(!process.HasExited, "background app exited");
                        Check(GetForegroundWindow() == sentinel.Handle, "background launch stole guest focus");
                        process.Refresh();
                        shown |= process.MainWindowHandle != IntPtr.Zero;
                        Pump(20);
                    }
                    var window = FindWindow(null, "Inbox - Mailkeep");
                    GetWindowThreadProcessId(window, out var owner);
                    Console.WriteLine($"background process={process.Id} window={window} owner={owner} visible={IsWindowVisible(window)} process-main={process.MainWindowHandle} shown={shown} responding={process.Responding}");
                    var timing = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mailkeep", "logs", "timing.log");
                    if (File.Exists(timing)) Console.WriteLine(string.Join("\n", File.ReadAllLines(timing).TakeLast(35)));
                    if (File.Exists(Path.Combine(root, "errors.log"))) Console.WriteLine(File.ReadAllText(Path.Combine(root, "errors.log")));
                    if (screenshots is not null)
                    {
                        Directory.CreateDirectory(screenshots);
                        var screen = Forms.SystemInformation.VirtualScreen;
                        using var image = new Bitmap(screen.Width, screen.Height);
                        using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(screen.Location, Point.Empty, screen.Size);
                        image.Save(Path.Combine(screenshots, "background-launch.png"));
                    }
                    Check(window != IntPtr.Zero && owner == process.Id && IsWindowVisible(window)
                        && SendMessageTimeout(window, 0, IntPtr.Zero, IntPtr.Zero, 2, 2000, out _) != IntPtr.Zero,
                        "background app has no visible responsive window");
                    GetWindowRect(window, out var bounds);
                    SetCursorPos(bounds.Right - 50, bounds.Bottom - 80);
                    MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
                    MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
                    Wait(() => GetForegroundWindow() == window, "intentional guest click did not activate Mailkeep");
                    PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    Check(process.WaitForExit(10000), "fixture did not close politely");
                }
                finally { if (!process.HasExited) process.Kill(); }
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(45)), "background launch test timed out");
        if (failure is not null) throw new InvalidOperationException("background launch failed", failure);
        Console.WriteLine("PASS background-launch --no-activate saved-maximized visible responsive; sentinel kept foreground for 20 seconds; intentional mouse click activates; polite close");
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out WindowRect rect);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint x, uint y, uint data, UIntPtr extra);

    public static void Run(string app, string root, MailkeepServices services, string? screenshots,
        bool digest = false, bool legacyInput = false, bool selectionAnchor = false)
    {
        services.Database.SetSetting("sync.auto", "off");
        var arrowFolders = new List<LocalFolder>();
        if (!digest && !selectionAnchor)
        {
            foreach (var name in new[] { "Keyboard A", "Keyboard B", "Keyboard C empty" })
            {
                var folder = services.Database.CreateLocalFolder(name, null);
                arrowFolders.Add(folder);
                if (name.EndsWith("empty")) continue;
                services.Database.Upsert(new MessageInput(MailContract.LiveSource, "local-fixture", folder.Key,
                    name, null, 0, name, "sender@example.invalid", "Fixture", ["local@example.invalid"],
                    DateTimeOffset.UtcNow, 100, true, 0, name + " body", "<body><h1>" + name + " body</h1></body>", null));
            }
        }
        var text = digest ? "Weekly LinkedIn contact bucket - remaining people"
            : string.Join("\r\n", Enumerable.Range(1, 200).Select(i => $"Line {i}: Keyboard navigation fixture."));
        var digestHtml = "<body style='background:#dff0df;font-family:Arial'><h1>Keep in touch: 44 remaining</h1><table>"
            + string.Concat(Enumerable.Range(1, 44).Select(i => $"<tr><td style='padding:14px'><b>Contact {i}</b></td><td>Example company</td>"
                + $"<td><a href='https://example.invalid/contact/{i}'>Review contact</a></td></tr>")) + "</table></body>";
        foreach (var html in new[] { false, true })
            services.Database.Upsert(new MessageInput(MailContract.LiveSource, "local-fixture", LocalFolderKinds.Inbox,
                "pane-" + html, null, 0, "Pane " + html, "sender@example.invalid", "Fixture", ["local@example.invalid"],
                DateTimeOffset.UtcNow.AddMinutes(html ? 2 : 1), 200, true, 0, text,
                html ? (digest ? digestHtml : "<body><a href='https://example.invalid/one'>One</a><a href='https://example.invalid/two'>Two</a>"
                    + string.Concat(Enumerable.Range(1, 200).Select(i => $"<p>Line {i}: Keyboard navigation fixture.</p>")) + "</body>") : null, null));
        Exception? failure = null;
        var thread = new Thread(() => { try { CheckUi(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(90)), "pane navigation timed out");
        if (failure is not null) throw new InvalidOperationException("pane navigation failed", failure);
        if (selectionAnchor)
        {
            Console.WriteLine("PASS selection-anchor native Up/Down follow focus; sorting preserves focused message; refresh restores matching focus and selection; shortcut selection sets keyboard anchor");
            return;
        }
        Console.WriteLine(digest ? $"PASS digest-rendering contacts=44 plain-stub=49 HTML-visible text-hidden painted legacy-input={legacyInput}"
            : "PASS pane-navigation native Tab/Shift+Tab wrap twice; HTML with links, plain text, empty body; tree/list/account arrows; body scrolling; no added pane borders; Ctrl+I unchanged");

        void CheckUi()
        {
            Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
            Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            var directory = Path.GetDirectoryName(Path.GetFullPath(app))!;
            var loader = Path.Combine(directory, "WebView2Loader.dll");
            if (!File.Exists(loader)) loader = Path.Combine(directory, "runtimes", "win-x64", "native", "WebView2Loader.dll");
            NativeLibrary.Load(loader);
            var assembly = Assembly.LoadFrom(Path.Combine(directory, "Mailkeep.WinForms.dll"));
            var type = assembly.GetType("Mailkeep.WinForms.MainForm", true)!;
            using var main = (Forms.Form)Activator.CreateInstance(type, [root])!;
            object? Get(string name) => type.GetField(name, Flags)!.GetValue(main);
            if (legacyInput)
                Get("_html")!.GetType().GetField("RoutePaneKeys", Flags)!.SetValue(Get("_html"), false);
            // These invented accounts are already checked: arrows must exercise navigation without collection.
            var checkedAccounts = (Dictionary<string, DateTimeOffset>)Get("_accountChecked")!;
            foreach (var account in services.Accounts.Load()) checkedAccounts[account.Name] = DateTimeOffset.UtcNow.AddMinutes(5);
            main.Show();
            main.Activate(); // This scenario runs only on the isolated guest desktop.
            Wait(() => (bool)Get("_ready")!, "main not ready");
            var tree = (Forms.TreeView)Get("_localTree")!;
            var list = (Forms.ListView)Get("_messages")!;
            var accounts = (Forms.ListView)Get("_accounts")!;
            var plain = (Forms.RichTextBox)Get("_body")!;
            var html = (Forms.Control)Get("_html")!;
            Wait(() => (bool)html.GetType().GetProperty("IsReady", Flags)!.GetValue(html)!, "HTML runtime unavailable");
            // Match an older runtime: standard browser initialization, but pane routing is still requested.
            if (legacyInput) html.GetType().GetField("RoutePaneKeys", Flags)!.SetValue(html, true);
            var inbox = tree.Nodes.Find("LocalFolder:" + LocalFolderKinds.Inbox, true).Single();
            tree.SelectedNode = inbox;
            Wait(() => list.VirtualListSize >= 3, "Inbox fixture not loaded");
            if (selectionAnchor)
            {
                list.Focus();
                list.SelectedIndices.Clear(); list.SelectedIndices.Add(2); list.Items[0].Focused = true;
                Key("{DOWN}");
                Check(list.FocusedItem?.Index == 1 && list.SelectedIndices.Cast<int>().SequenceEqual([1]),
                    "Down followed the highlight instead of the dotted focus row");
                Key("{UP}");
                Check(list.FocusedItem?.Index == 0 && list.SelectedIndices.Cast<int>().SequenceEqual([0]),
                    "Up did not continue from the focused row");
                list.SelectedIndices.Clear(); list.SelectedIndices.Add(2); list.Items[2].Focused = true;
                var rows = (IReadOnlyList<object>)Get("_rows")!;
                var focusedId = ((MessageSummary)rows[2]).Id;
                var sort = type.GetField("_sortDescending", Flags)!;
                sort.SetValue(main, !(bool)sort.GetValue(main)!);
                type.GetMethod("Show", Flags, null, [typeof(IReadOnlyList<object>)], null)!.Invoke(main, [rows]);
                rows = (IReadOnlyList<object>)Get("_rows")!;
                var expected = rows.ToList().FindIndex(row => row is MessageSummary message && message.Id == focusedId);
                Check(list.SelectedIndices.Cast<int>().SequenceEqual([expected]), "sorting did not restore selection");
                Check(list.FocusedItem?.Index == expected, "sorting restored the highlight but left the dotted focus on another message");
                Key("{DOWN}");
                Check(list.FocusedItem?.Index == expected + 1 && list.SelectedIndices.Cast<int>().SequenceEqual([expected + 1]),
                    "Down after sorting used a stale focused row");
                list.SelectedIndices.Clear(); list.SelectedIndices.Add(0); list.Items[0].Focused = true;
                type.GetMethod("NavigateMessage", Flags)!.Invoke(main, [1, false, null, false]);
                Check(list.SelectedIndices.Cast<int>().SequenceEqual([1]) && list.FocusedItem?.Index == 1,
                    "message navigation changed the highlight but not its keyboard anchor");
                var previewId = ((MessageSummary)((IReadOnlyList<object>)Get("_rows")!)[1]).Id;
                Wait(() => Get("_currentContent") is MessageContent content && content.Summary.Id == previewId,
                    "preview did not follow the new focused selection");
                Capture("selection-anchor");
                main.Close();
                return;
            }
            if (digest)
            {
                var rows = ((IEnumerable<object>)Get("_rows")!).ToList();
                var index = rows.FindIndex(row => row is MessageSummary message && message.Subject == "Pane True");
                list.SelectedIndices.Clear(); list.SelectedIndices.Add(index); list.Items[index].Focused = true;
                Wait(() => Get("_currentContent") is MessageContent content && content.Summary.Subject == "Pane True", "digest not selected");
                Wait(() => html.Visible && (bool)html.GetType().GetProperty("DocumentLoaded")!.GetValue(html)!, "digest HTML did not load");
                Check(!plain.Visible, "digest fell back to its plain stub");
                Check(text.Length == 49, "digest fixture does not match the reported plain stub length");
                Check(Script("document.querySelectorAll('tr').length") == "44", "digest contacts missing");
                Check(Script("document.body.innerText.includes('Contact 44')") == "true", "last digest contact missing");
                Pump(500);
                Capture(legacyInput ? "digest-legacy" : "digest-modern");
                using var image = new Bitmap(main.Width, main.Height);
                using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(main.Location, Point.Empty, main.Size);
                var painted = 0;
                for (var y = 0; y < image.Height; y += 4)
                    for (var x = 0; x < image.Width; x += 4)
                    {
                        var pixel = image.GetPixel(x, y);
                        if (pixel.G > 220 && pixel.R > 200 && pixel.B > 200 && pixel.G - pixel.R > 10) painted++;
                    }
                Check(painted > 4000, "digest HTML was loaded but not painted");
                main.Close();
                return;
            }
            foreach (var useHtml in new[] { true, false })
            {
                var rows = ((IEnumerable<object>)Get("_rows")!).ToList();
                var index = rows.FindIndex(row => row is MessageSummary message && message.Subject == "Pane " + useHtml);
                list.SelectedIndices.Clear(); list.SelectedIndices.Add(index); list.Items[index].Focused = true;
                Wait(() => Get("_currentContent") is MessageContent content && content.Summary.Subject == "Pane " + useHtml,
                    "message not selected");
                Wait(() => useHtml ? html.Visible && (bool)html.GetType().GetProperty("DocumentLoaded")!.GetValue(html)! : plain.Visible,
                    "body renderer not ready");
                ControlCycle(useHtml ? html : plain, useHtml ? "html" : "plain");
                var body = useHtml ? html : (Forms.Control)plain;
                body.Focus(); Pump(100);
                Check(body.Parent!.BackColor != SystemColors.Highlight && body.Parent.Padding == Forms.Padding.Empty,
                    "body has an added focus border or padding");
                if (useHtml)
                {
                    Check(Script("document.hasFocus()") == "false", "Tab entered HTML before Down");
                    Key("{TAB}");
                    Check(accounts.ContainsFocus, "Tab before Down did not go directly to Connected Accounts");
                    Key("+{TAB}");
                    Check(html.ContainsFocus && Script("document.hasFocus()") == "false", "reverse Tab entered HTML content");
                    Key("{DOWN}");
                    Check(Script("document.hasFocus()") == "true", "Down did not enter HTML content");
                    Check(Script("document.activeElement.textContent") == "\"One\"", "Down did not focus the first link");
                    Key("{TAB}");
                    Check(html.ContainsFocus && Script("document.activeElement.textContent") == "\"Two\"", "content Tab did not follow the next link");
                    Key("+{TAB}");
                    Check(Script("document.activeElement.textContent") == "\"One\"", "content Shift+Tab did not follow the previous link");
                    Script("window.scrollTo(0,0)");
                    Key("{DOWN}");
                    Wait(() => double.Parse(Script("scrollY"), System.Globalization.CultureInfo.InvariantCulture) > 0, "HTML Down did not scroll");
                    Key("{ESC}");
                    Check(html.ContainsFocus && Script("document.hasFocus()") == "false", "Escape did not return to body pane navigation");
                }
                else
                {
                    plain.SelectionStart = 0;
                    Key("{DOWN}");
                    Check(plain.SelectionStart > 0, "plain Down did not move the caret");
                }
                Capture("pane-" + (useHtml ? "html" : "plain") + "-body");
                Key("{TAB}");
                Check(accounts.ContainsFocus && body.Parent.BackColor != SystemColors.Highlight, "body focus decoration returned");
                Check(accounts.Items.Count >= 2, "Connected fixture missing");
                accounts.Items[0].Selected = true; accounts.Items[0].Focused = true;
                Key("{DOWN}");
                Check(accounts.FocusedItem?.Index == 1, "Connected Down did not navigate");
                var accountName = accounts.FocusedItem.Name;
                type.GetMethod("PopulateAccountList", Flags)!.Invoke(main, null);
                Check(accounts.FocusedItem?.Name == accountName, "Connected list refresh lost the arrow-key account anchor");
                Check(accounts.SelectedItems.Cast<Forms.ListViewItem>().Single().Name == accountName,
                    "Connected list refresh changed the selected account");
                Key("{DOWN}");
                Check(accounts.FocusedItem?.Index == 2 && accounts.SelectedIndices.Cast<int>().SequenceEqual([2]),
                    "Connected Down after refresh skipped or revisited an account");
                for (var step = 1; step <= 8; step++)
                {
                    Key("{TAB}");
                    Forms.Control[] expectedPanes = [accounts, tree, list, plain];
                    var expectedPane = step % 4 == 3 && html.Visible ? html : expectedPanes[step % 4];
                    Check(expectedPane.ContainsFocus, $"Connected Tab cycle step {step} did not leave the account pane correctly; {FocusName()}");
                    Console.WriteLine($"CONNECTED TAB {step}: selected={accounts.SelectedItems.Cast<Forms.ListViewItem>().FirstOrDefault()?.Name}; focused={accounts.FocusedItem?.Name}; view={((MailAccount?)Get("_selectedAccount"))?.Name}");
                }
                Capture("pane-connected");
                // Restore Inbox after navigating the Connected list.
                tree.SelectedNode = inbox;
                Wait(() => list.VirtualListSize >= 3, "Inbox did not reload");
            }
            tree.Focus();
            var original = tree.SelectedNode;
            Key("{DOWN}");
            Check(tree.SelectedNode != original, "Local tree Down did not navigate");
            Capture("pane-local");
            tree.SelectedNode = inbox;
            Wait(() => list.VirtualListSize >= 3, "Inbox did not reload for list arrows");
            list.Focus(); list.SelectedIndices.Clear(); list.SelectedIndices.Add(0); list.Items[0].Focused = true;
            Key("{DOWN}");
            Check(list.SelectedIndices.Count == 1 && list.SelectedIndices[0] == 1, "message list Down did not navigate");
            Capture("pane-list");
            // Existing shortcut must still land in Inbox/list.
            Key("^i");
            Check(tree.SelectedNode == inbox && list.ContainsFocus, "Ctrl+I changed");
            type.GetMethod("ClearPreview", Flags)!.Invoke(main, null);
            ControlCycle(plain, "empty");
            tree.SelectedNode = tree.Nodes.Find("LocalFolder:" + arrowFolders[0].Key, true).Single();
            tree.Focus();
            CheckFolder(0);
            var folderSamples = new List<double>();
            var blankTransitions = 0;
            EventHandler visibleChanged = (_, _) => { if (!html.Visible) blankTransitions++; };
            html.VisibleChanged += visibleChanged;
            for (var iteration = 0; iteration < 8; iteration++)
            {
                var watch = Stopwatch.StartNew();
                Forms.SendKeys.SendWait("{DOWN}"); CheckFolder(1);
                folderSamples.Add(watch.Elapsed.TotalMilliseconds);
                watch.Restart();
                Forms.SendKeys.SendWait("{UP}"); CheckFolder(0);
                folderSamples.Add(watch.Elapsed.TotalMilliseconds);
            }
            html.VisibleChanged -= visibleChanged;
            var orderedSamples = folderSamples.Skip(2).Order().ToArray();
            var latency = new { samplesMs = folderSamples, medianMs = orderedSamples[orderedSamples.Length / 2],
                p95Ms = orderedSamples[(int)Math.Ceiling(orderedSamples.Length * .95) - 1], blankTransitions };
            Console.WriteLine("FOLDER LATENCY " + System.Text.Json.JsonSerializer.Serialize(latency));
            if (screenshots is not null)
                File.WriteAllText(Path.Combine(screenshots, "redraw-latency.json"), System.Text.Json.JsonSerializer.Serialize(latency));
            Check(blankTransitions == 0, "populated folder switches blanked the reading pane");
            for (var iteration = 0; iteration < 3; iteration++)
            {
                Key("{DOWN}"); CheckFolder(1);
                Key("{DOWN}"); CheckFolder(2);
                Key("{UP}"); CheckFolder(1);
                Key("{UP}"); CheckFolder(0);
            }
            Capture("folder-arrow-inbox-redraw");
            Forms.SendKeys.SendWait("{DOWN 2}{UP 2}");
            Pump(500);
            CheckFolder(0);
            tree.SelectedNode = tree.Nodes.Find("LocalFolder:" + LocalFolderKinds.Drafts, true).Single();
            Key("{UP}");
            Check(tree.SelectedNode == inbox, "Up from Drafts did not reach Inbox");
            Wait(() => list.VirtualListSize >= 3 && Get("_currentContent") is MessageContent inboxContent
                && inboxContent.Summary.Folder == LocalFolderKinds.Inbox
                && ((Forms.Control)Get("_folderBand")!).Text == "Inbox", "Up to Inbox did not refresh its list and preview");
            Wait(() => html.Visible && (bool)html.GetType().GetProperty("DocumentLoaded")!.GetValue(html)!
                && Script("document.body.innerText.includes('Keyboard navigation fixture')") == "true",
                "Up to Inbox left the previous folder HTML visible");
            Capture("folder-arrow-returned-inbox");
            Check(services.Database.RecentFetches().Count == 0, "navigation invoked collection");
            main.Close();

            void CheckFolder(int index)
            {
                var folder = arrowFolders[index];
                Check(tree.SelectedNode?.Name == "LocalFolder:" + folder.Key, "native arrow did not select expected folder");
                Wait(() => (string)Get("_selectedLocalFolder")! == folder.Key
                    && ((Forms.Control)Get("_folderBand")!).Text == folder.Name,
                    "folder-tree arrow did not update the displayed folder");
                if (index == 2)
                {
                    Wait(() => list.VirtualListSize == 0 && Get("_currentContent") is null
                        && ((Forms.Label)Get("_subject")!).Text == "Select a message to read it", "empty folder retained previous content");
                }
                else
                {
                    Wait(() => list.VirtualListSize == 1 && Get("_currentContent") is MessageContent content
                        && content.Summary.Subject == folder.Name, "folder-tree arrows retained previous message list or preview");
                    Wait(() => html.Visible && (bool)html.GetType().GetProperty("DocumentLoaded")!.GetValue(html)!
                        && Script("document.body.innerText.includes('" + folder.Name + " body')") == "true",
                        "folder-tree arrows retained previous HTML document");
                }
                Check(tree.ContainsFocus, "folder refresh moved keyboard focus out of the tree");
            }

            void ControlCycle(Forms.Control body, string label)
            {
                Forms.Control[] panes = [tree, list, body, accounts];
                tree.Focus(); Pump(100);
                for (var i = 1; i <= 8; i++)
                {
                    Key("{TAB}");
                    if (!panes[i % 4].ContainsFocus)
                    {
                        Capture("pane-navigation-failed");
                        Console.WriteLine("HTML focus: " + Script("JSON.stringify({focused:document.hasFocus(),active:document.activeElement.tagName,scroll:scrollY})"));
                    }
                    Check(panes[i % 4].ContainsFocus, $"{label}: forward step {i} expected {panes[i % 4].AccessibleName}; focus={FocusName()}");
                    if (body == html && i % 4 == 2)
                        Check(Script("document.hasFocus()") == "false", "pane Tab entered document links without Down");
                    Check(panes[i % 4].Parent!.BackColor != SystemColors.Highlight
                        && panes[i % 4].Parent!.Padding == Forms.Padding.Empty, $"{label}: added pane focus decoration at {i}");
                }
                for (var i = 1; i <= 8; i++)
                {
                    Key("+{TAB}");
                    Check(panes[(8 - i) % 4].ContainsFocus, $"{label}: reverse step {i} expected {panes[(8 - i) % 4].AccessibleName}; focus={FocusName()}");
                }
            }
            string FocusName() => $"tree={tree.ContainsFocus},list={list.ContainsFocus},plain={plain.ContainsFocus},html={html.ContainsFocus},accounts={accounts.ContainsFocus}";
            string Script(string script)
            {
                var core = html.GetType().GetProperty("CoreWebView2")!.GetValue(html)!;
                var task = (Task<string>)core.GetType().GetMethod("ExecuteScriptAsync")!.Invoke(core, [script])!;
                Wait(() => task.IsCompleted, "HTML script timed out");
                return task.GetAwaiter().GetResult();
            }
            void Capture(string name)
            {
                if (screenshots is null) return;
                Directory.CreateDirectory(screenshots);
                using var image = new Bitmap(main.Width, main.Height);
                using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(main.Location, Point.Empty, main.Size);
                image.Save(Path.Combine(screenshots, name + ".png"));
            }
        }
    }

    private static void Key(string keys) { Forms.SendKeys.SendWait(keys); Pump(180); }
    private static void Pump(int ms)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ms) { Forms.Application.DoEvents(); Thread.Sleep(10); }
    }
    private static void Wait(Func<bool> condition, string error)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { Check(clock.ElapsedMilliseconds < 15000, error); Pump(10); }
    }
    private static void Check(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
}
