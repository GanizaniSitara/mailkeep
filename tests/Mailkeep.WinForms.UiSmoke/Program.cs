using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Mailkeep.Core;
using Mailkeep.Infrastructure;

if (args.Contains("--inspect-compose", StringComparer.OrdinalIgnoreCase))
    return InspectComposeWindow();
if (args.Contains("--inspect-accounts", StringComparer.OrdinalIgnoreCase))
    return InspectAccountsDialog();

var app = Option(args, "--app") ?? throw new ArgumentException("--app PATH is required.");
var scenario = (Option(args, "--scenario") ?? "accounts").ToLowerInvariant();
if (scenario == "send-error")
    return SendFailureSmoke.Run(app, Option(args, "--screenshots"));
if (scenario is "msi-seed" or "msi-check")
    return InstallerPersistenceSmoke.Run(scenario == "msi-seed", Option(args, "--root")!);
if (scenario == "local-pop-smtp")
    return LocalServerAccountSmoke.Run(app, Option(args, "--screenshots"));
if (scenario == "provider-presets")
    return ProviderPresetSmoke.Run(app, Option(args, "--screenshots"));
if (scenario == "browser-input-compat")
    return PaneNavigationSmoke.ProbeInputOption(app, Option(args, "--screenshots")!);
if (scenario == "pilot-consolidation-headless")
    return PilotConsolidationSmoke.Run(Option(args, "--screenshots") ?? throw new ArgumentException("--screenshots output is required."));
if (scenario == "pilot-consolidation-copy")
    return PilotConsolidationSmoke.ExistingCopy(Option(args, "--screenshots")!, Option(args, "--confirm-plan")!);
if (scenario == "pilot-folder-copy")
    return PilotConsolidationSmoke.FolderCopy(Option(args, "--screenshots")!);
var bulkDrag = scenario == "bulk-drag-events";
if (bulkDrag) scenario = "drag-events";
if (scenario == "bulk-performance")
    return BulkPerformanceSmoke.Run(app, Option(args, "--screenshots")!);
if (scenario == "bulk-headless")
    return BulkPerformanceSmoke.Headless(Option(args, "--screenshots")!);
if (scenario == "bulk-scale")
    return BulkPerformanceSmoke.Headless(Option(args, "--screenshots")!, fullScale: true);
if (scenario == "bulk-workflow")
    return BulkWorkflowSmoke.Run(app, Option(args, "--screenshots")!);
if (scenario == "pilot-consolidation-workflow")
    return BulkWorkflowSmoke.Run(app, Option(args, "--screenshots")!, consolidatePilots: true);
if (scenario == "storage-contention")
    return StorageContentionSmoke.Run(app, Option(args, "--screenshots")!);
if (scenario == "adoption-cleanup")
    return AdoptionCleanupSmoke.Run(app, Option(args, "--screenshots")!);
if (scenario == "account-authorize")
    return AccountAuthorizeSmoke.Run(app, Option(args, "--screenshots"));
if (scenario == "filed-folder-delete")
    return await FiledFolderDeleteSmoke.Run(app, Option(args, "--screenshots"));
if (scenario == "local-root")
    return LocalRootSmoke.Run(app, Option(args, "--screenshots"));
if (scenario == "archive-workflow")
    return ArchiveWorkflowSmoke.Run(app, Option(args, "--screenshots"));
if (scenario == "archive-tree")
    return ArchiveTreeSmoke.Run(app, Option(args, "--root") ?? Path.Combine(Path.GetTempPath(), "mailkeep-archive-tree-" + Guid.NewGuid().ToString("N")));
if (scenario == "archive-toolbar")
    return ArchiveToolbarSmoke.Run(app, Option(args, "--root") ?? Path.Combine(Path.GetTempPath(), "mailkeep-archive-toolbar-" + Guid.NewGuid().ToString("N")));
if (scenario == "layout-json")
{
    // Compare the startup reader with the historical reflection contract, without opening a window/store.
    var assembly = System.Reflection.Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(app))!, "Mailkeep.WinForms.dll"));
    var type = assembly.GetType("Mailkeep.WinForms.WindowLayout", true)!;
    var read = type.GetMethod("Read")!;
    var write = type.GetMethod("ToJson")!;
    string[] fixtures = [
        "{}",
        "{\"SelectedFolder\":\"local:inbox\"}",
        "{\"Bounds\":{\"X\":80,\"Y\":90,\"Width\":1100,\"Height\":720},\"Maximized\":false,\"Dpi\":96,\"FolderWidth\":320,\"LocalFraction\":0.5,\"MessageFraction\":0.55,\"MessageColumns\":[185,350,130,145,80]}",
        "{\"Bounds\":{\"X\":-1920,\"Y\":-400,\"Width\":1200,\"Height\":1160},\"Maximized\":true,\"Dpi\":144,\"FolderWidth\":375,\"LocalFraction\":0.38,\"MessageFraction\":0.4,\"MessageColumns\":[235,410,120,140,72],\"MessageColumnOrder\":[1,0,2,4,3],\"ExpandedFolders\":[\"local:inbox\",\"archive:Work\"],\"MessageSplit\":480,\"LocalSplit\":310,\"SelectedAccount\":\"fixture\",\"SelectedFolder\":\"local:inbox\",\"FutureSetting\":true}",
        "{\"Bounds\":{\"X\":0,\"Y\":0,\"Width\":0,\"Height\":0},\"MessageColumns\":[],\"ExpandedFolders\":[],\"SelectedAccount\":null,\"selectedFolder\":\"ignored-case\"}",
        "{\"Bounds\":{\"Location\":{\"X\":-100,\"Y\":200},\"Size\":{\"Width\":1100,\"Height\":720},\"X\":80}}",
        "{\"Bounds\":{\"X\":80,\"Width\":900,\"Location\":{\"Y\":90},\"Size\":{\"Height\":720}}}",
        "{\"MessageSplit\":100,\"MessageSplit\":200,\"ExpandedFolders\":[null,\"\"],\"Bounds\":{\"IsEmpty\":false,\"Left\":123}}"
    ];
    foreach (var json in fixtures)
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize(json, type)!;
        var parsed = read.Invoke(null, [json])!;
        var expected = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(legacy, type));
        var actual = System.Text.Json.Nodes.JsonNode.Parse((string)write.Invoke(parsed, null)!);
        Check(System.Text.Json.Nodes.JsonNode.DeepEquals(expected, actual), "layout JSON changed the legacy contract");
        var roundTrip = read.Invoke(null, [actual!.ToJsonString()])!;
        Check(System.Text.Json.Nodes.JsonNode.DeepEquals(actual,
            System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(roundTrip, type))), "layout did not round-trip");
    }
    foreach (var json in new string?[] { null, "null", "{", "[]", "{\"Dpi\":\"invalid\"}", "{\"Bounds\":null}",
        "{\"Bounds\":{\"Location\":null}}", "{\"MessageColumns\":[null]}", "{\"Maximized\":null}", "{\"Dpi\":999999999999}" })
        Check(read.Invoke(null, [json]) is null, "invalid layout must fall back to defaults");
    Console.WriteLine("PASS layout-json legacy-contract=8 round-trip=8 invalid=10 bounds=negative/default/maximized setter-order=preserved columns=preserved");
    return 0;
}
if (scenario == "message-icons")
{
    // Render the published image list without opening or activating any window.
    var assembly = System.Reflection.Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(app))!, "Mailkeep.WinForms.dll"));
    var type = assembly.GetType("Mailkeep.WinForms.MainForm", true)!;
    using var icons = (System.Windows.Forms.ImageList)type.GetMethod("MessageImages", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null)!;
    using var proof = new System.Drawing.Bitmap(icons.Images.Count * 48, 57);
    using var graphics = System.Drawing.Graphics.FromImage(proof);
    graphics.Clear(System.Drawing.Color.White);
    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
    for (var i = 0; i < icons.Images.Count; i++)
    {
        using var icon = new System.Drawing.Bitmap(icons.Images[i]);
        if (i is 2 or 3 or 6 or 7)
        {
            var blue = 0;
            for (var y = 0; y < icon.Height; y++)
                for (var x = 0; x < icon.Width; x++)
                    if (icon.GetPixel(x, y).ToArgb() == System.Drawing.Color.FromArgb(0, 70, 200).ToArgb()) blue++;
            if (blue < 8) throw new InvalidOperationException("reply arrow is not blue: " + i);
        }
        graphics.DrawImage(icon, new System.Drawing.Rectangle(i * 48, 0, 48, 57));
    }
    proof.Save(Option(args, "--output") ?? "message-icons.png");
    Console.WriteLine("PASS message-icons replied=blue read,unread,both published-image-list rendered-without-windows");
    return 0;
}
if (scenario == "browser-runtime")
{
    // No windows or mail store: distinguish a missing WebView runtime before guest UI checks.
    var directory = Path.GetDirectoryName(Path.GetFullPath(app))!;
    var loader = Path.Combine(directory, "WebView2Loader.dll");
    if (!File.Exists(loader)) loader = Path.Combine(directory, "runtimes", "win-x64", "native", "WebView2Loader.dll");
    NativeLibrary.Load(loader);
    var core = System.Reflection.Assembly.LoadFrom(Path.Combine(directory, "Microsoft.Web.WebView2.Core.dll"));
    var environment = core.GetType("Microsoft.Web.WebView2.Core.CoreWebView2Environment", true)!;
    try
    {
        Console.WriteLine("PASS browser-runtime version=" + environment.GetMethod("GetAvailableBrowserVersionString", [typeof(string)])!.Invoke(null, [null]));
        return 0;
    }
    catch (System.Reflection.TargetInvocationException error)
    {
        Console.WriteLine("FAIL browser-runtime " + error.InnerException!.GetType().Name + ": " + error.InnerException.Message);
        return 3;
    }
}
if (scenario == "live-destinations")
{
    // Read-only: the folders the filing-shortcut drop-downs offer for the real store.
    var live = new MailkeepServices();
    var output = Option(args, "--output") ?? Path.Combine(Path.GetTempPath(), "filing-tree.png");
    Console.WriteLine($"count={Mailkeep.WinForms.UiSmokeAccess.RenderTree(app, live, output)} image={output}");
    return 0;
}
if (scenario == "live-state")
{
    // Read-only: what the running client shows — status line, band, list size, selected account row.
    var live = AutomationElement.FromHandle(FindWindow(null, FindWindowTitle()));
    string Named(string name) => FindNamed(live, name) is { } element ? element.Current.Name : "?";
    var accounts = FindNamed(live, "Connected accounts");
    var selected = accounts.FindAll(TreeScope.Children, new PropertyCondition(SelectionItemPattern.IsSelectedProperty, true))
        .Cast<AutomationElement>().Select(row => row.Current.Name).ToArray();
    var list = FindNamed(live, "Message list");
    var rows = (int)SendMessage(NativeHandle(list), 0x1004, IntPtr.Zero, IntPtr.Zero);
    var statusBar = live.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.StatusBar));
    var texts = statusBar?.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Select(e => e.Current.Name).Where(n => n.Length > 0) ?? [];
    Console.WriteLine($"title={live.Current.Name} rows={rows} selected-accounts=[{string.Join(", ", selected)}] status=[{string.Join(" | ", texts)}]");
    return 0;

    static string FindWindowTitle()
    {
        foreach (var process in Process.GetProcessesByName("Mailkeep.WinForms"))
            if (process.MainWindowTitle.Length > 0) return process.MainWindowTitle;
        return "Inbox - Mailkeep";
    }
}
if (scenario == "live-account-switch")
{
    // Changes only which account the running client shows, and ends back on the one it started on.
    var handle = Process.GetProcessesByName("Mailkeep.WinForms").Select(process => process.MainWindowHandle).First(value => value != IntPtr.Zero);
    var live = AutomationElement.FromHandle(handle);
    var accounts = FindNamed(live, "Connected accounts");
    var list = NativeHandle(FindNamed(live, "Message list"));
    var statusBar = live.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.StatusBar));
    string Status() => string.Join(" | ", statusBar.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Select(e => e.Current.Name).Where(n => n.Length > 0));
    var rows = accounts.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true)).Cast<AutomationElement>().ToArray();
    var start = rows.FirstOrDefault(row => ((SelectionItemPattern)row.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected);
    if (Option(args, "--account") is { } only)
    {
        // A local folder first, so the account click is a real change of view.
        Select(FindNamed(live, "Local mail folders").FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem))
            .Cast<AutomationElement>().FirstOrDefault(node => node.Current.Name.StartsWith("Drafts")) ?? throw new InvalidOperationException("no Drafts node"));
        Thread.Sleep(800);
        var target = rows.Single(row => row.Current.Name.Contains(only, StringComparison.OrdinalIgnoreCase));
        var watch = Stopwatch.StartNew();
        ((SelectionItemPattern)target.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        var last = "";
        while (watch.ElapsedMilliseconds < 30000)
        {
            var now = Status();
            if (now != last) { Console.WriteLine($"+{watch.ElapsedMilliseconds} ms [{now}] rows={(int)SendMessage(list, 0x1004, IntPtr.Zero, IntPtr.Zero)}"); last = now; }
            if (now.Contains("new mail") || now.Contains(" new |") || now.Contains("could not check")) break;
            Thread.Sleep(20);
        }
        return 0;
    }
    foreach (var row in rows.Take(6).Append(start).Where(row => row is not null))
    {
        var name = row!.Current.Name;
        var clock = Stopwatch.StartNew();
        ((SelectionItemPattern)row.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        string now = "";
        while (clock.ElapsedMilliseconds < 5000 && !(now = Status()).Contains(name, StringComparison.OrdinalIgnoreCase)) Thread.Sleep(5);
        Console.WriteLine($"{name}: status after {clock.ElapsedMilliseconds} ms -> [{now}] rows={(int)SendMessage(list, 0x1004, IntPtr.Zero, IntPtr.Zero)}");
        Thread.Sleep(300);
    }
    return 0;
}
if (scenario == "live-split")
{
    // Read-only: measures the running client's local/connected split.
    var live = AutomationElement.FromHandle(FindWindow(null, "Inbox - Mailkeep"));
    var pane = FindNamed(live, "Local and connected mail splitter").Current.BoundingRectangle;
    var tree = FindNamed(live, "Local mail folders").Current.BoundingRectangle;
    Console.WriteLine($"live-split pane={pane} tree={tree} fraction={(tree.Bottom - pane.Top) / (pane.Height - 4):F3}");
    return 0;
}
if (scenario == "store-profile")
    return StoreProfileSmoke.Run(Option(args, "--root") ?? throw new ArgumentException("--root PATH is required."),
        int.Parse(Option(args, "--rounds") ?? "3"));
if (scenario == "oe-import-check")
{
    // OE Classic's real address book, read the way the client reads it (from a copy) and imported into a store
    // made for the purpose and deleted afterwards. The live Mailkeep store is not opened.
    var book = OeAddressBook.DefaultPath;
    var before = File.ReadAllBytes(book);
    var scratch = Path.Combine(Path.GetTempPath(), "mailkeep-oe-import-check", Guid.NewGuid().ToString("N"));
    try
    {
        var store = new MailkeepServices(scratch);
        store.Database.Initialize();
        var read = OeAddressBook.Read(book);
        var first = store.Database.ImportContacts(read);
        var second = store.Database.ImportContacts(read);
        var unchanged = before.SequenceEqual(File.ReadAllBytes(book));
        Console.WriteLine($"{(unchanged && first.Added > 0 && second.Added == 0 ? "PASS" : "FAIL")} oe-import-check read={read.Count} "
            + $"added={first.Added} held={first.AlreadyHeld} unusable={first.Unusable} reimport_added={second.Added} "
            + $"named={read.Count(contact => contact.Name.Length > 0)} oe_book_unchanged={unchanged}");
        return unchanged && first.Added > 0 && second.Added == 0 ? 0 : 1;
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(scratch, true); } catch (IOException) { }
    }
}
if (scenario == "image-fetch")
{
    await RemoteImageFetchSmoke.Run(app);
    return 0;
}
var readingFixture = scenario is "reading" or "image-menu" or "startup-reading" or "remote-images" or "search" or "search-filters" or "sender-search" or "search-reader" or "compose" or "compose-attachments" or "attachment-open" or "print-html" or "shortcuts";
var isDll = app.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
var root = Path.Combine(Path.GetTempPath(), "mailkeep-ui-smoke", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var services = new MailkeepServices(root);
services.Database.Initialize();
services.Accounts.Save([
    new MailAccount("alpha-fixture", "custom", "alpha@example.invalid", "INBOX", "Alpha fixture",
        "imap.example.invalid", null, "secret", new KeepAliveSettings(), MailboxModes.Connected,
        new IncomingServerSettings(MailProtocols.Imap, "imap.example.invalid", 993)),
    new MailAccount("connected-fixture", "custom", "connected@example.invalid", "INBOX", "Connected fixture",
        "imap.example.invalid", null, "secret", new KeepAliveSettings(), MailboxModes.Connected,
        new IncomingServerSettings(MailProtocols.Imap, "imap.example.invalid", 993)),
    new MailAccount("zeta-fixture", "custom", "zeta@example.invalid", "INBOX", "Zeta fixture",
        "imap.example.invalid", null, "secret", new KeepAliveSettings(), MailboxModes.Connected,
        new IncomingServerSettings(MailProtocols.Imap, "imap.example.invalid", 993)),
    new MailAccount("local-fixture", "custom", "local@example.invalid", "INBOX", LocalFolderKinds.Inbox,
        "pop.example.invalid", null, "secret", new KeepAliveSettings(), MailboxModes.LocalKeep,
        new IncomingServerSettings(MailProtocols.Pop3, "pop.example.invalid", 995))
]);
// An expired sign-in, as Microsoft reports it, so the Accounts window shows its short Fetched text.
if (scenario == "accounts")
{
    var expired = services.Database.LogFetch("connected-fixture", "connected@example.invalid", "scheduled", DateTimeOffset.UtcNow.AddMinutes(-5));
    services.Database.FinishFetch(expired, DateTimeOffset.UtcNow.AddMinutes(-5), 0, 0, 0,
        "OAuth token request failed (400): {\"error\":\"invalid_grant\",\"error_description\":\"AADSTS70000: The provided value for the input parameter 'refresh_token' or 'assertion' is not valid. The grant is expired. The user must sign in again.\"}");
}
// Send Outbox has to fail through the product's own SMTP path, so the scenario needs a real socket rather
// than a substituted transport. A loopback listener that accepts and hangs up makes every attempt fail the
// same way with no DNS lookup, no packet leaving the machine and no chance of delivering mail.
var sendingServer = new OutgoingServerSettings("smtp.example.invalid", 587);
if (scenario == "outbox-send")
{
    var blackhole = new TcpListener(IPAddress.Loopback, 0);
    blackhole.Start();
    sendingServer = new OutgoingServerSettings("127.0.0.1", ((IPEndPoint)blackhole.LocalEndpoint).Port, MailSecurityModes.None);
    _ = Task.Run(async () =>
    {
        try { while (true) (await blackhole.AcceptTcpClientAsync()).Close(); }
        catch { /* the listener dies with the test process */ }
    });
}
// Invented addresses only: these windows appear on a real desktop, and a fixture that looks like someone's
// own mail is a fixture they will misread as their own mail.
services.Identities.Save([
    new SendingIdentity("fixture-1", null, "Untested One", "one@example.invalid", null, false, "gmail",
        new OutgoingServerSettings("smtp.example.invalid", 465), null, null, null),
    new SendingIdentity("fixture-2", null, "Untested Two", "two@example.invalid", null, false, "microsoft",
        new OutgoingServerSettings("smtp.example.invalid", 587), null, null, null),
    // Only this one has been configured and tested, so compose is reachable while the others stay locked.
    new SendingIdentity("fixture-3", null, "Tested Three", "three@example.invalid", null, true, "microsoft",
        sendingServer, null, null, "fixture-secret", SendingIdentityStates.Ready)
]);
TcpListener? imageServer = null;
var imageRequests = 0;
string? imageOrigin = null;
if (scenario == "remote-images")
{
    imageServer = new TcpListener(IPAddress.Loopback, 0);
    imageServer.Start();
    imageOrigin = "http://127.0.0.1:" + ((IPEndPoint)imageServer.LocalEndpoint).Port;
    _ = Task.Run(async () =>
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        try
        {
            while (true)
            {
                using var client = await imageServer.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                Interlocked.Increment(ref imageRequests);
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {png.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header);
                await stream.WriteAsync(png);
            }
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    });
}
var readingHtml = scenario == "remote-images"
    ? $"<html><body style='background:#dff0df'><h1>Remote image fixture</h1><img src='{imageOrigin}/pixel.png' width='100' height='70'></body></html>"
    : "<html><body style='background:#dff0df'><h1>HTML reading fixture</h1><p>Formatted message body.</p><img src='cid:fixture@inline' width='100' height='70'></body></html>";
var attachmentOpenName = "Mailkeep open " + Guid.NewGuid().ToString("N")[..8];
string? readingRaw = null;
if (readingFixture)
{
    var builder = new MimeKit.BodyBuilder { TextBody = "Fixture body", HtmlBody = readingHtml };
    var imagePixels = Enumerable.Range(0, 32 * 32).SelectMany(_ => new byte[] { 255, 0, 255, 255 }).ToArray();
    var inlineBitmap = System.Windows.Media.Imaging.BitmapSource.Create(32, 32, 96, 96,
        System.Windows.Media.PixelFormats.Bgra32, null, imagePixels, 32 * 4);
    var inlineEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
    inlineEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(inlineBitmap));
    using var inlineBytes = new MemoryStream();
    inlineEncoder.Save(inlineBytes);
    builder.LinkedResources.Add("inline.png", inlineBytes.ToArray(), new MimeKit.ContentType("image", "png")).ContentId = "fixture@inline";
    builder.Attachments.Add(scenario == "attachment-open" ? attachmentOpenName + " first.txt" : "fixture.txt", Encoding.UTF8.GetBytes("First attachment fixture"));
    builder.Attachments.Add(scenario == "attachment-open" ? attachmentOpenName + " second.txt" : "second.txt", Encoding.UTF8.GetBytes("Second attachment fixture"));
    if (scenario == "attachment-open")
        builder.Attachments.Add("invoice.exe.txt", Encoding.UTF8.GetBytes("Inert text fixture"));
    using var message = new MimeKit.MimeMessage { Body = builder.ToMessageBody() };
    using var bytes = new MemoryStream();
    message.WriteTo(bytes);
    readingRaw = services.Blobs.Put(bytes.ToArray());
}
services.Database.Upsert(new MessageInput(MailContract.LiveSource, "local-fixture", LocalFolderKinds.Inbox, "fixture-1", null, 0,
    "Automation fixture", "sender@example.invalid", "Sender", ["local@example.invalid"], DateTimeOffset.UtcNow,
    128, false, readingRaw is null ? 0 : 2, scenario == "print-pages"
        ? "START OF PRINT FIXTURE\r\n" + string.Join("\r\n", Enumerable.Range(1, 500).Select(index => $"Line {index:000}: Printable message content.")) + "\r\nEND OF PRINT FIXTURE"
        : "Fixture body", readingFixture ? readingHtml : null, readingRaw));
if (scenario is "reading" or "startup-reading" or "remote-images" or "filing" or "move-filing" or "drag-filing" or "drag-events" or "read-state" or "latency" or "marks" or "filing-shortcuts" or "shortcuts" or "selection-transition")
    services.Database.Upsert(new MessageInput(MailContract.LiveSource, "local-fixture", LocalFolderKinds.Inbox, "fixture-2", null, 0,
        "Second reading fixture", "second@example.invalid", "Second sender", ["local@example.invalid"], DateTimeOffset.UtcNow.AddHours(-1),
        128, false, 0, "Second fixture body", null, null));
// Each sort must have one unambiguous winner: alpha holds the most mail, zeta holds the most recent,
// and alpha sorts first alphabetically. Dates are hours apart so ordering never depends on write timing.
for (var index = 0; index < 3; index++)
    services.Database.Upsert(new MessageInput(MailContract.LiveSource, "alpha-fixture", "Alpha fixture", $"alpha-{index}", null, 0,
        $"Alpha {index}", "sender@example.invalid", "Sender", ["alpha@example.invalid"], DateTimeOffset.UtcNow.AddHours(-3 - index),
        128, true, 0, "Fixture body", null, null));
services.Database.MarkSeen("alpha-fixture", "Alpha fixture");
services.Database.Upsert(new MessageInput(MailContract.LiveSource, "connected-fixture", "Connected fixture", "connected-1", null, 0,
    "Connected unread", "sender@example.invalid", "Sender", ["connected@example.invalid"], DateTimeOffset.UtcNow.AddHours(-2),
    128, false, 0, "Fixture body", null, null));
for (var index = 0; index < 2; index++)
    services.Database.Upsert(new MessageInput(MailContract.LiveSource, "zeta-fixture", "Zeta fixture", $"zeta-{index}", null, 0,
        $"Zeta unread {index}", "sender@example.invalid", "Sender", ["zeta@example.invalid"], DateTimeOffset.UtcNow.AddMinutes(-index),
        128, false, 0, "Fixture body", null, null));

Process? process = null;
if (scenario == "outbox-edit")
{
    var attachment = Path.Combine(root, "queued-fixture.txt");
    File.WriteAllText(attachment, "Queued attachment bytes");
    var draft = services.Send.SaveDraft("fixture-3", ["recipient@example.invalid"], "Queued edit fixture", "Queued body",
        attachmentPaths: [attachment]);
    await services.Send.QueueAsync(draft.Id);
    File.Delete(attachment);
}
// The real mailbox has twenty-five connected accounts holding hundreds of messages each, so the accounts
// list scrolls and every switch waits on a real database read. A three-account fixture proves neither.
if (scenario == "selection")
    for (var account = 0; account < 25; account++)
    {
        var name = $"bulk-{account:00}";
        services.Accounts.Save([.. services.Accounts.Load(),
            new MailAccount(name, "custom", $"{name}@example.invalid", "INBOX", $"Bulk {account:00}",
                "imap.example.invalid", null, "secret", new KeepAliveSettings(), MailboxModes.Connected,
                new IncomingServerSettings(MailProtocols.Imap, "imap.example.invalid", 993))]);
        // A different count per account, so a list that failed to repopulate reads as the wrong number
        // rather than as a plausible one.
        for (var index = 0; index < 100 + account; index++)
            services.Database.Upsert(new MessageInput(MailContract.LiveSource, name, $"Bulk {account:00}", $"{name}-{index}", null, 0,
                $"Bulk {account:00} message {index}", "sender@example.invalid", "Sender", [$"{name}@example.invalid"],
                DateTimeOffset.UtcNow.AddMinutes(-index), 128, true, 0, "Fixture body", null, null));
    }
// Two queued messages, because the retry bug only shows when a confirmed send has more than one thing to do
// and the earlier failure has already pushed them into backoff.
if (scenario == "outbox-send")
    foreach (var subject in new[] { "Outbox retry one", "Outbox retry two" })
        await services.Send.QueueAsync(services.Send.SaveDraft("fixture-3", ["recipient@example.invalid"], subject, "Queued body").Id);
if (scenario is "filing" or "move-filing" or "drag-filing" or "drag-events" or "connected-copy" or "latency" or "filing-shortcuts")
{
    services.Database.CreateLocalFolder("Filed fixture", null);
    services.Database.Upsert(new MessageInput(MailContract.LiveSource, "fixture-3", LocalFolderKinds.Sent,
        "sent:filing-fixture", null, 0, "Sent filing fixture", "three@example.invalid", "Tested Three",
        ["recipient@example.invalid"], DateTimeOffset.UtcNow, 128, true, 0, "Sent body", null, null));
}
if (scenario is "filing" or "move-filing" or "drag-filing")
{
    var original = services.Database.ListMessages().First(message => message.Account == "local-fixture");
    services.Database.Upsert(new MessageInput(MailContract.ArchiveSource, "archive-fixture", "Inbox", "imported-copy", null, 0,
        original.Subject, original.Sender, "Sender", [], original.Date, 128, true, 0, "Imported copy", null, null));
}
if (scenario == "read-state")
    foreach (var original in services.Database.ListMessages().Where(message => message.Account == "local-fixture"))
        services.Database.Upsert(new MessageInput(MailContract.ArchiveSource, "archive-fixture", "Inbox", "copy:" + original.Id,
            null, 0, original.Subject, original.Sender, "Sender", [], original.Date, 128, false, 0, "Imported copy", null, null));
if (scenario == "drafts-list")
    DraftsListSmoke.Seed(services);
if (scenario == "split-restore")
    services.Database.SetSetting("window.layout", System.Text.Json.JsonSerializer.Serialize(new
    {
        Bounds = new { X = 320, Y = 170, Width = 1280, Height = 820 }, Maximized = true, Dpi = 96,
        FolderWidth = 260, LocalFraction = 0.9007936507936508, MessageFraction = 0.5
    }));
if (scenario == "tree-render")
{
    var health = services.Database.CreateLocalFolder("Health", null);
    for (var index = 0; index < 265; index++)
        services.Database.Upsert(new MessageInput(MailContract.LiveSource, "local-fixture", health.Key, "health-" + index, null, 0,
            "Health " + index, "sender@example.invalid", "Sender", [], DateTimeOffset.UtcNow.AddMinutes(-index), 1, false, 0, "Body", null, null));
}
if (scenario is "search-folders" or "folders")
{
    var parent = services.Database.CreateLocalFolder("Projects", null);
    var child = services.Database.CreateLocalFolder("Child", parent.Key);
    var other = services.Database.CreateLocalFolder("Other", null);
    var otherChild = services.Database.CreateLocalFolder("Child", other.Key);
    foreach (var folder in new[] { parent, child, otherChild })
        services.Database.Upsert(new MessageInput(MailContract.LiveSource, "local-fixture", folder.Key, folder.Key, null, 0,
            "Custom folder " + folder.Key, "sender@example.invalid", "Sender", [], DateTimeOffset.UtcNow, 1, false, 0,
            "Custom folder fixture", null, null));
}
if (scenario == "sender-search")
{
    foreach (var (key, sender, name) in new[] {
        ("address", "helpdesk@harbourtonfielding.invalid", "Broker fixture"),
        ("name", "name@example.invalid", "Harbourton Lansdown"),
        ("body-only", "other@example.invalid", "Unrelated") })
        services.Database.Upsert(new MessageInput(MailContract.LiveSource, "local-fixture", LocalFolderKinds.Inbox,
            key, null, 0, "Partial sender " + key, sender, name, ["fixture@example.invalid"],
            DateTimeOffset.UtcNow, 128, true, 0, "harbourton fielding body-only noise", null, null));
    var location = Path.Combine(root, "portable-fixture");
    var file = Path.Combine(location, FolderBundle.FileName);
    using (var bundle = FolderBundle.Create(file, "Portable fixture", "local"))
        bundle.Add(Encoding.UTF8.GetBytes("From: Portable <archive@harbourtonfielding.invalid>\r\nSubject: Portable sender\r\n\r\nRetained raw bytes"),
            true, "fixture:sender", "2026-01-01T00:00:00Z", 1);
    ArchiveManifest.Register(location, file);
    new ArchiveStore(services.Paths).Add(location);
}
if (scenario == "search")
{
    services.Catalog.SyncOperational();
    services.Catalog.SyncAttachments();
}
if (scenario == "startup-reading")
{
    Check(Mailkeep.WinForms.UiSmokeAccess.IsInsidersFixture(app), "Insiders fixture required for cache-free portable reading");
    // The generic fixture also seeds plain messages as projection-only rows. Give those invented
    // messages real MIME before converting: the production converter must still reject missing bytes.
    using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = services.Paths.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
    {
        connection.Open();
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT id,subject,body_text FROM messages WHERE raw_path IS NULL";
        var missing = new List<(long Id, string Subject, string Body)>();
        using (var rows = select.ExecuteReader())
            while (rows.Read()) missing.Add((rows.GetInt64(0), rows.GetString(1), rows.GetString(2)));
        foreach (var row in missing)
        {
            using var mime = new MimeKit.MimeMessage { Subject = row.Subject, Body = new MimeKit.TextPart("plain") { Text = row.Body } };
            using var bytes = new MemoryStream();
            mime.WriteTo(bytes);
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE messages SET raw_path=$raw WHERE id=$id";
            update.Parameters.AddWithValue("$raw", services.Blobs.Put(bytes.ToArray()));
            update.Parameters.AddWithValue("$id", row.Id);
            update.ExecuteNonQuery();
        }
    }
    var portable = new BulkFolderMigration(services.Paths);
    portable.Run(portable.Preview().Confirmation, publish: true);
    Directory.Delete(services.Paths.BlobDirectory, recursive: true);
}
if (scenario == "layout")
    services.Database.SetSetting("window.layout", System.Text.Json.JsonSerializer.Serialize(new
    {
        Bounds = new { X = 80, Y = 90, Width = 1100, Height = 720 }, Maximized = false, Dpi = 96,
        FolderWidth = 320, LocalFraction = 0.5, MessageFraction = 0.55,
        MessageColumns = new[] { 185, 350, 130, 145, 80 }
    }));
try
{
    if (scenario == "toolbar-hover")
    {
        ToolbarHoverSmoke.Run(app, root, services, Option(args, "--screenshots"));
        return 0;
    }
    if (scenario is "pane-navigation" or "pane-navigation-legacy" or "selection-anchor" or "digest-rendering" or "digest-rendering-legacy")
    {
        PaneNavigationSmoke.Run(app, root, services, Option(args, "--screenshots"),
            digest: scenario.StartsWith("digest-rendering"), legacyInput: scenario.EndsWith("-legacy"), selectionAnchor: scenario == "selection-anchor");
        return 0;
    }
    if (scenario == "background-launch")
    {
        PaneNavigationSmoke.BackgroundLaunch(app, root, services, Option(args, "--screenshots"));
        return 0;
    }
    if (scenario == "startup")
    {
        StartupSmoke.Run(app, root, services, Option(args, "--screenshots"));
        return 0;
    }
    if (scenario == "reply")
    {
        ReplySmoke.Run(app, services);
        return 0;
    }
    if (scenario == "print-html")
    {
        HtmlPrintSmoke.Run(app, services, Option(args, "--screenshots") ?? Path.Combine(root, "print-proof"));
        return 0;
    }
    if (scenario == "print-pages")
    {
        PrintSmoke.Run(app, services, Option(args, "--screenshots"));
        return 0;
    }
    if (scenario == "drag-events")
    {
        DragEventSmoke.Run(app, root, services, bulkDrag, Option(args, "--screenshots"));
        return 0;
    }
    if (scenario == "toolbar-find-layout")
    {
        ToolbarFindSmoke.Run(app, root, services, Option(args, "--screenshots"));
        return 0;
    }
    if (scenario == "export-pictures")
    {
        ExportPicturesSmoke.Run(app, root, services, Option(args, "--screenshots"));
        return 0;
    }
    if (scenario is "shortcuts" or "compose-input" or "compose-send" or "send-recovery")
    {
        ShortcutSmoke.Run(app, root, services, scenario == "compose-input", Option(args, "--screenshots"),
            scenario == "compose-send", scenario == "send-recovery");
        if (scenario == "send-recovery") ExportFixtureDatabase();
        return 0;
    }
    if (scenario == "image-policies")
    {
        ImagePolicySmoke.Run(app, root, services);
        return 0;
    }
    if (scenario == "image-site-scope")
    {
        ImageSiteScopeSmoke.Run(app, services, Option(args, "--screenshots"));
        return 0;
    }
    if (scenario == "outbox-edit")
    {
        OutboxEditSmoke.Run(app, root, services);
        return 0;
    }
    if (scenario == "ui-blocking")
    {
        UiBlockingSmoke.Run(app, root, services);
        return 0;
    }
    if (scenario == "receive-redraw")
    {
        ReceiveRedrawSmoke.Run(app, root, services);
        return 0;
    }
    if (scenario == "selection-transition")
    {
        SelectionTransitionSmoke.Run(app, root, services, Option(args, "--screenshots"));
        return 0;
    }
    if (scenario == "receive-scope")
    {
        ReceiveScopeSmoke.Run(app, root, services);
        return 0;
    }
    if (scenario == "remote-queue")
    {
        RemoteQueueSmoke.Run(app, root, services, Option(args, "--screenshots"), Option(args, "--real-account"), Option(args, "--cli"));
        return 0;
    }
    if (scenario == "permanent-delete")
    {
        PermanentDeleteSmoke.Run(app, root, services);
        return 0;
    }
    if (scenario == "image-loading")
    {
        ImageLoadingSmoke.Run(app, root, services, Option(args, "--screenshots"));
        return 0;
    }
    if (scenario is "draft-drag" or "draft-drag-converted")
    {
        DraftDragSmoke.Run(app, root, services, Option(args, "--screenshots"), args.Contains("--physical-drag"), scenario == "draft-drag-converted");
        return 0;
    }
    if (scenario == "image-permission-bar")
    {
        await ImagePermissionBarSmoke.Run(app, root, services, Option(args, "--screenshots"));
        return 0;
    }
    if (scenario == "oe-draft-conversion")
    {
        await OeDraftConversionSmoke.Run(app, root, services);
        return 0;
    }
    if (scenario == "html-preview")
    {
        HtmlPreviewSmoke.Run(app, root, Option(args, "--screenshots"));
        return 0;
    }
    if (scenario == "connected-refresh")
    {
        await ConnectedRefreshSmoke.Run(app, Option(args, "--screenshots") ?? Path.Combine(root, "shots"));
        return 0;
    }
    if (scenario == "preview-latency")
    {
        await PreviewLatencySmoke.Run(app, root, services);
        return 0;
    }
    if (scenario is "compose-window" or "compose-window-abort")
    {
        ComposeWindowSmoke.Run(app, root, services, Option(args, "--screenshots"), scenario == "compose-window-abort");
        return 0;
    }
    if (scenario == "download-progress")
    {
        await DownloadProgressSmoke.Run(app, root, services, Option(args, "--screenshots"));
        ExportFixtureDatabase();
        return 0;
    }
    if (scenario == "arrival-scroll")
    {
        ArrivalScrollSmoke.Run(app, root, services, Option(args, "--screenshots"));
        return 0;
    }
    if (scenario == "sidebar-state")
    {
        SidebarStateSmoke.Run(app, root, services);
        return 0;
    }
    if (scenario == "filing-shortcuts")
    {
        FilingShortcutSmoke.Run(app, root, services);
        return 0;
    }
    if (scenario is "mixed-sort" or "search-reader")
    {
        SearchWorkflowSmoke.Run(app, root, services, scenario, Option(args, "--screenshots"));
        return 0;
    }
    if (scenario == "drafts-list")
    {
        DraftsListSmoke.Run(app, root);
        return 0;
    }
    if (scenario == "find-latency")
        return FindLatencySmoke.Run(app, root, services, int.Parse(Option(args, "--limit-ms") ?? "100"));
    if (scenario == "split-save")
    {
        // In process: move the local/connected splitter and read the store back without closing the window.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.ThrowException);
                var assembly = System.Reflection.Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(app))!, "Mailkeep.WinForms.dll"));
                var type = assembly.GetType("Mailkeep.WinForms.MainForm", true)!;
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var form = (System.Windows.Forms.Form)Activator.CreateInstance(type, [root])!;
                form.Show();
                var clock = Stopwatch.StartNew();
                while (!(bool)type.GetField("_ready", flags)!.GetValue(form)! || clock.Elapsed < TimeSpan.FromSeconds(3))
                { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(10); }
                var split = (System.Windows.Forms.SplitContainer)type.GetField("_folderSplit", flags)!.GetValue(form)!;
                var before = services.Database.GetSetting("window.layout");
                split.SplitterDistance = 150;
                var expected = 150.0 / (split.Height - split.SplitterWidth);
                clock.Restart();
                while (clock.Elapsed < TimeSpan.FromSeconds(2)) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(10); }
                var saved = services.Database.GetSetting("window.layout");
                var fraction = saved is null ? -1 : System.Text.Json.JsonDocument.Parse(saved).RootElement.GetProperty("LocalFraction").GetDouble();
                if (Math.Abs(fraction - expected) > 0.005) throw new InvalidOperationException($"moved split not saved before closing: saved={fraction:F3} expected={expected:F3} before={(before is null ? "none" : "set")}");
                Console.WriteLine($"PASS split-save saved-without-closing fraction={fraction:F3}");
                form.Dispose();
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60))) throw new TimeoutException("split-save did not finish");
        if (failure is not null) throw failure;
        return 0;
    }
    if (scenario == "marks")
    {
        // Written out here as well: a failure leaves files open, and the clean-up's own error would hide it.
        try { AddressingSmoke.Marks(app, root, services, Option(args, "--screenshots"), Option(args, "--live-marks"), Option(args, "--live-metadata")); }
        catch (Exception error) { Console.Error.WriteLine(error); throw; }
        return 0;
    }
    if (scenario == "latency")
        return LatencySmoke.Run(app, root, services, int.Parse(Option(args, "--archive-rows") ?? "250000"),
            int.Parse(Option(args, "--limit-ms") ?? "250"));
    if (scenario == "cli-send")
    {
        await CliSendSmoke.Run(app, root, services);
        return 0;
    }
    var start = isDll
        ? new ProcessStartInfo(Environment.ProcessPath!, $"\"{Path.GetFullPath(app)}\" --root \"{root}\"")
        : new ProcessStartInfo(Path.GetFullPath(app), $"--root \"{root}\"");
    start.UseShellExecute = false;
    start.RedirectStandardError = true;
    var oeBook = scenario == "addressing" ? AddressingSmoke.WriteOeAddressBook(root) : null;
    if (oeBook is not null) start.Environment["MAILKEEP_OE_ADDRESSBOOK"] = oeBook;
    start.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(app))!;
    process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch Mailkeep WinForms.");
    // An app that dies on startup otherwise looks identical to one that is merely slow to draw.
    var appErrors = Task.Run(process.StandardError.ReadToEndAsync);
    var window = WaitFor(() =>
    {
        process.Refresh();
        if (process.HasExited)
            throw new InvalidOperationException($"Mailkeep exited with code {process.ExitCode}: {appErrors.Result}");
        return process.MainWindowHandle == IntPtr.Zero ? null : AutomationElement.FromHandle(process.MainWindowHandle);
    }, TimeSpan.FromSeconds(scenario == "reading" ? 30 : 10));
    Check(window.Current.IsEnabled, "main window is disabled");
    Check(!window.Current.IsOffscreen, "main window is off-screen");

    // Readiness = a ready-gated toolbar button coming alive. Find is not clicked: it opens a modal now.
    WaitForEnabled(window, "Fetch Log");
    var toolbar = Find(window, ControlType.ToolBar, "Main toolbar");
    Check(Find(toolbar, ControlType.Button, "Find").Current.IsEnabled, "Find button is missing from the toolbar");
    // The search field is gone by design — search is summoned by hotkey/menu, not parked in the toolbar.
    Check(toolbar.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Search query")) is null,
        "the toolbar has grown a search field again");
    // Accounts management lives in the Tools menu only; the toolbar must not regrow an entry point.
    Check(toolbar.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, "Accounts")) is null,
        "the toolbar has an Accounts button again");
    Check(FindNamed(toolbar, "Refresh Cnnct'd").Current.IsEnabled, "Refresh Cnnct'd toolbar button is missing");
    Check(toolbar.Current.NativeWindowHandle != 0, "Main toolbar has no native window handle");
    if (scenario == "selection")
    {
        var accountList = FindNamed(window, "Connected accounts");
        var messages = FindNamed(window, "Message list");
        var list = NativeHandle(messages);
        // Switching accounts must actually reload the list. The fixture gives each account a different
        // number of messages so a stale list is a wrong count, not a coincidence.
        SwitchTo("alpha@example.invalid", 3);
        SwitchTo("zeta@example.invalid", 2);
        SwitchTo("connected@example.invalid", 1);
        SwitchTo("alpha@example.invalid", 3);
        // Then the same switching among the bulk accounts, including rows far enough down the list to have
        // been scrolled to, and straight back to a small one.
        SwitchTo("bulk-00@example.invalid", 100);
        SwitchTo("bulk-24@example.invalid", 124);
        SwitchTo("bulk-12@example.invalid", 112);
        SwitchTo("bulk-13@example.invalid", 113);
        SwitchTo("alpha@example.invalid", 3);
        Console.WriteLine("PASS selection account-switch=repopulates accounts=28");
        PostMessage(NativeHandle(window), 0x0010, IntPtr.Zero, IntPtr.Zero);
        Check(process.WaitForExit(5000), "selection fixture did not close");
        return 0;

        // UI Automation's Select() writes the selection straight into the control. A real click goes through
        // focus and hit-testing first, and that is where an account switch has gone wrong before, so the
        // switch is driven as an actual click on the row.
        void SwitchTo(string address, int expected)
        {
            var rows = accountList.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true)).Cast<AutomationElement>().ToArray();
            var index = Array.FindIndex(rows, row => row.Current.Name.StartsWith(address, StringComparison.OrdinalIgnoreCase));
            Check(index >= 0, "no account row for " + address);
            ClickRow(NativeHandle(accountList), rows[index]);
            WaitUntil(() => (int)SendMessage(list, 0x1004, IntPtr.Zero, IntPtr.Zero) == expected,
                $"clicking {address} did not repopulate the list: expected {expected}, list still shows "
                + (int)SendMessage(list, 0x1004, IntPtr.Zero, IntPtr.Zero));
        }
    }
    if (scenario == "outbox-send")
    {
        var folders = FindNamed(window, "Local mail folders");
        SelectFolder(folders, "Outbox");
        var messages = FindNamed(window, "Message list");
        WaitUntil(() => (int)SendMessage(NativeHandle(messages), 0x1004, IntPtr.Zero, IntPtr.Zero) == 2, "Queued fixtures did not load");
        var queued = services.Database.ListOutgoing(OutgoingStates.Outbox)
            .OrderBy(message => message.Subject, StringComparer.Ordinal).ToArray();
        Check(queued.Length == 2, "outbox-send fixture needs exactly two queued messages");
        var one = queued[0].Id;
        var two = queued[1].Id;
        SendOutbox();
        WaitUntil(() => Attempts(one) == 1 && Attempts(two) == 1, "Send Outbox did not attempt both confirmed messages");
        DismissFailure();
        Check(State(one) == OutgoingStates.Outbox && State(two) == OutgoingStates.Outbox, "A failed message left the Outbox");
        Check(services.Database.GetOutgoing(one)!.NextAttemptAt > DateTimeOffset.UtcNow, "A failed message was not placed in retry backoff");
        Check(services.Database.GetOutgoing(one)!.LastError is { Length: > 0 }, "A failed message recorded no error");
        // The bug this scenario exists for: a confirmed Send Outbox used to run the ordinary due-message loop,
        // so messages still inside their retry delay were skipped without a word and the queue looked stuck.
        SendOutbox();
        WaitUntil(() => Attempts(one) == 2 && Attempts(two) == 2, "Send Outbox skipped messages still inside their retry backoff");
        DismissFailure();
        // Send Outbox asks nothing, as in OE: no "Send N message(s) now?" window may appear.
        Check(FindWindow("#32770", "Send Outbox") == IntPtr.Zero, "Send Outbox asked for confirmation");
        Check(services.Database.ListOutgoing(OutgoingStates.Outbox).Count == 2, "Failed messages did not stay in the Outbox");
        Check(services.Database.ListOutgoing(OutgoingStates.Sent).Count == 0, "A message was reported sent to the loopback blackhole");
        CaptureWindow(NativeHandle(window), Option(args, "--screenshots"), "outbox-retry");
        PostMessage(NativeHandle(window), 0x0010, IntPtr.Zero, IntPtr.Zero);
        Check(process.WaitForExit(5000), "Outbox send fixture did not close");
        Console.WriteLine("PASS outbox-send attempts=2-each backoff=bypassed confirmation=none sent=0");
        return 0;

        // Off this thread: the failure dialog that follows is modal, and Invoke would sit inside its loop.
        void SendOutbox()
        {
            Expand(Find(window, ControlType.MenuItem, "File"));
            var click = Task.Run(() => Invoke(FindNamed(window, "Send Outbox menu item")));
            Check(click.Wait(5000), "Send Outbox menu click did not return");
        }
        // The failure dialog only appears once the whole send operation has unwound, so dismissing it is also
        // how this scenario waits for the attempt to be finished with.
        void DismissFailure()
        {
            var failure = WaitNativeWindow("Sending queued messages…");
            ClickNative(failure, "OK");
            WaitUntil(() => !IsWindowVisible(failure), "Send failure dialog did not close");
        }
        int Attempts(string id) => services.Database.GetOutgoing(id)!.AttemptCount;
        string State(string id) => services.Database.GetOutgoing(id)!.Status;
    }
    // Compose is driven from the toolbar with no menu open: while a drop-down is tracking, the first click
    // anywhere else only dismisses the menu, so opening one here would swallow the New Mail click.
    if (scenario == "layout")
    {
        InspectLayout(window, process, start, services);
        return 0;
    }
    if (scenario == "attachment-open")
    {
        OpenAndCheck(window, "First attachment fixture", "attachment-open-preview", attachmentOpenName + " first.txt");
        SendMessage(NativeHandle(FindNamed(window, "Message list")), 0x0100, new IntPtr(13), IntPtr.Zero);
        var opened = WaitForWindow("Automation fixture");
        OpenAndCheck(opened, "Second attachment fixture", "attachment-open-window", attachmentOpenName + " second.txt");
        Invoke(WaitForEnabled(opened, "Message attachments"));
        var blockedItem = WaitFor(() => AutomationElement.RootElement.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, "invoice.exe.txt")), TimeSpan.FromSeconds(5), "blocked attachment missing");
        Invoke(blockedItem);
        var warning = WaitForWindow("Could not open attachment");
        CaptureWindow(NativeHandle(warning), Option(args, "--screenshots"), "attachment-open-blocked");
        Invoke(FindNamed(warning, "OK"));
        var openedHandle = NativeHandle(opened);
        PostMessage(openedHandle, 0x0010, IntPtr.Zero, IntPtr.Zero);
        WaitUntil(() => !IsWindowVisible(openedHandle), "standalone message did not close");
        PostMessage(NativeHandle(window), 0x0010, IntPtr.Zero, IntPtr.Zero);
        Check(process.WaitForExit(5000), "attachment Open fixture did not close");
        Console.WriteLine("PASS attachment-open association=Notepad preview=first-bytes standalone=second-bytes filename-spaces=supported");
        return 0;

        void OpenAndCheck(AutomationElement owner, string expected, string capture, string filename)
        {
            Invoke(WaitForEnabled(owner, "Message attachments"));
            var item = WaitFor(() => AutomationElement.RootElement.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, filename)),
                TimeSpan.FromSeconds(5), "paperclip attachment menu item missing: " + filename);
            Invoke(item);
            var note = WaitFor(() =>
            {
                foreach (var candidate in Process.GetProcessesByName("notepad"))
                {
                    using (candidate)
                        if (candidate.MainWindowTitle.Contains(attachmentOpenName, StringComparison.Ordinal)
                            && candidate.MainWindowHandle != IntPtr.Zero)
                            return AutomationElement.FromHandle(candidate.MainWindowHandle);
                }
                return null;
            }, TimeSpan.FromSeconds(15), "associated Notepad did not open the uniquely named attachment");
            var noteHandle = NativeHandle(note);
            try
            {
                var document = ReadingText(note);
                var text = ((TextPattern)document.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1);
                Check(text.TrimEnd('\r', '\n') == expected, "Open displayed the wrong attachment contents: " + text);
                CaptureWindow(noteHandle, Option(args, "--screenshots"), capture);
            }
            finally
            {
                PostMessage(noteHandle, 0x0010, IntPtr.Zero, IntPtr.Zero);
                WaitUntil(() => !IsWindowVisible(noteHandle), "fixture Notepad did not close");
            }
        }
    }
    if (scenario == "read-state")
    {
        var messages = FindNamed(window, "Message list");
        var rows = messages.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true))
            .Cast<AutomationElement>().ToArray();
        Check(rows.Length == 2, "read-state fixture needs two rows");
        Select(rows[0]);
        ((SelectionItemPattern)rows[1].GetCurrentPattern(SelectionItemPattern.Pattern)).AddToSelection();
        SetRead(true);
        CaptureWindow(NativeHandle(window), Option(args, "--screenshots"), "messages-marked-read");
        SetRead(false);
        CaptureWindow(NativeHandle(window), Option(args, "--screenshots"), "messages-marked-unread");
        SelectFolder(FindNamed(window, "Local mail folders"), "Drafts");
        WaitUntil(() => (int)SendMessage(NativeHandle(messages), 0x1004, IntPtr.Zero, IntPtr.Zero) == 0, "Drafts did not load");
        Expand(Find(window, ControlType.MenuItem, "Message"));
        Check(!Find(window, ControlType.MenuItem, "Mark as Read").Current.IsEnabled
            && !Find(window, ControlType.MenuItem, "Mark as Unread").Current.IsEnabled, "empty folder allows read-state changes");
        ((ExpandCollapsePattern)Find(window, ControlType.MenuItem, "Message").GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
        PostMessage(NativeHandle(window), 0x0010, IntPtr.Zero, IntPtr.Zero);
        Check(process.WaitForExit(5000), "read-state fixture did not close");
        Console.WriteLine("PASS read-state read-unread=2 selection=preserved unread-counts=updated search=updated empty-folder=disabled");
        return 0;

        void SetRead(bool read)
        {
            Expand(Find(window, ControlType.MenuItem, "Message"));
            Invoke(Find(window, ControlType.MenuItem, read ? "Mark as Read" : "Mark as Unread"));
            WaitUntil(() => services.Database.ListMessages().Where(message => message.Account == "local-fixture").All(message => message.IsRead == read),
                "read-state command did not update both messages");
            Check((int)SendMessage(NativeHandle(messages), 0x1032, IntPtr.Zero, IntPtr.Zero) == 2, "read-state update lost the selection");
            Check(services.Database.LocalFolderCounts(["local-fixture"])[LocalFolderKinds.Inbox].Unread == (read ? 0 : 2), "folder unread count is stale");
            var expected = read ? "Inbox" : "Inbox  (2)";
            WaitUntil(() => FindNamed(window, "Local mail folders").FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem)).Cast<AutomationElement>()
                .Any(node => node.Current.Name == expected), "Merged Inbox badge counts hidden imported copies");
            services.Catalog.SyncOperational();
            var hits = services.Catalog.Search("fixture", folder: LocalFolderKinds.Inbox);
            Check(hits.Count == 2 && hits.Results.All(hit => hit.IsRead == read), "search read state is stale");
            Expand(Find(window, ControlType.MenuItem, "Message"));
            Check(!Find(window, ControlType.MenuItem, read ? "Mark as Read" : "Mark as Unread").Current.IsEnabled,
                "already-applied read-state command remains enabled");
            ((ExpandCollapsePattern)Find(window, ControlType.MenuItem, "Message").GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
        }
    }
    if (scenario == "drag-filing")
    {
        var messages = FindNamed(window, "Message list");
        var tree = FindNamed(window, "Local mail folders");
        SelectFolder(tree, "Drafts");
        SelectFolder(tree, "Inbox");
        WaitUntil(() => (int)SendMessage(NativeHandle(messages), 0x1004, IntPtr.Zero, IntPtr.Zero) == 2, "Merged Inbox did not load");
        var destination = services.Database.ListLocalFolders().Single(folder => folder.Name == "Filed fixture");
        DragSelectionTo("Drafts");
        Check(services.Database.ListMessages().Count(message => message.Account == "local-fixture" && message.Folder == LocalFolderKinds.Inbox) == 2,
            "drag into Drafts changed received mail");
        DragSelectionTo(destination.Name);
        try
        {
            WaitUntil(() => services.Database.ListMessages().Count(message => message.Account == "local-fixture" && message.Folder == destination.Key) == 2,
                "drag did not file both selected messages");
        }
        catch
        {
            Console.Error.WriteLine(string.Join(" | ", services.Database.ListMessages().Where(message => message.Account == "local-fixture")
                .Select(message => message.Subject + " -> " + message.Folder)));
            CaptureWindow(NativeHandle(window), Option(args, "--screenshots"), "message-drag-failure");
            throw;
        }
        AssertInboxRefreshed(tree, messages);
        SelectFolder(tree, destination.Name);
        WaitUntil(() => (int)SendMessage(NativeHandle(messages), 0x1004, IntPtr.Zero, IntPtr.Zero) == 2, "dragged messages are not visible at the destination");
        CaptureWindow(NativeHandle(window), Option(args, "--screenshots"), "message-drag-filing");
        PostMessage(NativeHandle(window), 0x0010, IntPtr.Zero, IntPtr.Zero);
        Check(process.WaitForExit(5000), "drag fixture did not close");
        Console.WriteLine("PASS drag-filing selected=2 drafts=rejected merged-inbox=refreshed source-selection=retained imported-copy=hidden local-destination=visible");
        return 0;

        void DragSelectionTo(string name)
        {
            SetForegroundWindow(NativeHandle(window));
            SetWindowPos(NativeHandle(window), new IntPtr(-1), 0, 0, 0, 0, 0x0043);
            var rows = messages.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true))
                .Cast<AutomationElement>().ToArray();
            Check(rows.Length == 2, "drag fixture needs two selected rows");
            Select(rows[0]);
            ((SelectionItemPattern)rows[1].GetCurrentPattern(SelectionItemPattern.Pattern)).AddToSelection();
            var from = rows[0].Current.BoundingRectangle;
            var to = Find(tree, ControlType.TreeItem, name).Current.BoundingRectangle;
            var x = (int)from.Left + 75;
            var y = (int)(from.Top + from.Height / 2);
            var tx = (int)to.Left + 25;
            var ty = (int)(to.Top + to.Height / 2);
            Check(SetCursorPos(x, y), "could not position the mouse for dragging");
            Thread.Sleep(100);
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            try
            {
                Thread.Sleep(100);
                for (var step = 1; step <= 20; step++)
                {
                    SetCursorPos(x + (tx - x) * step / 20, y + (ty - y) * step / 20);
                    Thread.Sleep(40);
                }
                Thread.Sleep(200);
            }
            finally
            {
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                SetWindowPos(NativeHandle(window), new IntPtr(-2), 0, 0, 0, 0, 0x0043);
            }
            Thread.Sleep(300);
        }
    }
    if (scenario == "folders")
    {
        var tree = FindNamed(window, "Local mail folders");
        SelectFolder(tree, "Inbox");
        Expand(Find(window, ControlType.MenuItem, "Folder"));
        foreach (var command in new[] { "Rename Folder menu item", "Move Folder Into menu item", "Delete Folder menu item" })
            Check(!Find(window, ControlType.MenuItem, command).Current.IsEnabled, "standard folder command is enabled: " + command);
        ((ExpandCollapsePattern)Find(window, ControlType.MenuItem, "Folder").GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
        SelectFolder(tree, "All Local Mail");
        FolderCommand("New Folder menu item");
        EditFolderName("Working");
        WaitUntil(() => services.Database.ListLocalFolders().Any(folder => folder.Name == "Working"), "folder creation was not saved");
        var created = services.Database.ListLocalFolders().Single(folder => folder.Name == "Working");
        FolderCommand("Rename Folder menu item");
        var duplicateRename = Task.Run(() => EditFolderName("projects"));
        var duplicateError = WaitNativeWindow("Could not rename the folder");
        Check(NativeControls(duplicateError).Any(control => control.Title.Contains("already exists in this location")),
            "duplicate folder error did not explain the conflict");
        ClickNative(duplicateError, "OK");
        Check(duplicateRename.Wait(5000), "duplicate rename did not return");
        Check(services.Database.ListLocalFolders().Single(folder => folder.Key == created.Key).Name == "Working",
            "rejected duplicate rename changed the original folder");
        FolderCommand("Rename Folder menu item");
        EditFolderName("Filed work");
        WaitUntil(() => services.Database.ListLocalFolders().Any(folder => folder.Key == created.Key && folder.Name == "Filed work"), "rename changed the key or was not saved");
        FolderCommand("New Folder menu item");
        EditFolderName("Notes");
        WaitUntil(() => services.Database.ListLocalFolders().Any(folder => folder.Name == "Notes" && folder.ParentKey == created.Key), "new nested folder has the wrong parent");
        SelectFolder(tree, "Filed work");
        MoveFolder("Projects/Child", "Filed work", "Filed work/Notes");
        var projects = services.Database.ListLocalFolders().Single(folder => folder.Name == "Projects");
        var destination = services.Database.ListLocalFolders().Single(folder => folder.Name == "Child" && folder.ParentKey == projects.Key);
        WaitUntil(() => services.Database.ListLocalFolders().Single(folder => folder.Key == created.Key).ParentKey == destination.Key,
            "folder did not move under the chosen Child");
        SelectFolder(tree, "Filed work");
        MoveFolder("All Local Mail (top level)", "Projects/Child/Filed work", "Projects/Child/Filed work/Notes");
        WaitUntil(() => services.Database.ListLocalFolders().Single(folder => folder.Key == created.Key).ParentKey is null,
            "folder did not return to the top level");
        Expand(Find(tree, ControlType.TreeItem, "Filed work"));
        SelectFolder(tree, "Notes");
        var deletion = Task.Run(() => FolderCommand("Delete Folder menu item"));
        var prompt = WaitNativeWindow("Delete Folder");
        ClickNative(prompt, "&Yes");
        Check(deletion.Wait(5000), "folder deletion did not return");
        WaitUntil(() => services.Database.ListLocalFolders().All(folder => folder.Name != "Notes"), "empty folder was not deleted");
        CaptureWindow(NativeHandle(window), Option(args, "--screenshots"), "folder-management");
        SelectFolder(tree, "Inbox");
        var messages = FindNamed(window, "Message list");
        WaitUntil(() => (int)SendMessage(NativeHandle(messages), 0x1004, IntPtr.Zero, IntPtr.Zero) == 1, "Inbox fixture is missing");
        Select(messages.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true))
            .Cast<AutomationElement>().Single());
        WaitForEnabled(toolbar, "Move");
        var messageMove = Task.Run(() => Invoke(FindNamed(toolbar, "Move")));
        var messagePicker = WaitNativeWindow("Move to folder");
        var messageFolders = NativeControls(messagePicker).Single(control => control.Class.Contains("LISTBOX", StringComparison.OrdinalIgnoreCase)).Handle;
        Check((int)SendText(messageFolders, 0x01a2, new IntPtr(-1), "All Local Mail (top level)") == -1, "message picker offers a non-folder destination");
        Check((int)SendText(messageFolders, 0x018c, new IntPtr(-1), "Other/Child") >= 0, "message destination path is missing");
        SendMessage(GetParent(messageFolders), 0x0111, new IntPtr((1 << 16) | (GetDlgCtrlID(messageFolders) & 0xffff)), messageFolders);
        CaptureWindow(messagePicker, Option(args, "--screenshots"), "message-folder-paths");
        ClickNative(messagePicker, "Move");
        Check(messageMove.Wait(5000), "message Move picker did not return");
        var other = services.Database.ListLocalFolders().Single(folder => folder.Name == "Other");
        var otherChild = services.Database.ListLocalFolders().Single(folder => folder.Name == "Child" && folder.ParentKey == other.Key);
        WaitUntil(() => services.Database.ListMessages().Any(message => message.Account == "local-fixture" && message.SourceKey == "fixture-1"
            && message.Folder == otherChild.Key), "message was filed in the wrong Child folder");
        PostMessage(NativeHandle(window), 0x0010, IntPtr.Zero, IntPtr.Zero);
        Check(process.WaitForExit(5000), "folder fixture did not close");
        Console.WriteLine("PASS folders create=nested rename=stable-key duplicate-name=rejected move=full-path top-level=restored self-descendants=excluded delete=empty standard-folders=protected message-destination=correct-child");
        return 0;

        void FolderCommand(string name)
        {
            Expand(Find(window, ControlType.MenuItem, "Folder"));
            Invoke(Find(window, ControlType.MenuItem, name));
        }
        void EditFolderName(string name)
        {
            Console.WriteLine("FOLDER editing " + name);
            var edit = IntPtr.Zero;
            WaitUntil(() => (edit = SendMessage(NativeHandle(tree), 0x110f, IntPtr.Zero, IntPtr.Zero)) != IntPtr.Zero, "folder label editor did not open");
            SendText(edit, 0x000c, IntPtr.Zero, name);
            SendMessage(NativeHandle(tree), 0x1116, IntPtr.Zero, IntPtr.Zero);
        }
        void MoveFolder(string path, params string[] excluded)
        {
            var opening = Task.Run(() => FolderCommand("Move Folder Into menu item"));
            var picker = WaitNativeWindow("Move to folder");
            var list = NativeControls(picker).Single(control => control.Class.Contains("LISTBOX", StringComparison.OrdinalIgnoreCase)).Handle;
            foreach (var item in excluded)
                Check((int)SendText(list, 0x01a2, new IntPtr(-1), item) == -1, "folder or descendant offered as destination: " + item);
            Check((int)SendText(list, 0x01a2, new IntPtr(-1), "Other/Child") >= 0, "duplicate child path is missing");
            Check((int)SendText(list, 0x018c, new IntPtr(-1), path) >= 0, "destination path missing: " + path);
            SendMessage(GetParent(list), 0x0111, new IntPtr((1 << 16) | (GetDlgCtrlID(list) & 0xffff)), list);
            ClickNative(picker, "Move");
            Check(opening.Wait(5000), "folder Move picker did not return");
        }
    }
    if (scenario is "filing" or "move-filing")
    {
        var target = services.Database.ListLocalFolders().Single(folder => folder.Name == "Filed fixture");
        var messages = FindNamed(window, "Message list");
        var tree = FindNamed(window, "Local mail folders");
        SelectFolder(tree, "Drafts");
        SelectFolder(tree, "Inbox");
        WaitUntil(() => (int)SendMessage(NativeHandle(messages), 0x1004, IntPtr.Zero, IntPtr.Zero) == 2, "Merged Inbox did not load");
        SelectBoth();
        var move = Task.Run(() => Invoke(FindNamed(toolbar, "Move")));
        var chooser = WaitNativeWindow("Move to folder");
        var list = NativeControls(chooser).Single(control => control.Class.Contains("LISTBOX", StringComparison.OrdinalIgnoreCase)).Handle;
        Check((int)SendText(list, 0x01a2, new IntPtr(-1), "Drafts") == -1, "received messages can be filed into Drafts");
        Check((int)SendText(list, 0x01a2, new IntPtr(-1), "Outbox") == -1, "received messages can be filed into Outbox");
        Check((int)SendText(list, 0x018c, new IntPtr(-1), target.Name) >= 0, "target folder is missing");
        SendMessage(GetParent(list), 0x0111, new IntPtr((1 << 16) | (GetDlgCtrlID(list) & 0xffff)), list);
        ClickNative(chooser, "Move");
        Check(move.Wait(5000), "Move dialog did not return");
        WaitUntil(() => services.Database.ListMessages().Count(message => message.Account == "local-fixture" && message.Folder == target.Key) == 2,
            "Move did not file both selected messages");
        AssertInboxRefreshed(tree, messages);
        SelectFolder(FindNamed(window, "Local mail folders"), target.Name);
        WaitUntil(() => (int)SendMessage(NativeHandle(messages), 0x1004, IntPtr.Zero, IntPtr.Zero) == 2, "filed messages are not visible in their folder");
        if (scenario == "move-filing")
        {
            CaptureWindow(NativeHandle(window), Option(args, "--screenshots"), "toolbar-move-filing");
            PostMessage(NativeHandle(window), 0x0010, IntPtr.Zero, IntPtr.Zero);
            Check(process.WaitForExit(5000), "Move fixture did not close");
            Console.WriteLine("PASS move-filing selected=2 merged-inbox=refreshed source-selection=retained imported-copy=hidden local-destination=visible");
            return 0;
        }
        SelectBoth();
        // Moving local mail to Deleted Items is reversible, so it happens without a prompt, as in OE.
        var delete = Task.Run(() => Invoke(FindNamed(toolbar, "Delete")));
        Check(delete.Wait(5000), "Delete did not return");
        Check(FindWindow(null, "Delete message") == IntPtr.Zero, "moving local mail to Deleted Items asked for confirmation");
        WaitUntil(() => services.Database.ListMessages().Count(message => message.Account == "local-fixture" && message.Folder == LocalFolderKinds.Deleted) == 2,
            "Delete did not move both messages into Deleted Items");
        services.Catalog.SyncOperational();
        Check(services.Catalog.Search("fixture", folder: target.Key).Count == 0, "search retained the old filing folder");
        SelectFolder(FindNamed(window, "Local mail folders"), "Deleted Items");
        WaitUntil(() => (int)SendMessage(NativeHandle(messages), 0x1004, IntPtr.Zero, IntPtr.Zero) == 2, "deleted messages cannot be reviewed");
        SelectBoth();
        var permanentDelete = Task.Run(() => Invoke(FindNamed(toolbar, "Delete")));
        var permanentPrompt = WaitNativeWindow("Delete message");
        Check(NativeControls(permanentPrompt).Any(control => control.Title.StartsWith("Permanently delete", StringComparison.Ordinal)),
            "deleting from Deleted Items did not explain permanence");
        ClickNative(permanentPrompt, "&No");
        Check(permanentDelete.Wait(5000), "permanent-delete cancellation did not return");
        Check(services.Database.ListMessages().Count(message => message.Account == "local-fixture" && message.Folder == LocalFolderKinds.Deleted) == 2,
            "cancelling permanent deletion removed mail");
        CaptureWindow(NativeHandle(window), Option(args, "--screenshots"), "local-filing");
        SelectFolder(FindNamed(window, "Local mail folders"), "Sent");
        WaitUntil(() => (int)SendMessage(NativeHandle(messages), 0x1004, IntPtr.Zero, IntPtr.Zero) == 1, "Sent copy is not visible");
        Select(messages.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true))
            .Cast<AutomationElement>().Single());
        WaitForEnabled(toolbar, "Delete");
        var sentDelete = Task.Run(() => Invoke(FindNamed(toolbar, "Delete")));
        Check(sentDelete.Wait(5000), "Sent deletion did not return");
        Check(FindWindow(null, "Delete message") == IntPtr.Zero, "moving a Sent copy to Deleted Items asked for confirmation");
        WaitUntil(() => services.Database.ListMessages().Any(message => message.SourceKey == "sent:filing-fixture"
            && message.Folder == LocalFolderKinds.Deleted), "Sent copy was not moved into Deleted Items");
        SelectFolder(FindNamed(window, "Local mail folders"), "Deleted Items");
        WaitUntil(() => (int)SendMessage(NativeHandle(messages), 0x1004, IntPtr.Zero, IntPtr.Zero) == 3,
            "Deleted Items did not retain the Sent copy alongside received mail");
        PostMessage(NativeHandle(window), 0x0010, IntPtr.Zero, IntPtr.Zero);
        Check(process.WaitForExit(5000), "filing fixture did not close");
        Console.WriteLine("PASS filing multi-move=2 destination=visible draft-outbox=excluded local-delete=2-unprompted deleted-items=visible search=updated permanent-delete=explained cancel=retained sent-delete=local-unprompted");
        return 0;

        void SelectBoth()
        {
            var rows = messages.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true))
                .Cast<AutomationElement>().ToArray();
            Check(rows.Length == 2, "filing fixture does not have two rows");
            Select(rows[0]);
            ((SelectionItemPattern)rows[1].GetCurrentPattern(SelectionItemPattern.Pattern)).AddToSelection();
            Check((int)SendMessage(NativeHandle(messages), 0x1032, IntPtr.Zero, IntPtr.Zero) == 2, "both filing messages were not selected");
        }
    }
    if (scenario == "connected-copy")
    {
        var target = services.Database.ListLocalFolders().Single(folder => folder.Name == "Filed fixture");
        var accounts = FindNamed(window, "Connected accounts");
        Select(FindNamed(accounts, "connected@example.invalid"));
        var messages = FindNamed(window, "Message list");
        WaitUntil(() => (int)SendMessage(NativeHandle(messages), 0x1004, IntPtr.Zero, IntPtr.Zero) == 1,
            "Connected fixture did not load");
        Select(messages.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true)).Cast<AutomationElement>().Single());
        var copying = Task.Run(() => Invoke(FindNamed(toolbar, "Move")));
        var confirm = WaitNativeWindow("Copy to Local Mail");
        Check(NativeControls(confirm).Any(control => control.Title.Contains("originals will remain", StringComparison.OrdinalIgnoreCase)),
            "Connected copy did not explain that the server original remains");
        ClickNative(confirm, "&Yes");
        WaitUntil(() => !IsWindowVisible(confirm), "Connected copy confirmation did not close");
        var chooser = WaitNativeWindow("Copy to Local Mail");
        var list = NativeControls(chooser).Single(control => control.Class.Contains("LISTBOX", StringComparison.OrdinalIgnoreCase)).Handle;
        Check((int)SendText(list, 0x01a2, new IntPtr(-1), "Drafts") == -1, "Connected copies can be filed into Drafts");
        Check((int)SendText(list, 0x01a2, new IntPtr(-1), "Outbox") == -1, "Connected copies can be filed into Outbox");
        Check((int)SendText(list, 0x018c, new IntPtr(-1), target.Name) >= 0, "Local copy target is missing");
        SendMessage(GetParent(list), 0x0111, new IntPtr((1 << 16) | (GetDlgCtrlID(list) & 0xffff)), list);
        ClickNative(chooser, "Copy");
        Check(copying.Wait(5000), "Connected copy dialogs did not return");
        var local = services.Database.ListMessagesForAccounts([MailContract.LocalCopyAccount], target.Key).Single();
        Check(local.Subject == "Connected unread" && local.IsLocalStoredCopy, "Connected message was not copied as Local Mail");
        Check(services.Database.ListMessages("connected-fixture", MailContract.LiveSource).Count == 1,
            "Connected original was removed from the cache");
        SelectFolder(FindNamed(window, "Local mail folders"), target.Name);
        WaitUntil(() => (int)SendMessage(NativeHandle(messages), 0x1004, IntPtr.Zero, IntPtr.Zero) == 1,
            "Local copy is not visible in its destination");
        CaptureWindow(NativeHandle(window), Option(args, "--screenshots"), "connected-copy-local");
        PostMessage(NativeHandle(window), 0x0010, IntPtr.Zero, IntPtr.Zero);
        Check(process.WaitForExit(5000), "Connected copy fixture did not close");
        Console.WriteLine("PASS connected-copy original=retained local=visible provenance=local drafts-outbox=excluded provider-writes=none");
        return 0;
    }
    if (scenario == "search")
    {
        InspectSearch(window, services, Option(args, "--screenshots"));
        PostMessage(NativeHandle(window), 0x0010, IntPtr.Zero, IntPtr.Zero);
        Check(process.WaitForExit(5000), "search fixture did not close");
        return 0;
    }
    if (scenario == "sender-search")
    {
        var archiveFile = Path.Combine(root, "portable-fixture", FolderBundle.FileName);
        var originalArchive = File.ReadAllBytes(archiveFile);
        Invoke(FindNamed(Find(window, ControlType.ToolBar, "Main toolbar"), "Find"));
        var find = WaitForWindow("Find Message");
        foreach (var fragment in new[] { "harbourton", "fielding", "BOURTON", "Har" })
        {
            SendText(NativeHandle(FindNamed(find, "From criteria")), 0x000c, IntPtr.Zero, fragment);
            SearchStatus("2 messages found");
        }
        ((TogglePattern)FindNamed(find, "Search archives too").GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        SendText(NativeHandle(FindNamed(find, "From criteria")), 0x000c, IntPtr.Zero, "fielding");
        SearchStatus("2 messages found; 1 in archives");
        CaptureWindow(NativeHandle(find), Option(args, "--screenshots"), "sender-partial-results");
        SendText(NativeHandle(FindNamed(find, "From criteria")), 0x000c, IntPtr.Zero, "ha");
        SearchStatus("Enter at least 3 characters in From.");
        Check((int)SendMessage(NativeHandle(FindNamed(find, "Find results")), 0x1004, IntPtr.Zero, IntPtr.Zero) == 3,
            "Short sender input discarded previous results");
        CaptureWindow(NativeHandle(find), Option(args, "--screenshots"), "sender-minimum");
        Check(originalArchive.SequenceEqual(File.ReadAllBytes(archiveFile)), "Sender search modified the portable archive");
        if (Option(args, "--screenshots") is { } evidence)
            File.WriteAllText(Path.Combine(evidence, "sender-archive-proof.json"), System.Text.Json.JsonSerializer.Serialize(new {
                before = Convert.ToBase64String(originalArchive), after = Convert.ToBase64String(File.ReadAllBytes(archiveFile)) }));
        Close(find);
        PostMessage(NativeHandle(window), 0x0010, IntPtr.Zero, IntPtr.Zero);
        Check(process.WaitForExit(5000), "sender-search fixture did not close");
        Console.WriteLine("PASS sender-search fragments=address+name+middle+case+three-characters minimum=guarded archives=1 bytes=unchanged body-only=excluded");
        return 0;

        void SearchStatus(string expected)
        {
            Invoke(FindNamed(find, "Find Now"));
            WaitUntil(() => NativeControls(NativeHandle(find)).Any(control => control.Title == expected),
                "Sender search did not report " + expected);
        }
    }
    if (scenario == "search-filters")
    {
        Invoke(FindNamed(Find(window, ControlType.ToolBar, "Main toolbar"), "Find"));
        var find = WaitForWindow("Find Message");
        Toggle("Has attachments");
        SearchCount(1);
        Invoke(FindNamed(find, "New Search"));
        SendText(NativeHandle(FindNamed(find, "Look in")), 0x014d, new IntPtr(-1), "Drafts");
        SearchCount(0);
        Invoke(FindNamed(find, "New Search"));
        SendText(NativeHandle(FindNamed(find, "Look in")), 0x014d, new IntPtr(-1), "Inbox");
        SearchCount(1);
        Invoke(FindNamed(find, "New Search"));
        Toggle("Received before switch");
        SearchCount(7);
        CaptureWindow(NativeHandle(find), Option(args, "--screenshots"), "search-filters");
        Close(find);
        PostMessage(NativeHandle(window), 0x0010, IntPtr.Zero, IntPtr.Zero);
        Check(process.WaitForExit(5000), "search-filter fixture did not close");
        Console.WriteLine("PASS search-filters attachment-only=1 empty-folder=0 inbox-only=1 date-only=7");
        return 0;

        void Toggle(string name) => ((TogglePattern)FindNamed(find, name).GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        void SearchCount(int expected)
        {
            Invoke(FindNamed(find, "Find Now"));
            var status = expected == 1 ? "1 message found" : $"{expected} messages found";
            try { WaitUntil(() => NativeControls(NativeHandle(find)).Any(control => control.Title == status), $"filter-only search did not report {status}"); }
            catch
            {
                CaptureWindow(NativeHandle(find), Option(args, "--screenshots"), "search-filter-failure");
                Console.Error.WriteLine(string.Join(" | ", NativeControls(NativeHandle(find)).Select(control => control.Title).Where(value => value.Length > 0)));
                throw;
            }
            Check((int)SendMessage(NativeHandle(FindNamed(find, "Find results")), 0x1004, IntPtr.Zero, IntPtr.Zero) == expected,
                "filter-only results disagree with the count");
        }
    }
    if (scenario == "search-folders")
    {
        Invoke(FindNamed(Find(window, ControlType.ToolBar, "Main toolbar"), "Find"));
        var find = WaitForWindow("Find Message");
        Choose("Projects");
        SearchFolders(["Projects", "Projects/Child"]);
        ((TogglePattern)FindNamed(find, "Include subfolders").GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        SearchFolders(["Projects"]);
        Choose("Projects/Child");
        SearchFolders(["Projects/Child"]);
        Choose("Other/Child");
        SearchFolders(["Other/Child"]);
        CaptureWindow(NativeHandle(find), Option(args, "--screenshots"), "custom-folder-search");
        Close(find);
        PostMessage(NativeHandle(window), 0x0010, IntPtr.Zero, IntPtr.Zero);
        Check(process.WaitForExit(5000), "custom-folder search fixture did not close");
        Console.WriteLine("PASS search-folders names=resolved descendants=included exact-folder=limited duplicate-child-names=distinguished result-paths=readable");
        return 0;

        void Choose(string name)
        {
            Check((int)SendText(NativeHandle(FindNamed(find, "Look in")), 0x014d, new IntPtr(-1), name) >= 0,
                "custom folder choice missing: " + name);
        }
        void SearchFolders(string[] expected)
        {
            Invoke(FindNamed(find, "Find Now"));
            WaitForEnabled(find, "Find Now");
            var results = FindNamed(find, "Find results");
            WaitUntil(() =>
            {
                var rows = results.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true)).Cast<AutomationElement>().ToArray();
                var paths = rows.Select(row => row.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>().Last().Current.Name).Order();
                return paths.SequenceEqual(expected.Order());
            }, "custom folder scope or result paths are wrong: " + string.Join(",", expected));
        }
    }
    if (scenario == "image-menu")
    {
        Console.WriteLine(ImageMenuSmoke.Run(window, root, Option(args, "--screenshots")));
        Close(window);
        process.WaitForExit(5000);
        return 0;
    }
    if (scenario is "reading" or "startup-reading")
    {
        InspectReading(window, Option(args, "--screenshots"), root, verifyAttachments: scenario == "reading");
        Console.WriteLine(scenario == "startup-reading"
            ? "PASS startup-reading first-html=painted standalone=painted text=visible empty=cleared html-restored=painted"
            : "PASS reading html=visible standalone=visible text=visible empty=cleared multiselect=cleared html-restored=visible");
        Close(window);
        process.WaitForExit(5000);
        return 0;
    }
    if (scenario == "remote-images")
    {
        InspectRemoteImages(window, services, () => Volatile.Read(ref imageRequests), imageOrigin!, Option(args, "--screenshots"));
        Console.WriteLine("PASS remote-images default=blocked once=loaded remembered=confirmed standalone=allowed");
        Close(window);
        process.WaitForExit(5000);
        return 0;
    }
    if (scenario == "split-restore")
    {
        // Restarted several times: the saved local/connected split must hold on every start, not most.
        var fractions = new List<double>();
        for (var run = 0; run < 4; run++)
        {
            if (run > 0) process = Process.Start(start)!;
            var launched = process;
            var current = run == 0 ? window : WaitFor(() =>
            {
                launched.Refresh();
                return launched.MainWindowHandle == IntPtr.Zero ? null : AutomationElement.FromHandle(launched.MainWindowHandle);
            }, TimeSpan.FromSeconds(20));
            WaitForEnabled(current, "Fetch Log");
            Thread.Sleep(1500);
            var pane = FindNamed(current, "Local and connected mail splitter").Current.BoundingRectangle;
            var tree = FindNamed(current, "Local mail folders").Current.BoundingRectangle;
            // The tree sits below a 24px heading inside the top panel.
            fractions.Add((tree.Bottom - pane.Top) / (pane.Height - 4));
            PostMessage(NativeHandle(current), 0x0010, IntPtr.Zero, IntPtr.Zero);
            Check(process.WaitForExit(10000), "client did not close");
            services.Database.SetSetting("window.layout", System.Text.Json.JsonSerializer.Serialize(new
            {
                Bounds = new { X = 320, Y = 170, Width = 1280, Height = 820 }, Maximized = true, Dpi = 96,
                FolderWidth = 260, LocalFraction = 0.9007936507936508, MessageFraction = 0.5
            }));
        }
        var ok = fractions.All(value => Math.Abs(value - 0.9) < 0.03);
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} split-restore expected=0.90 got={string.Join(",", fractions.Select(value => value.ToString("F3")))}");
        return ok ? 0 : 1;
    }
    if (scenario == "tree-render")
    {
        // The folder tree as painted, for measuring against OE's own screenshot.
        WaitUntil(() => FindNamed(window, "Local mail folders").FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem)).Cast<AutomationElement>()
            .Any(node => node.Current.Name == "Health  (265)"), "the Health folder did not show its unread count");
        Thread.Sleep(500);
        CaptureWindow(NativeHandle(window), Option(args, "--screenshots"), "folder-tree");
        Close(window);
        process.WaitForExit(5000);
        Console.WriteLine("PASS tree-render health=265");
        return 0;
    }
    if (scenario == "addressing")
    {
        AddressingSmoke.Addressing(window, services, oeBook!, Option(args, "--screenshots"));
        Close(window);
        process.WaitForExit(5000);
        return 0;
    }
    if (scenario is "compose" or "compose-attachments" or "compose-recovery")
    {
        if (scenario == "compose-recovery") InspectComposeRecovery(window, services, root, Option(args, "--screenshots"));
        else InspectComposeAttachments(window, services, root, Option(args, "--screenshots"));
        Close(window);
        process.WaitForExit(5000);
        return 0;
    }
    else InspectToolsMenu(window, scenario, Option(args, "--screenshots"));

    var connectedAccounts = FindNamed(window, "Connected accounts");
    var alphabetical = Find(window, ControlType.Button, "Sort connected accounts alphabetically");
    var volume = Find(window, ControlType.Button, "Sort connected accounts by message volume");
    var newMail = Find(window, ControlType.Button, "Sort connected accounts by new mail");
    // The default sort is New, which puts zeta (newest mail) first once dates load; start from A–Z explicitly.
    Invoke(alphabetical);
    WaitForFirstAccount(connectedAccounts, "alpha@example.invalid");
    Invoke(volume);
    WaitForFirstAccount(connectedAccounts, "alpha@example.invalid");
    Invoke(newMail);
    WaitForFirstAccount(connectedAccounts, "zeta@example.invalid");
    Invoke(alphabetical);
    WaitForFirstAccount(connectedAccounts, "alpha@example.invalid");

    // The reading pane is a rich edit for its line spacing. UI Automation reports it as a Document whose
    // name is the text it shows, so it is found by type — the window has exactly one — and it answers no
    // WM_GETFONT, so its font cannot be probed the way the plain edits are.
    var messageBody = WaitFor(() => window.FindFirst(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)),
        TimeSpan.FromSeconds(10), "Could not find the reading pane document.");
    Check(messageBody.Current.IsEnabled, "the reading pane is disabled");
    Check(window.Current.IsEnabled, "window is not responsive after dialog automation");
    Console.WriteLine("PASS window=enabled controls=invoked body=document");
    Close(window);
    process.WaitForExit(5000);
    return 0;
}
finally
{
    imageServer?.Stop();
    if (process is { HasExited: false }) process.Kill(true);
    var smokeRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mailkeep-ui-smoke")) + Path.DirectorySeparatorChar;
    Check(Path.GetFullPath(root).StartsWith(smokeRoot, StringComparison.OrdinalIgnoreCase), "cleanup escaped the smoke-test root");
    // WebView2 releases its profile files shortly after the last owning window exits.
    var cleanupDeadline = DateTime.UtcNow.AddSeconds(10);
    while (Directory.Exists(root))
    {
        try { Directory.Delete(root, true); }
        catch (IOException) when (DateTime.UtcNow < cleanupDeadline) { Thread.Sleep(200); }
        catch (UnauthorizedAccessException) when (DateTime.UtcNow < cleanupDeadline) { Thread.Sleep(200); }
        catch (Exception error) when (bulkDrag && error is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("Retained bulk drag fixture after cleanup lock: " + root); break; }
    }
}

void ExportFixtureDatabase()
{
    if (Option(args, "--screenshots") is not { } directory) return;
    using var source = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={services.Paths.DatabasePath};Mode=ReadOnly;Pooling=False");
    using var target = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(directory, scenario + ".db")};Pooling=False");
    source.Open();
    target.Open();
    source.BackupDatabase(target);
}

static void InspectRemoteImages(AutomationElement window, MailkeepServices services, Func<int> requests, string origin, string? screenshots)
{
    var load = FindNamed(window, "Load blocked pictures");
    Thread.Sleep(500);
    Check(requests() == 0, "remote picture loaded before permission");
    CaptureWindow(NativeHandle(window), screenshots, "remote-images-blocked");
    Invoke(load);
    WaitUntil(() => window.FindFirst(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.NameProperty, "Load blocked pictures")) is null,
        "clicking the blue bar did not invoke its action");
    WaitUntil(() => requests() > 0, "clicking the blue bar did not load the picture");
    CaptureWindow(NativeHandle(window), screenshots, "remote-images-loaded");

    var list = FindNamed(window, "Message list");
    var rows = list.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true))
        .Cast<AutomationElement>().ToArray();
    Check(rows.Length == 2, "remote-images fixture needs two messages");
    ClickRow(NativeHandle(list), rows[1]);
    WaitFor(() => VisibleChild(NativeHandle(window), "RICHEDIT") != IntPtr.Zero ? window : null,
        TimeSpan.FromSeconds(10), "plain-text fixture did not replace the one-time remote image");
    ClickRow(NativeHandle(list), rows[0]);
    WaitFor(() => VisibleChild(NativeHandle(window), "RICHEDIT") == IntPtr.Zero
        && VisibleChild(NativeHandle(window), "Chrome") != IntPtr.Zero ? window : null,
        TimeSpan.FromSeconds(10), "remote-image message did not replace plain text");
    FindNamed(window, "Load blocked pictures");
    var always = FindNamed(window, "Image site permissions");
    var allowing = Task.Run(() => Invoke(always));
    var fixturePid = window.Current.ProcessId;
    var confirmation = WaitFor(() =>
    {
        var previous = IntPtr.Zero;
        while ((previous = FindWindowEx(IntPtr.Zero, previous, null, "Image domain permissions")) != IntPtr.Zero)
        {
            GetWindowThreadProcessId(previous, out var pid);
            if (pid == fixturePid) return AutomationElement.FromHandle(previous);
        }
        return null;
    }, TimeSpan.FromSeconds(10), "fixture image site dialog did not appear");
    CaptureWindow(NativeHandle(confirmation), screenshots, "image-site-radio-buttons");
    Select(FindNamed(confirmation, "Always allow pictures from " + origin));
    Invoke(FindNamed(confirmation, "Save image site permissions"));
    Check(allowing.Wait(5000), "remembered-origin confirmation did not close");
    WaitUntil(() => (services.Database.GetSetting("images.allowed_origins") ?? "").Contains(origin),
        "confirmed image origin was not remembered");

    SendMessage(NativeHandle(list), 0x0100, new IntPtr(13), IntPtr.Zero);
    var opened = WaitForWindow("Automation fixture");
    WaitUntil(() => VisibleChild(NativeHandle(opened), "Chrome") != IntPtr.Zero,
        "remembered image document did not open");
    Check(requests() == 1, "remembered image did not reuse the fetched cache");
    Check(opened.FindFirst(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.NameProperty, "Load blocked pictures")) is null,
        "remembered origin still displayed the blocked-pictures bar");
    CaptureWindow(NativeHandle(opened), screenshots, "remote-images-remembered");
    Close(opened);
}

static void InspectSearch(AutomationElement window, MailkeepServices services, string? screenshots)
{
    Select(FindNamed(FindNamed(window, "Connected accounts"), "alpha@example.invalid"));
    var mainList = FindNamed(window, "Message list");
    WaitUntil(() => (int)SendMessage(NativeHandle(mainList), 0x1004, IntPtr.Zero, IntPtr.Zero) == 3, "background folder was not loaded");
    var title = window.Current.Name;
    var selected = SendMessage(NativeHandle(mainList), 0x100c, new IntPtr(-1), new IntPtr(2));
    Invoke(FindNamed(Find(window, ControlType.ToolBar, "Main toolbar"), "Find"));
    var find = WaitForWindow("Find Message");
    SendText(NativeHandle(FindNamed(find, "Subject criteria")), 0x000c, IntPtr.Zero, "Automation fixture");
    Invoke(FindNamed(find, "Find Now"));
    var results = FindNamed(find, "Find results");
    WaitUntil(() => (int)SendMessage(NativeHandle(results), 0x1004, IntPtr.Zero, IntPtr.Zero) == 1, "subject search did not return its message");
    OpenResult("search-message");
    Check(window.Current.Name == title && (int)SendMessage(NativeHandle(mainList), 0x1004, IntPtr.Zero, IntPtr.Zero) == 3
        && SendMessage(NativeHandle(mainList), 0x100c, new IntPtr(-1), new IntPtr(2)) == selected, "opening a search hit changed the current folder or selection");
    services.Database.Upsert(new MessageInput(MailContract.LiveSource, "local-fixture", LocalFolderKinds.Inbox,
        "cancel-fixture", null, 0, "Cancellation fixture", "sender@example.invalid", "Sender", ["local@example.invalid"],
        DateTimeOffset.UtcNow, 1, false, 0, "Cancellation fixture body", null, null));
    SendText(NativeHandle(FindNamed(find, "Subject criteria")), 0x000c, IntPtr.Zero, "Cancellation fixture");
    using (var locked = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={services.Paths.SearchCatalogPath};Pooling=False"))
    {
        locked.Open();
        using var transaction = locked.BeginTransaction();
        Invoke(FindNamed(find, "Find Now"));
        WaitForEnabled(find, "Stop find");
        Check(!FindNamed(find, "Find Now").Current.IsEnabled, "another search can start while one is running");
        // The worker is waiting for the catalog write lock. The UI must still answer and accept Stop.
        Thread.Sleep(200);
        Invoke(FindNamed(find, "Stop find"));
        Check((int)SendMessage(NativeHandle(results), 0x1004, IntPtr.Zero, IntPtr.Zero) == 1, "Stop discarded the previous results");
        transaction.Rollback();
    }
    WaitForEnabled(find, "Find Now");
    Check(NativeControls(NativeHandle(find)).Any(control => control.Title.StartsWith("Search stopped", StringComparison.Ordinal)),
        "cancelled search did not report stopping");
    Check(ResultRow().FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
        .Any(item => item.Current.Name == "Automation fixture"), "cancelled search replaced the previous result");
    Invoke(FindNamed(find, "Find Now"));
    WaitUntil(() => ResultRow().FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
        .Any(item => item.Current.Name == "Cancellation fixture"), "search did not recover after Stop");
    Invoke(FindNamed(find, "New Search"));
    SendText(NativeHandle(FindNamed(find, "Message criteria")), 0x000c, IntPtr.Zero, "First attachment fixture");
    SendText(NativeHandle(FindNamed(find, "From criteria")), 0x000c, IntPtr.Zero, "sender@example.invalid");
    SendText(NativeHandle(FindNamed(find, "To criteria")), 0x000c, IntPtr.Zero, "local@example.invalid");
    SendText(NativeHandle(FindNamed(find, "Subject criteria")), 0x000c, IntPtr.Zero, "Automation fixture");
    ((TogglePattern)FindNamed(find, "Search message text").GetCurrentPattern(TogglePattern.Pattern)).Toggle();
    ((TogglePattern)FindNamed(find, "Search attachment filenames").GetCurrentPattern(TogglePattern.Pattern)).Toggle();
    Invoke(FindNamed(find, "Find Now"));
    WaitUntil(() => (int)SendMessage(NativeHandle(results), 0x1004, IntPtr.Zero, IntPtr.Zero) == 1, "attachment-content search did not find its parent message");
    var attachmentRow = ResultRow();
    Check(attachmentRow.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
        .Any(item => item.Current.Name.Contains("fixture.txt", StringComparison.Ordinal)), "attachment-content result omitted the matching filename");
    SendText(NativeHandle(FindNamed(find, "From criteria")), 0x000c, IntPtr.Zero, "absent@example.invalid");
    Invoke(FindNamed(find, "Find Now"));
    WaitUntil(() => (int)SendMessage(NativeHandle(results), 0x1004, IntPtr.Zero, IntPtr.Zero) == 0,
        "attachment result ignored the parent sender criterion");
    SendText(NativeHandle(FindNamed(find, "From criteria")), 0x000c, IntPtr.Zero, "sender@example.invalid");
    Invoke(FindNamed(find, "Find Now"));
    WaitUntil(() => (int)SendMessage(NativeHandle(results), 0x1004, IntPtr.Zero, IntPtr.Zero) == 1,
        "combined header/attachment result did not return");
    OpenResult("search-attachment-message");
    services.Database.Upsert(new MessageInput(MailContract.LiveSource, "local-fixture", LocalFolderKinds.Inbox,
        "close-search-fixture", null, 0, "Close search fixture", "sender@example.invalid", "Sender", [],
        DateTimeOffset.UtcNow, 1, false, 0, "Close search fixture body", null, null));
    using (var locked = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={services.Paths.SearchCatalogPath};Pooling=False"))
    {
        locked.Open();
        using var transaction = locked.BeginTransaction();
        Invoke(FindNamed(find, "Find Now"));
        WaitForEnabled(find, "Stop find");
        var findHandle = NativeHandle(find);
        Close(find);
        WaitUntil(() => !IsWindowVisible(findHandle), "Find window cannot close while searching");
        transaction.Rollback();
    }
    Invoke(FindNamed(Find(window, ControlType.ToolBar, "Main toolbar"), "Find"));
    var reopened = WaitForWindow("Find Message");
    WaitForEnabled(reopened, "Find Now");
    var reopenedHandle = NativeHandle(reopened);
    Close(reopened);
    WaitUntil(() => !IsWindowVisible(reopenedHandle), "reopened Find window did not close");
    Check(window.Current.Name == title && (int)SendMessage(NativeHandle(mainList), 0x1004, IntPtr.Zero, IntPtr.Zero) == 3,
        "closing search lost the original folder");
    Console.WriteLine("PASS search subject=found combined-headers-and-attachment=found wrong-sender=excluded enter=opens-html folder=retained selection=retained stop=responsive previous-results=retained retry=works close-during-search=works");

    AutomationElement ResultRow() => results.FindAll(TreeScope.Children,
        new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true)).Cast<AutomationElement>().Single();
    void OpenResult(string name)
    {
        Select(ResultRow());
        SendMessage(NativeHandle(results), 0x0100, new IntPtr(13), IntPtr.Zero);
        var opened = WaitForWindow("Automation fixture");
        VerifyHtmlPaint(opened, screenshots, name);
        Close(opened);
    }
}

static void InspectLayout(AutomationElement window, Process process, ProcessStartInfo start, MailkeepServices services)
{
    var handle = NativeHandle(window);
    GetWindowRect(handle, out var initial);
    Check(initial.Left == 80 && initial.Top == 90 && initial.Right - initial.Left == 1100 && initial.Bottom - initial.Top == 720,
        "saved window bounds were not restored");
    var folders = FindNamed(window, "Local and connected mail splitter");
    Check(Math.Abs(folders.Current.BoundingRectangle.Width - 320) < 2, "saved folder width was not restored");
    var localTree = FindNamed(window, "Local mail folders");
    Check(Math.Abs((localTree.Current.BoundingRectangle.Bottom - folders.Current.BoundingRectangle.Top)
        / (folders.Current.BoundingRectangle.Height - 4) - 0.5) < 0.01, "saved local/connected proportion was not restored");
    var messages = FindNamed(window, "Message list");
    var split = FindNamed(window, "Messages and reading pane splitter");
    Check(Math.Abs(messages.Current.BoundingRectangle.Height / (split.Current.BoundingRectangle.Height - 4) - 0.55) < 0.01,
        "saved reading pane proportion was not restored");
    Check((int)SendMessage(NativeHandle(messages), 0x101d, IntPtr.Zero, IntPtr.Zero) == 185, "message column width was not restored");
    SetWindowPos(handle, IntPtr.Zero, 120, 130, 1150, 760, 0x0014);
    SendMessage(NativeHandle(messages), 0x101e, IntPtr.Zero, new IntPtr(235)); // LVM_SETCOLUMNWIDTH
    SetForegroundWindow(handle);
    SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0043);
    Thread.Sleep(150);
    var folderBounds = folders.Current.BoundingRectangle;
    Drag(FindNamed(window, "Folders and messages splitter"), 322, 80, 55, 0);
    Check(Math.Abs(folders.Current.BoundingRectangle.Width - 375) < 3,
        $"folder splitter drag did not resize the pane: before={folderBounds}, after={folders.Current.BoundingRectangle}");
    var messageBounds = messages.Current.BoundingRectangle;
    Drag(split, 80, (int)messageBounds.Height + 2, 0, -40);
    var expectedMessageHeight = messages.Current.BoundingRectangle.Height;
    Check(expectedMessageHeight < messageBounds.Height - 35, "reading splitter drag did not resize the pane");
    var localBottom = localTree.Current.BoundingRectangle.Bottom - folders.Current.BoundingRectangle.Top;
    Drag(folders, 80, (int)localBottom + 2, 0, -35);
    var expectedLocalBottom = localTree.Current.BoundingRectangle.Bottom - folders.Current.BoundingRectangle.Top;
    Check(expectedLocalBottom < localBottom - 30, "local/connected splitter drag did not resize the pane");
    Close(window);
    Check(process.WaitForExit(5000), "layout fixture did not close");
    using (var saved = System.Text.Json.JsonDocument.Parse(services.Database.GetSetting("window.layout")!))
    {
        var layout = saved.RootElement;
        Check(layout.GetProperty("Bounds").GetProperty("Width").GetInt32() == 1150, "resized width was not saved");
        Check(layout.GetProperty("FolderWidth").GetInt32() == 375, "dragged folder width was not saved");
        Check(layout.GetProperty("MessageColumns")[0].GetInt32() == 235, "changed column width was not saved");
    }
    using (var restarted = Process.Start(start)!)
    {
        try
        {
            var restored = ReadyWindow(restarted);
            var restoredHandle = NativeHandle(restored);
            GetWindowRect(restoredHandle, out var bounds);
            Check(bounds.Left == 120 && bounds.Top == 130 && bounds.Right - bounds.Left == 1150 && bounds.Bottom - bounds.Top == 760,
                "moved/resized bounds changed across restart");
            Check(Math.Abs(FindNamed(restored, "Local and connected mail splitter").Current.BoundingRectangle.Width - 375) < 2,
                "dragged folder width changed across restart");
            Check(Math.Abs(FindNamed(restored, "Local mail folders").Current.BoundingRectangle.Bottom
                - FindNamed(restored, "Local and connected mail splitter").Current.BoundingRectangle.Top - expectedLocalBottom) < 2,
                "dragged local/connected proportion changed across restart");
            var restoredMessages = FindNamed(restored, "Message list");
            Check(Math.Abs(restoredMessages.Current.BoundingRectangle.Height - expectedMessageHeight) < 2,
                "dragged reading pane changed across restart");
            Check((int)SendMessage(NativeHandle(restoredMessages), 0x101d, IntPtr.Zero, IntPtr.Zero) == 235,
                "changed column width changed across restart");
            ShowWindow(restoredHandle, 3); // Maximize, then minimize: restore the last usable state.
            ShowWindow(restoredHandle, 6);
            PostMessage(restoredHandle, 0x0010, IntPtr.Zero, IntPtr.Zero);
            Check(restarted.WaitForExit(5000), "minimized fixture did not close");
        }
        finally { if (!restarted.HasExited) restarted.Kill(true); }
    }
    using (var restarted = Process.Start(start)!)
    {
        try
        {
            var restored = ReadyWindow(restarted);
            Check(((WindowPattern)restored.GetCurrentPattern(WindowPattern.Pattern)).Current.WindowVisualState == WindowVisualState.Maximized,
                "maximized state was lost when closed while minimized");
            ShowWindow(NativeHandle(restored), 9);
            GetWindowRect(NativeHandle(restored), out var bounds);
            Check(bounds.Right - bounds.Left == 1150 && bounds.Bottom - bounds.Top == 760, "normal restore bounds were lost");
            Close(restored);
            Check(restarted.WaitForExit(5000), "restored fixture did not close");
        }
        finally { if (!restarted.HasExited) restarted.Kill(true); }
    }
    var offscreen = System.Text.Json.Nodes.JsonNode.Parse(services.Database.GetSetting("window.layout")!)!;
    offscreen["Bounds"]!["X"] = 50000;
    offscreen["Bounds"]!["Y"] = 50000;
    offscreen["Bounds"]!["Width"] = 10000;
    offscreen["Bounds"]!["Height"] = 10000;
    services.Database.SetSetting("window.layout", offscreen.ToJsonString());
    using (var restarted = Process.Start(start)!)
    {
        try
        {
            var restored = ReadyWindow(restarted);
            var restoredHandle = NativeHandle(restored);
            GetWindowRect(restoredHandle, out var bounds);
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            Check(GetMonitorInfo(MonitorFromWindow(restoredHandle, 2), ref monitor), "could not read the restored monitor");
            Check(bounds.Left >= monitor.Work.Left && bounds.Top >= monitor.Work.Top
                && bounds.Right <= monitor.Work.Right && bounds.Bottom <= monitor.Work.Bottom,
                "disconnected-screen bounds were not clamped to a connected work area");
            Close(restored);
            Check(restarted.WaitForExit(5000), "offscreen fixture did not close");
        }
        finally { if (!restarted.HasExited) restarted.Kill(true); }
    }
    services.Database.SetSetting("window.layout", "{broken json");
    using (var restarted = Process.Start(start)!)
    {
        try
        {
            var restored = ReadyWindow(restarted);
            Close(restored);
            Check(restarted.WaitForExit(5000), "invalid-settings fixture did not close");
        }
        finally { if (!restarted.HasExited) restarted.Kill(true); }
    }
    Console.WriteLine("PASS layout bounds=retained three-splitters=retained columns=retained maximized=retained minimized=not-restored offscreen=clamped invalid-settings=ignored");

    static AutomationElement ReadyWindow(Process app)
    {
        var result = WaitFor(() =>
        {
            app.Refresh();
            Check(!app.HasExited, "layout fixture exited at startup");
            return app.MainWindowHandle == IntPtr.Zero ? null : AutomationElement.FromHandle(app.MainWindowHandle);
        }, TimeSpan.FromSeconds(10));
        WaitForEnabled(result, "Fetch Log");
        return result;
    }

    static void Drag(AutomationElement splitter, int x, int y, int dx, int dy)
    {
        var target = NativeHandle(splitter);
        SendMessage(target, 0x0201, new IntPtr(1), new IntPtr((y << 16) | x));
        Thread.Sleep(100);
        for (var step = 1; step <= 11; step++)
        {
            SendMessage(target, 0x0200, new IntPtr(1), new IntPtr(((y + dy * step / 11) << 16) | (x + dx * step / 11)));
            Thread.Sleep(30);
        }
        SendMessage(target, 0x0202, IntPtr.Zero, new IntPtr(((y + dy) << 16) | (x + dx)));
        Thread.Sleep(150);
    }
}

// The automation tree is live: while a dialog is opening or closing, a subtree can briefly report nothing,
// so every lookup keeps asking for a few seconds instead of failing on the first miss.
static AutomationElement Find(AutomationElement root, ControlType type, string name)
    => WaitFor(() => root.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, type),
            new PropertyCondition(AutomationElement.NameProperty, name))),
        TimeSpan.FromSeconds(10), $"Could not find {type.ProgrammaticName} '{name}'.");
static AutomationElement FindNamed(AutomationElement root, string name)
    => WaitFor(() => root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name)),
        TimeSpan.FromSeconds(10), $"Could not find automation element '{name}'.");
static AutomationElement WaitFor(Func<AutomationElement?> find, TimeSpan timeout, string message = "UI Automation element did not appear.")
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until)
    {
        try { if (find() is { } element) return element; }
        catch (ElementNotAvailableException) { }
        Thread.Sleep(100);
    }
    throw new TimeoutException(message);
}

static void WaitForFirstAccount(AutomationElement list, string expected)
{
    var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
    while (DateTime.UtcNow < until)
    {
        var rows = list.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>()
            .Select(element => element.Current.Name)
            .Where(name => name.Contains('@'))
            .ToArray();
        if (rows.Length > 0 && rows[0].StartsWith(expected, StringComparison.OrdinalIgnoreCase)) return;
        Thread.Sleep(50);
    }
    throw new InvalidOperationException($"Connected account sort did not put '{expected}' first.");
}

static void Invoke(AutomationElement element)
    => ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
static void Select(AutomationElement element)
    => ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
// The dialog is always closed again, otherwise the caller's Invoke would stay blocked on the modal loop forever.
static int Inspecting(string title, Func<AutomationElement, string> inspect)
{
    AutomationElement window;
    try { window = WaitForWindow(title); }
    catch (TimeoutException) { throw new TimeoutException($"The '{title}' window never appeared. Open windows: {OpenWindows()}"); }
    Trace($"'{title}' window found");
    try
    {
        var summary = inspect(window);
        Console.WriteLine(summary);
        return 0;
    }
    finally
    {
        try { Close(window); } catch { }
    }
}
// Targeted lookups only, and the account grids are never walked: a DataGridView subtree stalls across processes.
static int InspectAccountsDialog() => Inspecting("Accounts", dialog =>
{
    var tabs = ChildNamed(dialog, "Account kinds")
        ?? throw new InvalidOperationException($"The account tabs are missing. Children: {Describe(dialog)}");
    var pages = tabs.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
        .Cast<AutomationElement>().Select(element => element.Current.Name).ToArray();
    // WinForms reports the selected page's name for every tab item, so the tabs are counted, not named.
    Trace($"tabs: {pages.Length}");
    Check(pages.Length == 2, $"expected Local Mail and Connected tabs, found {pages.Length}");
    var items = tabs.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
        .Cast<AutomationElement>().ToArray();
    var local = EnabledIn(dialog, "Local account actions");
    foreach (var button in new[] { "Add local account", "Edit local account", "Authorize local account",
        "Set default From address", "Remove local account" })
        Check(local.Contains(button), $"'{button}' is missing or disabled");
    // Authorize verifies as part of the sign-in, and importing belongs to the migration toolkit —
    // neither may creep back in as its own button.
    Check(!local.Any(name => name.Contains("Test", StringComparison.OrdinalIgnoreCase)), "a separate Test button came back");
    Check(!local.Any(name => name.Contains("from now", StringComparison.OrdinalIgnoreCase)), "a Start From Now button came back");
    Check(!local.Any(name => name.Contains("OE Classic", StringComparison.OrdinalIgnoreCase)),
        "the product offers an OE Classic import");
    Select(items[1]);
    Trace("connected tab selected");
    var connected = EnabledIn(dialog, "Connected account actions");
    foreach (var button in new[] { "Add connected account", "Remove connected account" })
        Check(connected.Contains(button), $"'{button}' is missing or disabled");
    // Receiving-only really means receiving-only: nothing on this tab may offer to send.
    Check(!connected.Contains("Set default From address"), "the Connected tab offers a sending action");
    return $"PASS accounts tabs=2 local={local.Length} connected={connected.Length}";
});

// Scoped to the three named panels: sweeping this window's whole subtree across processes stalls on the
// multi-line body editor, the same way the identity grid does.
static int InspectComposeWindow() => Inspecting("New Message", compose =>
{
    var headers = EnabledIn(compose, "Compose headers");
    foreach (var field in new[] { "From identity", "To recipients", "Cc recipients", "Bcc recipients", "Subject" })
        Check(headers.Contains(field), $"'{field}' is missing or disabled");
    Trace("locating Message body editor");
    var body = ChildNamed(compose, "Message body editor")
        ?? throw new InvalidOperationException($"'Message body editor' is missing. Children: {Describe(compose)}");
    Check(body.Current.IsEnabled, "'Message body editor' is disabled");
    var actions = EnabledIn(compose, "Compose actions");
    foreach (var button in new[] { "Send message", "Save draft", "Cancel compose" })
        Check(actions.Contains(button), $"'{button}' is missing or disabled");
    return $"PASS compose fields={headers.Length + 1} actions={actions.Length}";
});

static string[] EnabledIn(AutomationElement dialog, string panel)
{
    Trace($"locating {panel}");
    var found = ChildNamed(dialog, panel)
        ?? throw new InvalidOperationException($"'{panel}' is missing. Children: {Describe(dialog)}");
    Trace($"reading {panel}");
    var names = found.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>()
        .Where(element => element.Current.IsEnabled).Select(element => element.Current.Name).ToArray();
    Trace($"read {panel}");
    return names;
}

// An inspector that stalls is otherwise silent, so each step is announced on stderr and reported on failure.
static void Trace(string step) => Console.Error.WriteLine("step: " + step);

static void InspectReading(AutomationElement window, string? screenshots, string fixtureRoot, bool verifyAttachments = true)
{
    var handle = NativeHandle(window);
    WaitFor(() => VisibleChild(handle, "Chrome_WidgetWin_") != IntPtr.Zero ? window : null,
        TimeSpan.FromSeconds(15), "HTML browser is missing, hidden or has zero bounds.");
    Check(VisibleChild(handle, "RICHEDIT") == IntPtr.Zero, "text pane obscures HTML");
    VerifyHtmlPaint(window, screenshots, "preview");
    Check(FindNamed(window, "Message attachments").Current.IsEnabled, "preview attachment picker is disabled");
    if (verifyAttachments)
    {
        InspectAttachmentSave(window, Path.Combine(fixtureRoot, "saved-from-preview"));
    }

    // Enter opens the selected message through the same handler as double-click and search results.
    var list = FindNamed(window, "Message list");
    SendMessage(NativeHandle(list), 0x0100, new IntPtr(13), IntPtr.Zero);
    var opened = WaitForWindow("Automation fixture");
    WaitFor(() => VisibleChild(NativeHandle(opened), "Chrome_WidgetWin_") != IntPtr.Zero ? opened : null,
        TimeSpan.FromSeconds(15), "standalone message has no visible HTML browser");
    Check(VisibleChild(NativeHandle(opened), "RICHEDIT") == IntPtr.Zero, "standalone text pane obscures HTML");
    VerifyHtmlPaint(opened, screenshots, "message-window");
    Check(FindNamed(opened, "Message attachments").Current.IsEnabled, "standalone attachment picker is disabled");
    if (verifyAttachments)
    {
        InspectAttachmentSave(opened, Path.Combine(fixtureRoot, "saved-from-window"));
    }
    Close(opened);

    var accounts = FindNamed(window, "Connected accounts");
    Select(FindNamed(accounts, "alpha@example.invalid"));
    WaitFor(() => VisibleChild(handle, "RICHEDIT") != IntPtr.Zero ? window : null,
        TimeSpan.FromSeconds(10), "plain-text pane did not return");
    Check(VisibleChild(handle, "Chrome_WidgetWin_") == IntPtr.Zero, "HTML remains visible over plain text");
    WaitUntil(() => CurrentText()?.Contains("Fixture body") == true, "plain-text message body is missing");

    var folders = FindNamed(window, "Local mail folders");
    Select(FindNamed(folders, "Drafts"));
    WaitUntil(() => CurrentText() is { } empty && string.IsNullOrWhiteSpace(empty), "empty folder retained the previous body");
    Check(VisibleChild(handle, "Chrome_WidgetWin_") == IntPtr.Zero, "empty folder retained HTML");
    var inbox = folders.FindAll(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem)).Cast<AutomationElement>()
        .Single(item => item.Current.Name == "Inbox" || item.Current.Name.StartsWith("Inbox  (", StringComparison.Ordinal));
    Select(inbox);
    WaitFor(() => VisibleChild(handle, "Chrome_WidgetWin_") != IntPtr.Zero ? window : null,
        TimeSpan.FromSeconds(10), "HTML did not return after text and empty views");
    if (!verifyAttachments)
    {
        VerifyHtmlPaint(window, screenshots, "restored-preview");
        return; // Startup reading checks first paint and view switches; selection gestures have their own scenarios.
    }
    var rows = list.FindAll(TreeScope.Children,
        new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true)).Cast<AutomationElement>().ToArray();
    Check(rows.Length == 2, $"expected two reading fixtures, found {rows.Length}");
    ((SelectionItemPattern)rows[1].GetCurrentPattern(SelectionItemPattern.Pattern)).AddToSelection();
    WaitFor(() => VisibleChild(handle, "RICHEDIT") != IntPtr.Zero ? window : null,
        TimeSpan.FromSeconds(10), "multiple selection did not clear the HTML pane");
    WaitUntil(() => CurrentText() is { } empty && string.IsNullOrWhiteSpace(empty), "multiple selection retained the previous message body");
    // Home without Shift must collapse the range to one row and restore the first message.
    list.SetFocus();
    PostMessage(NativeHandle(list), 0x0100, new IntPtr(0x24), IntPtr.Zero);
    PostMessage(NativeHandle(list), 0x0101, new IntPtr(0x24), IntPtr.Zero);
    WaitFor(() => VisibleChild(handle, "Chrome_WidgetWin_") != IntPtr.Zero ? window : null,
        TimeSpan.FromSeconds(10), "single selection did not restore HTML");
    WaitUntil(() => (int)SendMessage(NativeHandle(list), 0x1032, IntPtr.Zero, IntPtr.Zero) == 1,
        "Home did not collapse the multiple selection");
    VerifyHtmlPaint(window, screenshots, "single-selection");

    string? CurrentText()
    {
        try
        {
            var richEdit = VisibleChild(handle, "RICHEDIT");
            if (richEdit == IntPtr.Zero) return null;
            var length = (int)SendMessage(richEdit, 0x000e, IntPtr.Zero, IntPtr.Zero);
            var text = new StringBuilder(length + 1);
            ReadNativeText(richEdit, 0x000d, new IntPtr(text.Capacity), text);
            return text.ToString();
        }
        catch (ElementNotAvailableException) { return null; } // Reacquire the provider after an asynchronous preview switch.
    }
}

static void InspectAttachmentSave(AutomationElement window, string destination)
{
    Invoke(FindNamed(window, "Message attachments"));
    var menu = WaitFor(() => AutomationElement.RootElement.FindFirst(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.NameProperty, "Save attachments")),
        TimeSpan.FromSeconds(5), "paperclip Save attachments menu item is missing");
    Check(menu.Current.IsEnabled, "paperclip cannot save attachments");
    Directory.CreateDirectory(destination);
    var button = menu;
    var invocation = Task.Run(() => Invoke(button));
    ChooseFile("Save attachments to", destination, folder: true);
    Check(invocation.Wait(5000), "attachment folder picker did not return");
    WaitFor(() => File.Exists(Path.Combine(destination, "fixture.txt"))
        && File.Exists(Path.Combine(destination, "second.txt")) ? window : null,
        TimeSpan.FromSeconds(10), "attachment files were not saved");
    Check(File.ReadAllText(Path.Combine(destination, "fixture.txt")) == "First attachment fixture", "Save attachments changed the first file");
    Check(File.ReadAllText(Path.Combine(destination, "second.txt")) == "Second attachment fixture", "Save attachments changed the second file");
    Check(Directory.GetFiles(destination, "*.png").Length == 1, "Save attachments lost the embedded image");
    using var savedImage = new System.Drawing.Bitmap(Directory.GetFiles(destination, "*.png").Single());
    Check(savedImage.Width == 32 && savedImage.Height == 32 && savedImage.GetPixel(16, 16).ToArgb() == System.Drawing.Color.Magenta.ToArgb(),
        "Save attachments changed the embedded image");
    Console.WriteLine("PASS paperclip Save attachments " + Path.GetFileName(destination));
}

static void ChooseFile(string title, string destination, bool folder = false)
{
    Trace("waiting for " + title);
    var deadline = DateTime.UtcNow.AddSeconds(10);
    var handle = IntPtr.Zero;
    var filename = IntPtr.Zero;
    while (filename == IntPtr.Zero && DateTime.UtcNow < deadline)
    {
        handle = FindWindow("#32770", title);
        if (handle != IntPtr.Zero) EnumChildWindows(handle, (child, _) =>
        {
            var name = new StringBuilder(256);
            GetClassName(child, name, name.Capacity);
            if (name.ToString() == "Edit" && (folder ? GetDlgCtrlID(child) == 1152 : GetDlgCtrlID(child) is 1001 or 1148) && IsWindowVisible(child))
            { filename = child; return false; }
            return true;
        }, IntPtr.Zero);
        if (filename == IntPtr.Zero) Thread.Sleep(100);
    }
    Check(filename != IntPtr.Zero, title + " file-name edit is missing; window=" + handle + "; controls="
        + (handle == IntPtr.Zero ? "none" : string.Join(" | ", NativeControls(handle).Select(control => control.Class + ":" + GetDlgCtrlID(control.Handle) + ":" + control.Title))));
    if (folder)
    {
        // The shell folder picker needs edit notifications, not just WM_SETTEXT.
        SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0003);
        SendText(filename, 0x000c, IntPtr.Zero, "");
        foreach (var character in destination)
            SendMessage(filename, 0x0102, new IntPtr(character), IntPtr.Zero); // WM_CHAR sends shell edit notifications.
        var entered = new StringBuilder(1024);
        ReadNativeText(filename, 0x000d, new IntPtr(entered.Capacity), entered);
        Check(entered.ToString() == destination, "folder picker received the wrong path: " + entered);
        ClickNative(handle, "Select Folder");
        return;
    }
    SendText(filename, 0x000c, IntPtr.Zero, destination);
    PostMessage(handle, 0x0111, new IntPtr(1), IntPtr.Zero);
}

static void VerifyHtmlPaint(AutomationElement window, string? screenshots, string name)
    => CaptureWindow(NativeHandle(window), screenshots, name, requireHtml: true);

static void CaptureWindow(IntPtr handle, string? screenshots, string name, bool requireHtml = false)
{
    ShowWindow(handle, 9);
    SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0043);
    try
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            ShowWindow(handle, 9);
            SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0043);
            Thread.Sleep(200);
            GetWindowRect(handle, out var bounds);
            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            var screen = GetDC(IntPtr.Zero);
            var memory = CreateCompatibleDC(screen);
            var bitmap = CreateCompatibleBitmap(screen, width, height);
            var previous = SelectObject(memory, bitmap);
            try
            {
                Check(BitBlt(memory, 0, 0, width, height, screen, bounds.Left, bounds.Top, 0x00CC0020), "screen capture failed");
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero,
                    System.Windows.Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(source,
                    System.Windows.Media.PixelFormats.Bgra32, null, 0);
                var pixels = new byte[width * height * 4];
                converted.CopyPixels(pixels, width * 4, 0);
                var green = 0;
                var magenta = 0;
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i] == 0xdf && pixels[i + 1] == 0xf0 && pixels[i + 2] == 0xdf) green++;
                    if (pixels[i] == 255 && pixels[i + 1] == 0 && pixels[i + 2] == 255) magenta++;
                }
                var htmlPainted = green > 10000 && magenta > 4000;
                if (!requireHtml || htmlPainted || DateTime.UtcNow >= deadline)
                {
                    if (screenshots is not null)
                    {
                        Directory.CreateDirectory(screenshots);
                        using var output = File.Create(Path.Combine(screenshots, name + ".png"));
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(converted));
                        encoder.Save(output);
                    }
                    if (!requireHtml || htmlPainted)
                    {
                        Console.WriteLine(requireHtml ? $"PASS {name} HTML and embedded image painted ({green} background, {magenta} image pixels)" : $"Captured {name}");
                        return;
                    }
                }
                Check(DateTime.UtcNow < deadline, $"{name} browser exists but the HTML fixture did not paint");
            }
            finally
            {
                SelectObject(memory, previous);
                DeleteObject(bitmap);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }
    finally { SetWindowPos(handle, new IntPtr(-2), 0, 0, 0, 0, 0x0043); }
}

static AutomationElement ReadingText(AutomationElement window) => WaitFor(() =>
    window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)),
    TimeSpan.FromSeconds(10), "plain-text document missing");

static void InspectComposeAttachments(AutomationElement window, MailkeepServices services, string root, string? screenshots)
{
    var original = Path.Combine(root, "compose-fixture.txt");
    File.WriteAllText(original, "Compose attachment bytes");
    var newMail = FindNamed(window, "New Mail");
    var opening = Task.Run(() => Invoke(newMail));
    var compose = WaitNativeWindow("New Message");
    FillCompose(compose, "Attachment draft fixture");
    ClickNative(compose, "Attach…");
    ChooseFile("Attach files", original);
    WaitUntil(() => AttachmentCount(compose) == 1, "file picker did not add the attachment");
    CaptureWindow(compose, screenshots, "compose-attachment");
    ClickNative(compose, "Save Draft");
    Check(opening.Wait(5000), "New Mail did not close after saving");
    WaitUntil(() => services.Database.ListOutgoing(OutgoingStates.Draft, 10).Count == 1, "draft was not saved");
    var draft = services.Database.ListOutgoing(OutgoingStates.Draft, 10).Single();
    Check(services.Database.GetOutgoingAttachments(draft.Id).Count == 1, "draft lost its attachment");
    File.Delete(original);

    var folders = FindNamed(window, "Local mail folders");
    SelectFolder(folders, "Drafts");
    var messages = FindNamed(window, "Message list");
    SendMessage(NativeHandle(messages), 0x0100, new IntPtr(13), IntPtr.Zero);
    compose = WaitNativeWindow("Draft - Attachment draft fixture");
    Check(AttachmentCount(compose) == 1, "reopening the draft lost its attachment");
    Check(File.ReadAllText(services.Database.ExtractOutgoingAttachment(draft.Id, 0)) == "Compose attachment bytes", "draft depended on the deleted original file");
    var list = NativeControls(compose).Single(control => control.Class.Contains("LISTBOX", StringComparison.OrdinalIgnoreCase)).Handle;
    SendMessage(list, 0x0185, new IntPtr(1), IntPtr.Zero); // LB_SETSEL selects the first attachment for removal.
    // Programmatic LB_SETSEL does not send the selection-change notification that a user click sends.
    SendMessage(GetParent(list), 0x0111, new IntPtr((1 << 16) | (GetDlgCtrlID(list) & 0xffff)), list);
    ClickNative(compose, "Remove");
    WaitUntil(() => AttachmentCount(compose) == 0, "Remove did not clear the attachment");
    ClickNative(compose, "Save Draft");
    WaitUntil(() => services.Database.GetOutgoingAttachments(draft.Id).Count == 0, "removed attachment survived a draft save");

    SelectFolder(folders, "Inbox");
    var forward = WaitForEnabled(window, "Forward");
    var forwarding = Task.Run(() => Invoke(forward));
    compose = WaitNativeWindow("Fw: Automation fixture");
    Check(AttachmentCount(compose) == 2, "forward did not retain the original attachments");
    FillCompose(compose, "Forward attachment fixture");
    CaptureWindow(compose, screenshots, "forward-attachments");
    ClickNative(compose, "Save Draft");
    Check(forwarding.Wait(5000), "Forward did not close after saving");
    WaitUntil(() => services.Database.ListOutgoing(OutgoingStates.Draft, 10).Count == 2, "forward draft was not saved");
    var forwarded = services.Database.ListOutgoing(OutgoingStates.Draft, 10).Single(value => value.Subject == "Forward attachment fixture");
    Check(services.Database.GetOutgoingAttachments(forwarded.Id).Count == 2, "saved forward lost its attachments");
    Check(File.ReadAllText(services.Database.ExtractOutgoingAttachment(forwarded.Id, 1)) == "Second attachment fixture", "forward attachment bytes changed");
    Console.WriteLine("PASS compose attach=added draft=snapshotted reopen=retained remove=persisted forward=retained");
}

static void SelectFolder(AutomationElement folders, string name)
{
    var item = folders.FindAll(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem)).Cast<AutomationElement>()
        .Single(value => value.Current.Name == name || value.Current.Name.StartsWith(name + "  (", StringComparison.Ordinal));
    Select(item);
}

static void AssertInboxRefreshed(AutomationElement tree, AutomationElement messages)
{
    WaitUntil(() => (int)SendMessage(NativeHandle(messages), 0x1004, IntPtr.Zero, IntPtr.Zero) == 0,
        "Merged Inbox retained filed messages or their imported copies");
    var handle = NativeHandle(tree);
    var root = SendMessage(handle, 0x110a, IntPtr.Zero, IntPtr.Zero);
    var inbox = SendMessage(handle, 0x110a, new IntPtr(4), root);
    var selected = SendMessage(handle, 0x110a, new IntPtr(9), IntPtr.Zero);
    Check(inbox != IntPtr.Zero && selected == inbox, "Filing changed the selected source folder");
}

static void InspectComposeRecovery(AutomationElement window, MailkeepServices services, string root, string? screenshots)
{
    var trigger = FindNamed(window, "New Mail");
    var opening = Task.Run(() => Invoke(trigger));
    var compose = WaitNativeWindow("New Message");
    FillCompose(compose, "Incomplete draft fixture");
    var to = NativeControls(compose).Where(control => control.Class.Contains(".EDIT.", StringComparison.OrdinalIgnoreCase)).OrderBy(control => control.Top).First();
    SendText(to.Handle, 0x000c, IntPtr.Zero, "");
    ClickNative(compose, "Save Draft");
    Check(opening.Wait(5000), "recipientless draft editor did not close");
    WaitUntil(() => services.Database.ListOutgoing(OutgoingStates.Draft, 10).Count == 1, "recipientless draft was not saved");
    var draft = services.Database.ListOutgoing(OutgoingStates.Draft, 10).Single();
    Check(draft.To.Count == 0 && draft.BodyText == "Compose body fixture", "unfinished draft changed");

    SelectFolder(FindNamed(window, "Local mail folders"), "Drafts");
    var messages = FindNamed(window, "Message list");
    SendMessage(NativeHandle(messages), 0x0100, new IntPtr(13), IntPtr.Zero);
    compose = WaitNativeWindow("Draft - Incomplete draft fixture");
    FillCompose(compose, "Incomplete draft fixture");
    var path = Path.Combine(root, "recovery-fixture.txt");
    File.WriteAllText(path, "Recovery attachment");
    ClickNative(compose, "Attach…");
    ChooseFile("Attach files", path);
    WaitUntil(() => AttachmentCount(compose) == 1, "recovery attachment was not added");
    File.Delete(path);
    ClickNative(compose, "Save Draft");
    var error = WaitNativeWindow("Saving draft…");
    ClickNative(error, "OK");
    compose = WaitNativeWindow("Draft - Incomplete draft fixture");
    Check(AttachmentCount(compose) == 1, "save failure discarded the attachment list");
    Check(services.Database.GetOutgoing(draft.Id)!.To.Count == 0, "failed save changed the original draft");
    CaptureWindow(compose, screenshots, "compose-save-recovery");

    File.WriteAllText(path, "Recovery attachment");
    var identity = services.Identities.Find("fixture-3")!;
    services.Identities.Upsert(identity with { Status = SendingIdentityStates.NeedsAuthorization });
    ClickNative(compose, "Send");
    error = WaitNativeWindow("Placing the message in the Outbox…");
    ClickNative(error, "OK");
    compose = WaitNativeWindow("Draft - Incomplete draft fixture");
    Check(AttachmentCount(compose) == 1, "queue failure discarded attachments");
    Check(services.Database.ListOutgoing(OutgoingStates.Draft, 10).Count == 1, "queue failure duplicated the draft");
    Check(services.Database.GetOutgoing(draft.Id)!.BodyText == "Compose body fixture", "save recovery lost the editor body");
    services.Identities.Upsert(identity);
    ClickNative(compose, "Save Draft");
    WaitUntil(() => !IsWindowVisible(compose), "recovered draft editor did not close");
    Check(services.Database.ListOutgoing(OutgoingStates.Draft, 10).Count == 1, "retry created a second draft");
    Check(File.ReadAllText(services.Database.ExtractOutgoingAttachment(draft.Id, 0)) == "Recovery attachment", "recovery changed the saved attachment");
    Check(services.Database.ListOutgoing(OutgoingStates.Outbox, 10).Count == 0, "failed queue put a message in the Outbox");
    Console.WriteLine("PASS compose recipientless=saved save-error=retained queue-error=retained retry=one-draft");
}

static void FillCompose(IntPtr window, string subject)
{
    var edits = NativeControls(window).Where(control => control.Class.Contains(".EDIT.", StringComparison.OrdinalIgnoreCase)).OrderBy(control => control.Top).ToArray();
    Check(edits.Length == 5, $"expected To/Cc/Bcc/Subject/body editors, found {edits.Length}");
    SendText(edits[0].Handle, 0x000c, IntPtr.Zero, "recipient@example.invalid");
    SendText(edits[3].Handle, 0x000c, IntPtr.Zero, subject);
    SendText(edits[4].Handle, 0x000c, IntPtr.Zero, "Compose body fixture");
}

static int AttachmentCount(IntPtr window)
{
    var list = NativeControls(window).Single(control => control.Class.Contains("LISTBOX", StringComparison.OrdinalIgnoreCase));
    return (int)SendMessage(list.Handle, 0x018b, IntPtr.Zero, IntPtr.Zero); // LB_GETCOUNT
}

static void ClickNative(IntPtr window, string caption)
{
    var button = NativeControls(window).Single(control => control.Class.Contains("BUTTON", StringComparison.OrdinalIgnoreCase) && control.Title == caption);
    PostMessage(button.Handle, 0x00f5, IntPtr.Zero, IntPtr.Zero); // BM_CLICK
}

static List<(IntPtr Handle, string Class, string Title, int Top)> NativeControls(IntPtr window)
{
    var controls = new List<(IntPtr, string, string, int)>();
    EnumChildWindows(window, (child, _) =>
    {
        if (!IsWindowVisible(child)) return true;
        var name = new StringBuilder(256);
        var title = new StringBuilder(512);
        GetClassName(child, name, name.Capacity);
        GetWindowText(child, title, title.Capacity);
        GetWindowRect(child, out var bounds);
        controls.Add((child, name.ToString(), title.ToString(), bounds.Top));
        return true;
    }, IntPtr.Zero);
    return controls;
}

static IntPtr WaitNativeWindow(string title)
{
    var handle = IntPtr.Zero;
    WaitUntil(() => (handle = FindWindow(null, title)) != IntPtr.Zero && IsWindowVisible(handle), "window did not open: " + title);
    return handle;
}

static void WaitUntil(Func<bool> condition, string failure)
{
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while (!condition())
    {
        Check(DateTime.UtcNow < deadline, failure);
        Thread.Sleep(100);
    }
}

static IntPtr VisibleChild(IntPtr parent, string classFragment)
{
    var found = IntPtr.Zero;
    EnumChildWindows(parent, (child, _) =>
    {
        var className = new StringBuilder(256);
        GetClassName(child, className, className.Capacity);
        GetWindowRect(child, out var bounds);
        if (className.ToString().Contains(classFragment, StringComparison.OrdinalIgnoreCase)
            && IsWindowVisible(child) && bounds.Right - bounds.Left > 200 && bounds.Bottom - bounds.Top > 80)
        { found = child; return false; }
        return true;
    }, IntPtr.Zero);
    return found;
}

// Toolbar elements are re-resolved after a dialog run, since the earlier handles go stale.
static AutomationElement WaitForEnabled(AutomationElement window, string name) => WaitFor(() =>
{
    var candidate = window.FindFirst(TreeScope.Descendants, new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
        new PropertyCondition(AutomationElement.NameProperty, name)));
    return candidate?.Current.IsEnabled == true ? candidate : null;
}, TimeSpan.FromSeconds(10));

// Tools drop-down items only exist in the automation tree while the menu is expanded, so the checks and the
// dialog that follows them all happen with it open. One modal dialog per run: chaining several through a
// single instance leaves the menu and dialog state unreliable.
static void InspectToolsMenu(AutomationElement window, string scenario, string? screenshots)
{
    Expand(Find(window, ControlType.MenuItem, "Tools"));
    var accounts = Find(window, ControlType.MenuItem, "Accounts menu item");
    Check(accounts.Current.IsEnabled, "Accounts menu item is disabled");
    // The Accounts window is the only management surface: neither the add entries nor the identities
    // dialog may creep back into Tools.
    Check(window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Connected account menu")) is null,
        "Tools still offers an Add Connected Account entry");
    Check(window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Sending Identities menu item")) is null,
        "Tools still offers Sending Identities");
    Check(Find(window, ControlType.MenuItem, "Connected keep-alive menu item").Current.IsEnabled,
        "Connected keep-alive menu item is missing or disabled");
    if (scenario == "accounts")
    {
        var invocation = Task.Run(() => Invoke(accounts));
        var dialog = WaitNativeWindow("Accounts");
        var tabs = NativeControls(dialog).Single(control => control.Class.Contains("SysTabControl32", StringComparison.OrdinalIgnoreCase)).Handle;
        Check((int)SendMessage(tabs, 0x1304, IntPtr.Zero, IntPtr.Zero) == 2, "Accounts must have Local Mail and Connected tabs");
        var local = Buttons();
        foreach (var caption in new[] { "Add…", "Properties", "Authorize", "Default From", "Remove", "Close" })
            Check(local.Contains(caption), $"Local account action '{caption}' is missing or disabled");
        Check(local.Length == 6, "unexpected Local account actions");
        SendMessage(tabs, 0x0201, new IntPtr(1), new IntPtr((12 << 16) | 225));
        SendMessage(tabs, 0x0202, IntPtr.Zero, new IntPtr((12 << 16) | 225));
        WaitUntil(() => (int)SendMessage(tabs, 0x130b, IntPtr.Zero, IntPtr.Zero) == 1, "Connected tab was not selected");
        var connected = Buttons();
        // Authorize signs a Connected account in again after its token expires; nothing here may offer to send.
        Check(connected.Order().SequenceEqual(new[] { "Add…", "Authorize", "Close", "Remove" }.Order()), "Connected account actions include sending or are missing");
        if (screenshots is not null) CaptureWindow(dialog, screenshots, "accounts-connected");
        ClickNative(dialog, "Close");
        Check(invocation.Wait(5000), "Accounts invocation did not return after Close");
        Console.WriteLine("PASS accounts tabs=2 local-actions=6 connected-actions=4");

        string[] Buttons() => NativeControls(dialog)
            .Where(control => control.Class.Contains("BUTTON", StringComparison.OrdinalIgnoreCase) && IsWindowEnabled(control.Handle))
            .Select(control => control.Title).ToArray();
    }
    else throw new ArgumentException($"Unknown --scenario '{scenario}'; use accounts or compose.");
}

// A dialog that never opened, and a message box that opened instead, look identical without this.
static string OpenWindows()
    => string.Join(", ", AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition)
        .Cast<AutomationElement>().Select(element => element.Current.Name).Where(name => name.Length > 0));

// Failures in an out-of-process inspector are otherwise blind, so report what the dialog actually exposes.
static string Describe(AutomationElement root)
    => string.Join(", ", root.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>()
        .Select(element => $"{element.Current.ControlType.ProgrammaticName}:'{element.Current.Name}'"));

static AutomationElement? ChildNamed(AutomationElement root, string name)
{
    var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
    while (DateTime.UtcNow < until)
    {
        if (root.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, name)) is { } child) return child;
        Thread.Sleep(100);
    }
    return null;
}

static AutomationElement WaitForWindow(string title) => WaitFor(() =>
{
    var handle = FindWindow(null, title);
    return handle == IntPtr.Zero ? null : AutomationElement.FromHandle(handle);
}, TimeSpan.FromSeconds(10));

static void Expand(AutomationElement element)
    => ((ExpandCollapsePattern)element.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
static void Close(AutomationElement element)
    => ((WindowPattern)element.GetCurrentPattern(WindowPattern.Pattern)).Close();
// A real left-click on a row: press and release on it, rather than writing the selection in through UI
// Automation. The row is measured through Automation because the list-view messages that answer with a
// rectangle take a pointer, and a pointer means nothing in another process.
static void ClickRow(IntPtr list, AutomationElement row)
{
    var area = row.Current.BoundingRectangle;
    var point = new ClientPoint { X = (int)(area.Left + area.Width / 2), Y = (int)(area.Top + area.Height / 2) };
    Check(ScreenToClient(list, ref point), "could not place the click inside the list");
    var packed = new IntPtr((point.Y << 16) | (point.X & 0xffff));
    SendMessage(list, 0x0201, new IntPtr(1), packed);
    SendMessage(list, 0x0202, IntPtr.Zero, packed);
}

static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static IntPtr NativeHandle(AutomationElement element) => new(unchecked((long)(uint)element.Current.NativeWindowHandle));
static string? Option(string[] values, string name)
{
    var index = Array.FindIndex(values, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
}

static NativeFont FontOf(AutomationElement element)
{
    var handle = NativeHandle(element);
    var fontHandle = SendMessage(handle, 0x0031, IntPtr.Zero, IntPtr.Zero);
    var font = new LogFont();
    if (fontHandle == IntPtr.Zero || GetObject(fontHandle, Marshal.SizeOf(font), ref font) == 0)
        throw new InvalidOperationException($"Could not inspect control HFONT (HWND=0x{handle.ToInt64():X}, HFONT=0x{fontHandle.ToInt64():X}).");
    return new NativeFont(font.FaceName, font.Height, font.Quality);
}

[DllImport("user32.dll")]
static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
[DllImport("user32.dll")]
static extern bool EnumChildWindows(IntPtr parent, WindowCallback callback, IntPtr state);
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
[DllImport("user32.dll")]
static extern bool IsWindowVisible(IntPtr window);
[DllImport("user32.dll")]
static extern bool GetWindowRect(IntPtr window, out WindowBounds bounds);
[DllImport("user32.dll")]
static extern bool ScreenToClient(IntPtr window, ref ClientPoint point);
[DllImport("user32.dll")]
static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
[DllImport("user32.dll", CharSet = CharSet.Auto)]
static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
[DllImport("user32.dll")]
static extern int GetDlgCtrlID(IntPtr window);
[DllImport("user32.dll")]
static extern IntPtr GetParent(IntPtr window);
[DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
static extern IntPtr SendText(IntPtr window, uint message, IntPtr word, string text);
[DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
static extern IntPtr ReadNativeText(IntPtr window, uint message, IntPtr word, StringBuilder text);
[DllImport("user32.dll")]
static extern bool PostMessage(IntPtr window, uint message, IntPtr word, IntPtr value);
[DllImport("user32.dll")]
static extern bool ShowWindow(IntPtr window, int command);
[DllImport("user32.dll")]
static extern bool IsWindowEnabled(IntPtr window);
[DllImport("user32.dll")]
static extern bool SetForegroundWindow(IntPtr window);
[DllImport("user32.dll")]
static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")]
static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
[DllImport("user32.dll")]
static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
[DllImport("user32.dll")]
static extern IntPtr GetDC(IntPtr window);
[DllImport("user32.dll")]
static extern int ReleaseDC(IntPtr window, IntPtr dc);
[DllImport("gdi32.dll")]
static extern IntPtr CreateCompatibleDC(IntPtr dc);
[DllImport("gdi32.dll")]
static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
[DllImport("gdi32.dll")]
static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
[DllImport("gdi32.dll")]
static extern bool DeleteObject(IntPtr obj);
[DllImport("gdi32.dll")]
static extern bool DeleteDC(IntPtr dc);
[DllImport("gdi32.dll")]
static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern IntPtr FindWindow(string? className, string windowName);
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string windowName);
[DllImport("user32.dll")]
static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
[DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
static extern int GetObject(IntPtr handle, int size, ref LogFont value);

[StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
struct LogFont
{
    public int Height, Width, Escapement, Orientation, Weight;
    public byte Italic, Underline, StrikeOut, CharSet, OutPrecision, ClipPrecision, Quality, PitchAndFamily;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FaceName;
}
readonly record struct NativeFont(string FaceName, int Height, byte Quality);
delegate bool WindowCallback(IntPtr window, IntPtr state);
[StructLayout(LayoutKind.Sequential)]
struct WindowBounds { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)]
struct ClientPoint { public int X, Y; }
[StructLayout(LayoutKind.Sequential)]
struct MonitorInfo { public int Size; public WindowBounds Monitor, Work; public uint Flags; }
