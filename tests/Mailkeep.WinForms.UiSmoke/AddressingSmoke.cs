using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;
using MimeKit;
using Forms = System.Windows.Forms;

/// <summary>The address book and the replied/forwarded marks, driven in the real client on a throwaway store. No
/// mouse and no keyboard of the desktop's: windows are driven by their own messages, so the run neither needs
/// nor disturbs whatever the person at the machine is doing. Deliveries use transports that send nowhere or
/// a loopback SMTP fixture; real accounts and credentials are never used.</summary>
internal static class AddressingSmoke
{
    /// <summary>An OE Classic address book of the shape the real one has, for the import to read.</summary>
    public static string WriteOeAddressBook(string root)
    {
        var folder = Path.Combine(root, "oe");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "_AddressBook.db");
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE [addressbook] ([id] INTEGER PRIMARY KEY NOT NULL,[type] INTEGER NOT NULL,[identity] INTEGER NOT NULL,[folder] INTEGER NOT NULL,[gender] INTEGER NOT NULL,[photo] BLOB,[name_first] TEXT,[name_middle] TEXT,[name_last] TEXT,[name_nick] TEXT,[name_display] TEXT,[email_personal] TEXT,[email_work] TEXT,[email_other] TEXT,[email_primary] INTEGER,[email_format] INTEGER NOT NULL,[notes] TEXT);
            INSERT INTO addressbook VALUES(1,0,1,0,0,NULL,'Ann',NULL,'Smith',NULL,'Smith, Ann','ann.smith@example.invalid','',NULL,0,0,NULL);
            INSERT INTO addressbook VALUES(2,0,1,0,0,NULL,'Bob',NULL,'Jones',NULL,'','bob.jones@example.invalid','',NULL,0,0,NULL);
            INSERT INTO addressbook VALUES(3,0,1,0,0,NULL,'Carla',NULL,'Smithers',NULL,'','carla@example.invalid','',NULL,0,0,NULL);
            """;
        command.ExecuteNonQuery();
        return path;
    }

    public static void Addressing(AutomationElement window, MailkeepServices services, string oeBook, string? screenshots)
    {
        var before = File.ReadAllBytes(oeBook);
        // Tools > Address Book…: opened through automation on the side, since the call stays open while the window
        // is; everything inside it is then driven by window messages.
        var opening = Open(window, "Tools", "Address Book menu item");
        var book = WaitNative("Address Book");
        Click(book, "Import from OE Classic…");
        var report = WaitNative("Import from OE Classic", "#32770");
        Check(Texts(report).Any(text => text.StartsWith("3 contacts added from OE Classic", StringComparison.Ordinal)), "import did not report three contacts");
        Click(report, "OK");
        WaitUntil(() => !IsWindow(report), "import report did not close");
        Check(services.Database.ListContacts().Count == 3, "import did not store three contacts");
        Check(before.SequenceEqual(File.ReadAllBytes(oeBook)), "the import changed OE Classic's address book");
        Check(Directory.GetFiles(Path.GetDirectoryName(oeBook)!).Length == 1, "the import left files beside OE Classic's address book");
        Click(book, "Import from OE Classic…");
        report = WaitNative("Import from OE Classic", "#32770");
        Check(Texts(report).Any(text => text.StartsWith("0 contacts added", StringComparison.Ordinal)), "a second import added contacts again");
        Click(report, "OK");
        WaitUntil(() => !IsWindow(report), "second import report did not close");
        var contacts = Children(book).Single(child => child.Class.Contains("SysListView32", StringComparison.OrdinalIgnoreCase)).Handle;
        WaitUntil(() => ItemCount(contacts) == 3, "the address book does not list three contacts");
        SetText(Children(book).Single(child => child.Class.Contains(".EDIT.", StringComparison.OrdinalIgnoreCase)).Handle, "jones");
        WaitUntil(() => ItemCount(contacts) == 1, "searching did not narrow the contacts to one");
        Capture(book, screenshots, "address-book");
        Click(book, "Close");
        Check(opening.Wait(10000), "the Address Book did not close");

        // An address written to before, not in the address book: offered from the sent mail.
        services.Database.Upsert(new MessageInput(MailContract.ArchiveSource, "OE Classic", "Sent Items", "sent-zed", null, 0,
            "Sent before", "me@example.invalid", "Me", ["zed.history@example.invalid"], DateTimeOffset.UtcNow.AddDays(-3),
            128, true, 0, "Body", null, null));

        // New Mail. Typing part of a name offers the contacts it could mean; Enter takes the first, quoted
        // because its name holds a comma.
        var newMail = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "New Mail"));
        opening = Task.Run(() => ((InvokePattern)newMail.GetCurrentPattern(InvokePattern.Pattern)).Invoke());
        var compose = WaitNative("New Message");
        var edits = Children(compose).Where(child => child.Class.Contains(".EDIT.", StringComparison.OrdinalIgnoreCase)).OrderBy(child => child.Top).ToArray();
        var (to, cc) = (edits[0].Handle, edits[1].Handle);
        foreach (var character in "smi") PostMessage(to, 0x0102, new IntPtr(character), IntPtr.Zero); // WM_CHAR
        var suggestions = IntPtr.Zero;
        WaitUntil(() => (suggestions = Children(compose).FirstOrDefault(child => child.Class.Contains("LISTBOX", StringComparison.OrdinalIgnoreCase)).Handle) != IntPtr.Zero
            && (int)SendMessage(suggestions, 0x018b, IntPtr.Zero, IntPtr.Zero) == 2, "typing 'smi' did not offer the two Smith contacts"); // LB_GETCOUNT
        Capture(compose, screenshots, "address-autocomplete");
        PostMessage(to, 0x0100, new IntPtr(0x0D), IntPtr.Zero); // Enter takes the first
        WaitUntil(() => Text(to) == "\"Smith, Ann\" <ann.smith@example.invalid>; ", $"Enter did not take the suggestion: '{Text(to)}'");
        WaitUntil(() => !IsWindowVisible(suggestions), "suggestions stayed open after one was taken");

        // The Cc button is Select Recipients: Bob, found by searching, goes into Cc.
        Click(compose, "Cc:");
        var picker = WaitNative("Select Recipients");
        SetText(Children(picker).Single(child => child.Class.Contains(".EDIT.", StringComparison.OrdinalIgnoreCase)).Handle, "bob");
        var pickerList = Children(picker).Single(child => child.Class.Contains("SysListView32", StringComparison.OrdinalIgnoreCase)).Handle;
        WaitUntil(() => ItemCount(pickerList) == 1, "searching the picker did not find Bob alone");
        Click(picker, "Cc: ->");
        var chosen = Children(picker).Where(child => child.Class.Contains("LISTBOX", StringComparison.OrdinalIgnoreCase)).OrderBy(child => child.Top).ToArray();
        WaitUntil(() => (int)SendMessage(chosen[1].Handle, 0x018b, IntPtr.Zero, IntPtr.Zero) == 1, "Bob did not go into the Cc list");
        Check((int)SendMessage(chosen[0].Handle, 0x018b, IntPtr.Zero, IntPtr.Zero) == 1, "the picker did not start from the To line");
        Capture(picker, screenshots, "select-recipients");
        Click(picker, "OK");
        WaitUntil(() => Text(cc) == "Bob Jones <bob.jones@example.invalid>", $"the picker did not fill Cc: '{Text(cc)}'");
        WaitUntil(() => Text(to) == "\"Smith, Ann\" <ann.smith@example.invalid>", $"the picker changed To: '{Text(to)}'");
        Capture(compose, screenshots, "address-picker-result");

        // The written-to history is offered too; Delete takes the highlighted address out of it for good.
        var bcc = edits[2].Handle;
        foreach (var character in "zed") PostMessage(bcc, 0x0102, new IntPtr(character), IntPtr.Zero);
        WaitUntil(() => IsWindowVisible(suggestions) && (int)SendMessage(suggestions, 0x018b, IntPtr.Zero, IntPtr.Zero) == 1,
            "typing 'zed' did not offer the address written to before");
        Capture(compose, screenshots, "address-history");
        PostMessage(bcc, 0x0100, new IntPtr(0x2E), IntPtr.Zero); // Delete
        WaitUntil(() => !IsWindowVisible(suggestions), "Delete did not take the address out of the suggestions");
        Check(Text(bcc) == "zed", $"Delete changed the typed text: '{Text(bcc)}'");
        Check(services.Database.ListAddressHistory().All(use => use.Email != "zed.history@example.invalid"), "Delete did not remove the address from the history");
        SetText(bcc, "");

        // Closed without keeping it: nothing is saved, and certainly nothing sent.
        PostMessage(compose, 0x0010, IntPtr.Zero, IntPtr.Zero);
        var save = WaitNative("New Message", "#32770");
        Click(save, "No");
        WaitUntil(() => !IsWindow(compose), "the message window did not close after No");
        Check(opening.Wait(10000), "New Mail did not return after its window closed");
        Check(services.Database.ListOutgoing().Count == 0, "closing without saving left a draft");
        Console.WriteLine("PASS addressing import=3 reimport=0 oe-book=unchanged search=narrows autocomplete=quoted-name cc-button=picker picker=cc history=offered delete=removed nothing-saved");
    }

    /// <summary>A transport that takes the message and sends it nowhere.</summary>
    private sealed class NowhereTransport : IOutgoingTransport
    {
        public Task SendAsync(SendingIdentity identity, MimeMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task VerifyAsync(SendingIdentity identity, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FailingTransport : IOutgoingTransport
    {
        public Task SendAsync(SendingIdentity identity, MimeMessage message, CancellationToken cancellationToken = default)
            => throw new IOException("delivery refused for the test");
        public Task VerifyAsync(SendingIdentity identity, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>Reply and forward through the client's own compose path, in process: a reply abandoned leaves
    /// nothing, mail waiting in the Outbox or failing to deliver leaves no mark, and once delivered the rows say
    /// Replied and Forwarded — and still do when the client is started again on the same store.</summary>
    public static void Marks(string app, string root, MailkeepServices services, string? screenshots, string? liveMarks = null, string? metadata = null)
    {
        services.Database.SetSetting("send.immediately", "false");
        services.Database.SetSetting("sync.auto", "off");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Run(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(120))) throw new TimeoutException("Marks smoke did not finish.");
        if (failure is not null) throw new InvalidOperationException("Marks smoke failed.", failure);

        void Run()
        {
            Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
            // The client runs under Application.Run, whose context outlives every dialog. Without one, a modal
            // loop installs a context for itself and takes it away on closing, and the compose path's awaits then
            // resume off this thread — a fault of the harness, not of the client.
            Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            var assembly = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(app))!, "Mailkeep.WinForms.dll"));
            var mainType = assembly.GetType("Mailkeep.WinForms.MainForm", true)!;
            var composeType = assembly.GetType("Mailkeep.WinForms.ComposeForm", true)!;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var inbox = services.Database.ListMessages().Where(message => message.Account == "local-fixture").OrderByDescending(message => message.Date).ToArray();
            Check(inbox.Length == 2, "the marks fixture needs two Inbox messages");
            var (answered, forwarded) = (inbox[0], inbox[1]);

            var main = Start();
            // Abandoned: the composer is closed with "don't save", and nothing is written.
            Compose(main, "ReplyToAsync", [services.Database.GetMessage(forwarded.Id)!, false], compose =>
            {
                composeType.GetField("_discard", flags)!.SetValue(compose, true);
                compose.Close();
            });
            Check(services.Database.ListOutgoing().Count == 0, "an abandoned reply was kept");
            // Sent to the Outbox: written and remembered against its message, but not marked yet.
            Compose(main, "ReplyToAsync", [services.Database.GetMessage(answered.Id)!, false], Finish);
            Compose(main, "ForwardContentAsync", [services.Database.GetMessage(forwarded.Id)!], compose =>
            {
                ((Forms.TextBox)composeType.GetField("_to", flags)!.GetValue(compose)!).Text = "someone@example.invalid";
                Finish(compose);
            });
            var queued = services.Database.ListOutgoing(OutgoingStates.Outbox).Select(message => message.Id).ToArray();
            Check(queued.Length == 2, $"expected the reply and the forward in the Outbox, found {queued.Length}");
            // Finish compose's queued refresh before the independent sender changes the store.
            Await((Task)mainType.GetMethod("RefreshAfterComposeAsync", flags)!.Invoke(main, [])!);
            Pump(30);
            Check(services.Database.MessageMarks([answered.Id, forwarded.Id]).Count == 0, "mail waiting in the Outbox already marked its message");
            var failing = new MailSendService(services.Identities, services.Database, services.Blobs, new FailingTransport());
            Check(Task.Run(() => failing.SendQueuedAsync(queued)).GetAwaiter().GetResult().All(result => !result.Ok), "the failing delivery succeeded");
            Check(services.Database.MessageMarks([answered.Id, forwarded.Id]).Count == 0, "a failed delivery marked its message");
            var sender = new MailSendService(services.Identities, services.Database, services.Blobs, new NowhereTransport());
            Check(Task.Run(() => sender.SendQueuedAsync(queued)).GetAwaiter().GetResult().All(result => result.Ok), "the nowhere delivery failed");

            // A separate sender committed the delivery while this folder stayed open.
            // Returning to the app must update arrows without reloading its rows or preview.
            var originalRows = Rows(main);
            var listBefore = (Forms.ListView)mainType.GetField("_messages", flags)!.GetValue(main)!;
            var selectedBefore = listBefore.SelectedIndices.Cast<int>().ToArray();
            var topBefore = listBefore.TopItem?.Index;
            var contentBefore = mainType.GetField("_currentContent", flags)!.GetValue(main);
            mainType.GetMethod("OnActivated", flags)!.Invoke(main, [EventArgs.Empty]);
            var refreshClock = Stopwatch.StartNew();
            while (Tip(main, answered.Id) != "Replied" || Tip(main, forwarded.Id) != "Forwarded")
            {
                if (refreshClock.Elapsed > TimeSpan.FromSeconds(3))
                    throw new InvalidOperationException("external delivery left stale replied/forwarded icons after activation");
                Pump(1);
            }
            Check(ReferenceEquals(Rows(main), originalRows), "mark refresh reloaded mailbox rows");
            Check(selectedBefore.SequenceEqual(listBefore.SelectedIndices.Cast<int>()) && listBefore.TopItem?.Index == topBefore,
                "mark refresh moved selection or scroll");
            Check(ReferenceEquals(mainType.GetField("_currentContent", flags)!.GetValue(main), contentBefore),
                "mark refresh reloaded the reading pane");
            Check(Tip(main, answered.Id) == "Replied", $"the replied row says '{Tip(main, answered.Id)}'");
            Check(Tip(main, forwarded.Id) == "Forwarded", $"the forwarded row says '{Tip(main, forwarded.Id)}'");
            Check(Row(main, answered.Id).ImageIndex >= 2 && Row(main, forwarded.Id).ImageIndex >= 2
                && Row(main, answered.Id).ImageIndex / 2 != Row(main, forwarded.Id).ImageIndex / 2, "the marked rows do not carry their own icons");
            CheckArrow(main, answered.Id, System.Drawing.Color.FromArgb(0, 70, 200));
            CheckArrow(main, forwarded.Id, System.Drawing.Color.FromArgb(0, 70, 200));
            Capture(main.Handle, screenshots, "replied-forwarded-marks");
            // Stay in this window while a second process adds another delivered mark.
            main.Activate();
            Pump(30);
            var timer = (Forms.Timer)mainType.GetField("_messageMarksRefresh", flags)!.GetValue(main)!;
            timer.Interval = 100;
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO message_marks(message_id,kind,marked_at) VALUES($id,'forwarded',$at)";
                command.Parameters.AddWithValue("$id", answered.Id);
                command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                command.ExecuteNonQuery();
            }
            refreshClock.Restart();
            while (Tip(main, answered.Id) != "Replied and forwarded")
            {
                if (refreshClock.Elapsed > TimeSpan.FromSeconds(3))
                    throw new InvalidOperationException("active-window timer left an external delivery mark stale");
                Pump(1);
            }
            timer.Interval = 5000;
            Check(ReferenceEquals(Rows(main), originalRows), "periodic mark refresh reloaded mailbox rows");
            // A read failure must retain the arrows rather than replace them with no marks.
            var savedMarks = mainType.GetField("_marks", flags)!.GetValue(main);
            var pending = Path.Combine(root, "bulk-migration", "pending");
            Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
            File.WriteAllText(pending, "fixture-only unavailable-store check");
            try
            {
                Await((Task)mainType.GetMethod("RefreshMessageMarksAsync", flags)!.Invoke(main, [])!);
                Check(ReferenceEquals(savedMarks, mainType.GetField("_marks", flags)!.GetValue(main)),
                    "a failed mark read erased the existing icons");
            }
            finally { File.Delete(pending); }
            CheckArrow(main, answered.Id, System.Drawing.Color.FromArgb(0, 70, 200));
            Capture(main.Handle, screenshots, "external-marks-refreshed");
            Console.WriteLine("PASS external-marks activation=updated active-timer=updated failed-read=kept rows=retained selection=retained scroll=retained preview=retained");
            // Exercise the normal immediate-send callback over actual loopback SMTP, then file the read rows.
            using var smtp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            smtp.Start();
            var deliveries = 0;
            var server = Task.Run(async () =>
            {
                for (var delivery = 0; delivery < (metadata is null ? 2 : 4); delivery++)
                {
                    using var client = await smtp.AcceptTcpClientAsync();
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
                    await writer.WriteLineAsync("220 localhost fixture");
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        if (line.StartsWith("EHLO")) await writer.WriteAsync("250-localhost\r\n250 AUTH PLAIN\r\n");
                        else if (line.StartsWith("AUTH")) await writer.WriteLineAsync("235 authenticated");
                        else if (line == "DATA")
                        {
                            await writer.WriteLineAsync("354 continue");
                            while (await reader.ReadLineAsync() is { } part && part != ".") { }
                            Interlocked.Increment(ref deliveries);
                            await writer.WriteLineAsync("250 accepted");
                        }
                        else if (line == "QUIT") { await writer.WriteLineAsync("221 bye"); break; }
                        else await writer.WriteLineAsync("250 ok");
                    }
                }
            });
            services.Identities.Save(services.Identities.Load().Select(identity => identity.IsReady ? identity with
            {
                Provider = "custom", Smtp = new OutgoingServerSettings("127.0.0.1",
                    ((System.Net.IPEndPoint)smtp.LocalEndpoint).Port, MailSecurityModes.None)
            } : identity).ToArray());
            services.Database.SetSetting("send.immediately", "true");
            Compose(main, "ReplyToAsync", [services.Database.GetMessage(forwarded.Id)!, false], Finish);
            Compose(main, "ForwardContentAsync", [services.Database.GetMessage(answered.Id)!], compose =>
            {
                ((Forms.TextBox)composeType.GetField("_to", flags)!.GetValue(compose)!).Text = "someone@example.invalid";
                Finish(compose);
            });
            var deliveryWait = Stopwatch.StartNew();
            while (deliveries < 2 && deliveryWait.Elapsed < TimeSpan.FromSeconds(5)) Pump(1);
            Check(deliveries == 2, "immediate reply/forward did not deliver exactly twice over loopback SMTP");
            services.Database.SetReadStates([answered.Id, forwarded.Id], true);
            var filed = services.Database.CreateLocalFolder("Filed marks", null);
            Await((Task)mainType.GetMethod("FileMessagesAsync", flags)!.Invoke(main,
                [new[] { services.Database.GetMessage(answered.Id)!.Summary, services.Database.GetMessage(forwarded.Id)!.Summary }, filed.Key, filed.Name])!);
            SelectFolder(main, filed.Key, [answered.Id, forwarded.Id]);
            foreach (var id in new[] { answered.Id, forwarded.Id })
            {
                Check(Tip(main, id) == "Replied and forwarded", "filing lost the combined marks");
                CheckArrow(main, id, System.Drawing.Color.FromArgb(0, 70, 200));
                CheckArrow(main, id, System.Drawing.Color.FromArgb(0, 70, 200));
            }
            Capture(main.Handle, screenshots, "replied-forwarded-filed-read");
            main.Close();
            main.Dispose();
            Pump(30);
            var again = Start();
            SelectFolder(again, filed.Key, [answered.Id, forwarded.Id]);
            Check(Tip(again, answered.Id) == "Replied and forwarded" && Tip(again, forwarded.Id) == "Replied and forwarded", "the marks did not survive filing and restart");
            CheckArrow(again, answered.Id, System.Drawing.Color.FromArgb(0, 70, 200));
            CheckArrow(again, forwarded.Id, System.Drawing.Color.FromArgb(0, 70, 200));
            if (liveMarks is not null)
            {
                // Reproduce the live marked row IDs/locations/read states, without bodies or credentials.
                using var state = System.Text.Json.JsonDocument.Parse(File.ReadAllText(liveMarks));
                var accounts = services.Accounts.Load().ToList();
                var template = accounts.Single(account => account.Name == "local-fixture");
                var ids = new List<long>();
                using var connection = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False");
                connection.Open();
                foreach (var row in state.RootElement.GetProperty("messages").EnumerateArray())
                {
                    var account = row.GetProperty("account").GetString()!;
                    if (!accounts.Any(value => value.Name == account)) accounts.Add(template with
                    { Name = account, Address = $"copied-state-{accounts.Count}@example.invalid" });
                    var id = row.GetProperty("id").GetInt64();
                    ids.Add(id);
                    using var command = connection.CreateCommand();
                    command.CommandText = "INSERT INTO messages(id,source,account,folder,source_key,subject,sender,is_read,body_text) VALUES($id,$source,$account,$folder,$key,$subject,'sender@example.invalid',$read,'Fixture body'); INSERT INTO message_marks(message_id,kind,marked_at) VALUES($id,$kind,$at)";
                    command.Parameters.AddWithValue("$id", id);
                    command.Parameters.AddWithValue("$source", row.GetProperty("source").GetString()!);
                    command.Parameters.AddWithValue("$account", account);
                    command.Parameters.AddWithValue("$folder", row.GetProperty("folder").GetString()!);
                    command.Parameters.AddWithValue("$key", row.GetProperty("source_key").GetString()!);
                    command.Parameters.AddWithValue("$subject", "Live-state fixture " + id);
                    command.Parameters.AddWithValue("$read", row.GetProperty("is_read").GetInt32());
                    command.Parameters.AddWithValue("$kind", row.GetProperty("kind").GetString()!);
                    command.Parameters.AddWithValue("$at", row.GetProperty("marked_at").GetString()!);
                    command.ExecuteNonQuery();
                }
                services.Accounts.Save(accounts);
                again.Close();
                again.Dispose();
                Pump(30);
                services.Database.SetSetting("window.layout", state.RootElement.GetProperty("layout").GetString()!);
                again = Start();
                SelectFolder(again, "local-all", ids.ToArray());
                foreach (var row in state.RootElement.GetProperty("messages").EnumerateArray())
                    CheckArrow(again, row.GetProperty("id").GetInt64(), row.GetProperty("kind").GetString() == MessageMarkKinds.Replied
                        ? System.Drawing.Color.FromArgb(0, 70, 200) : System.Drawing.Color.FromArgb(0, 70, 200));
                Capture(again.Handle, screenshots, "live-marked-rows");
                Console.WriteLine($"PASS live-marked-rows copied={ids.Count} read=preserved IDs=preserved arrows=rendered");
            }
            if (metadata is not null)
            {
                again.Close();
                again.Dispose();
                Pump(30);
                File.Copy(metadata, services.Paths.DatabasePath, true);
                services.Database.Initialize();
                services.Database.SetSetting("send.immediately", "true");
                long[] ids;
                using (var connection = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False"))
                {
                    connection.Open();
                    using var marked = connection.CreateCommand();
                    marked.CommandText = "SELECT DISTINCT message_id FROM message_marks ORDER BY message_id";
                    using var reader = marked.ExecuteReader();
                    var markedIds = new List<long>();
                    while (reader.Read()) markedIds.Add(reader.GetInt64(0));
                    ids = markedIds.ToArray();
                }
                var template = services.Accounts.Load().Single(account => account.Name == "local-fixture");
                var names = services.Database.ListHeads(MailContract.LiveSource).Select(head => head.Account).Distinct().ToArray();
                services.Accounts.Save(names.Select((name, index) => template with
                { Name = name, Address = $"metadata-copy-{index}@example.invalid" }).ToArray());
                again = Start(ids);
                Console.WriteLine($"CHECK full metadata copy messages={services.Database.Stats().Values.Sum()} marks={ids.Length}");
                foreach (var folder in ids.Select(id => services.Database.GetMessage(id)!.Summary.Folder).Distinct())
                {
                    var expected = ids.Where(id => services.Database.GetMessage(id)!.Summary.Folder == folder).ToArray();
                    var tree = (Forms.TreeView)mainType.GetField("_localTree", flags)!.GetValue(again)!;
                    var node = Walk(tree.Nodes).FirstOrDefault(node => node.Tag is LocalFolder local && local.Key == folder
                        || node.Tag?.GetType().GetProperty("LocalKey")?.GetValue(node.Tag) as string == folder);
                    Check(node is not null, "copied live folder is absent from the real tree: " + folder);
                    tree.SelectedNode = node;
                    var archiveFolder = node!.Tag?.GetType().GetProperty("ArchiveFolder")?.GetValue(node.Tag) as string;
                    var clock = Stopwatch.StartNew();
                    while (!expected.All(id => Rows(again).OfType<MessageSummary>().Any(message => message.Id == id))
                        || Rows(again).OfType<MessageSummary>().Any(message => message.Folder !=
                            (message.Source == MailContract.ArchiveSource ? archiveFolder : folder)))
                    {
                        if (clock.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("marked rows absent from copied live folder " + folder);
                        Pump(1);
                    }
                    foreach (var id in expected)
                    {
                        var marks = services.Database.MessageMarks([id])[id];
                        if (marks.Replied) CheckArrow(again, id, System.Drawing.Color.FromArgb(0, 70, 200));
                        if (marks.Forwarded) CheckArrow(again, id, System.Drawing.Color.FromArgb(0, 70, 200));
                    }
                    Capture(again.Handle, screenshots, folder == LocalFolderKinds.Deleted ? "full-copy-deleted-marks" : "full-copy-filed-marks");
                }
                var original = services.Database.GetMessage(ids[^1])!;
                Compose(again, "ReplyToAsync", [original, false], Finish);
                Compose(again, "ForwardContentAsync", [original], compose =>
                {
                    ((Forms.TextBox)composeType.GetField("_to", flags)!.GetValue(compose)!).Text = "someone@example.invalid";
                    Finish(compose);
                });
                Check(server.Wait(TimeSpan.FromSeconds(5)) && deliveries == 4, "copied live reply/forward did not deliver over loopback SMTP");
                var target = services.Database.CreateLocalFolder("Full copy filed", null);
                Await((Task)mainType.GetMethod("FileMessagesAsync", flags)!.Invoke(again,
                    [new[] { services.Database.GetMessage(original.Summary.Id)!.Summary }, target.Key, target.Name])!);
                SelectFolder(again, target.Key, [original.Summary.Id]);
                CheckArrow(again, original.Summary.Id, System.Drawing.Color.FromArgb(0, 70, 200));
                CheckArrow(again, original.Summary.Id, System.Drawing.Color.FromArgb(0, 70, 200));
                again.Close();
                again.Dispose();
                Pump(30);
                again = Start([original.Summary.Id]);
                SelectFolder(again, target.Key, [original.Summary.Id]);
                CheckArrow(again, original.Summary.Id, System.Drawing.Color.FromArgb(0, 70, 200));
                CheckArrow(again, original.Summary.Id, System.Drawing.Color.FromArgb(0, 70, 200));
                Capture(again.Handle, screenshots, "full-copy-reply-forward-file-restart");
                Console.WriteLine("PASS full-metadata-copy existing-marks=all-rendered tree=real reply+forward=loopback filed=kept reload=kept restart=kept");
            }
            again.Close();
            again.Dispose();
            Pump(30);
            Console.WriteLine("PASS marks abandoned=nothing-saved outbox=unmarked failed-send=unmarked delivered=replied+forwarded immediate=loopback-twice filed=read-icons reload=kept restart=kept");

            Forms.Form Start(long[]? required = null)
            {
                var form = (Forms.Form)Activator.CreateInstance(mainType, [root])!;
                form.Show();
                var clock = Stopwatch.StartNew();
                while (!(bool)mainType.GetField("_ready", flags)!.GetValue(form)!)
                {
                    if (clock.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("client did not start");
                    Pump(1);
                }
                var wanted = required ?? [answered.Id, forwarded.Id];
                var folder = services.Database.GetMessage(wanted[0])!.Summary.Folder;
                SelectFolder(form, folder, wanted);
                return form;
            }

            IEnumerable<Forms.TreeNode> Walk(Forms.TreeNodeCollection nodes)
            {
                foreach (Forms.TreeNode node in nodes)
                {
                    yield return node;
                    foreach (var child in Walk(node.Nodes)) yield return child;
                }
            }

            void Refresh(Forms.Form form)
            {
                // Emptied first, so the wait below is for the rows this load brings, not the ones already shown.
                mainType.GetField("_rows", flags)!.SetValue(form, Array.Empty<object>());
                mainType.GetMethod("LoadMessages", flags)!.Invoke(form, []);
                var clock = Stopwatch.StartNew();
                while (Rows(form).OfType<MessageSummary>().Count(message => message.Account == "local-fixture") < inbox.Length)
                {
                    if (clock.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Inbox did not load");
                    Pump(1);
                }
            }

            IReadOnlyList<object> Rows(Forms.Form form) => (IReadOnlyList<object>)mainType.GetField("_rows", flags)!.GetValue(form)!;

            Forms.ListViewItem Row(Forms.Form form, long id)
            {
                var index = Rows(form).Select((row, position) => (row, position))
                    .Single(pair => pair.row is MessageSummary message && message.Id == id).position;
                return (Forms.ListViewItem)mainType.GetMethod("Row", flags)!.Invoke(form, [index])!;
            }

            string Tip(Forms.Form form, long id) => Row(form, id).ToolTipText;

            void Await(Task task)
            {
                var clock = Stopwatch.StartNew();
                while (!task.IsCompleted)
                {
                    if (clock.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("fixture action did not finish");
                    Pump(1);
                }
                task.GetAwaiter().GetResult();
            }

            void SelectFolder(Forms.Form form, string folder, long[] ids)
            {
                mainType.GetField("_selectedAccount", flags)!.SetValue(form, null);
                mainType.GetField("_selectedArchive", flags)!.SetValue(form, null);
                mainType.GetField("_selectedLocalFolder", flags)!.SetValue(form, folder);
                mainType.GetMethod("LoadMessages", flags)!.Invoke(form, []);
                var clock = Stopwatch.StartNew();
                while (!ids.All(id => Rows(form).OfType<MessageSummary>().Any(message => message.Id == id && (folder == "local-all" || message.Folder == folder))))
                {
                    if (clock.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("filed fixture rows did not load");
                    Pump(1);
                }
                Pump(20);
            }

            void CheckArrow(Forms.Form form, long id, System.Drawing.Color color)
            {
                var list = (Forms.ListView)mainType.GetField("_messages", flags)!.GetValue(form)!;
                var index = Rows(form).Select((row, position) => (row, position))
                    .Single(pair => pair.row is MessageSummary message && message.Id == id).position;
                list.EnsureVisible(index);
                list.Refresh();
                Pump(10);
                using var image = new System.Drawing.Bitmap(list.Width, list.Height);
                using (var graphics = System.Drawing.Graphics.FromImage(image))
                {
                    var dc = graphics.GetHdc();
                    try { Check(PrintWindow(list.Handle, dc, 2), "message list capture failed"); }
                    finally { graphics.ReleaseHdc(dc); }
                }
                var bounds = list.GetItemRect(index);
                var pixels = 0;
                for (var y = Math.Max(0, bounds.Top); y < Math.Min(bounds.Bottom, image.Height); y++)
                    for (var x = Math.Max(0, bounds.Left); x < Math.Min(bounds.Left + 24, image.Width); x++)
                        if (image.GetPixel(x, y).ToArgb() == color.ToArgb()) pixels++;
                Check(pixels >= 8, $"the {Tip(form, id)} arrow is not visible on the rendered row ({pixels} pixels)");
            }

            // Compose is modeless: wait for both the opening command and the actual window to finish.
            void Compose(Forms.Form form, string command, object[] arguments, Action<Forms.Form> finish)
            {
                using var timer = new Forms.Timer { Interval = 100 };
                Forms.Form? opened = null;
                timer.Tick += (_, _) =>
                {
                    var open = Forms.Application.OpenForms.Cast<Forms.Form>()
                        .FirstOrDefault(candidate => composeType.IsInstanceOfType(candidate) && candidate.Visible);
                    if (open is null) return;
                    timer.Stop();
                    opened = open;
                    finish(open);
                };
                timer.Start();
                var task = (Task)mainType.GetMethod(command, flags)!.Invoke(form, arguments)!;
                var clock = Stopwatch.StartNew();
                while (!task.IsCompleted || opened is null || !opened.IsDisposed)
                {
                    if (clock.Elapsed > TimeSpan.FromSeconds(30))
                        throw new TimeoutException(command + " did not finish; open windows: " + string.Join(" | ",
                            Forms.Application.OpenForms.Cast<Forms.Form>().Select(open => $"{open.GetType().Name}:{open.Text}:{open.Visible}"))
                            + "; errors.log: " + (File.Exists(Path.Combine(root, "errors.log")) ? File.ReadAllText(Path.Combine(root, "errors.log")) : "none"));
                    Pump(1);
                }
                task.GetAwaiter().GetResult();
            }

            // The composer's own Send: validates, reads the body, and closes as the button does.
            void Finish(Forms.Form compose) => _ = (Task)composeType.GetMethod("FinishAsync", flags)!.Invoke(compose, [true])!;
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static void Pump(int passes)
    {
        for (var pass = 0; pass < passes; pass++) { Forms.Application.DoEvents(); Thread.Sleep(10); }
    }

    /// <summary>Opens a menu entry through automation, on the side: the call does not return while the window
    /// it opens is up, and the caller drives that window by its messages meanwhile.</summary>
    private static Task Open(AutomationElement window, string menu, string item)
    {
        var top = WaitFor(() => window.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
            new PropertyCondition(AutomationElement.NameProperty, menu))), "menu missing: " + menu);
        ((ExpandCollapsePattern)top.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        var entry = WaitFor(() => AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
            new PropertyCondition(AutomationElement.NameProperty, item))), "menu item missing: " + item);
        return Task.Run(() => ((InvokePattern)entry.GetCurrentPattern(InvokePattern.Pattern)).Invoke());
    }

    private static IntPtr WaitNative(string title, string? className = null)
    {
        var handle = IntPtr.Zero;
        WaitUntil(() => (handle = FindWindow(className, title)) != IntPtr.Zero && IsWindowVisible(handle), "window did not open: " + title);
        return handle;
    }

    private static List<(IntPtr Handle, string Class, string Title, int Top)> Children(IntPtr parent)
    {
        var found = new List<(IntPtr, string, string, int)>();
        EnumChildWindows(parent, (child, _) =>
        {
            if (!IsWindowVisible(child)) return true;
            var name = new StringBuilder(256);
            GetClassName(child, name, name.Capacity);
            GetWindowRect(child, out var bounds);
            found.Add((child, name.ToString(), Text(child), bounds.Top));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string Text(IntPtr window)
    {
        var text = new StringBuilder(2048);
        SendText(window, 0x000D, new IntPtr(text.Capacity), text); // WM_GETTEXT
        return text.ToString();
    }

    private static void SetText(IntPtr window, string text) => SendString(window, 0x000C, IntPtr.Zero, text); // WM_SETTEXT

    private static IEnumerable<string> Texts(IntPtr window) => Children(window).Select(child => child.Title.Replace("\r\n", " "));

    private static int ItemCount(IntPtr list) => (int)SendMessage(list, 0x1004, IntPtr.Zero, IntPtr.Zero); // LVM_GETITEMCOUNT

    private static void Click(IntPtr window, string caption)
    {
        var button = IntPtr.Zero;
        WaitUntil(() => (button = Children(window).FirstOrDefault(child => child.Class.Contains("BUTTON", StringComparison.OrdinalIgnoreCase)
            && child.Title.Replace("&", "") == caption).Handle) != IntPtr.Zero, "button not found: " + caption);
        var dialog = new StringBuilder(64);
        GetClassName(window, dialog, dialog.Capacity);
        // A message box ignores BM_CLICK while it is not the active window, and activating it would take the
        // desktop's focus; told of the click as WM_COMMAND it acts the same either way.
        if (dialog.ToString() == "#32770")
            PostMessage(window, 0x0111, new IntPtr(GetDlgCtrlID(button) & 0xffff), button); // WM_COMMAND, BN_CLICKED
        else PostMessage(button, 0x00F5, IntPtr.Zero, IntPtr.Zero); // BM_CLICK
    }

    /// <summary>The window as it draws itself, through PrintWindow, so it is captured whatever is in front of it.</summary>
    internal static void Capture(IntPtr window, string? folder, string name)
    {
        if (folder is null) return;
        Directory.CreateDirectory(folder);
        GetWindowRect(window, out var bounds);
        using var image = new System.Drawing.Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        using (var graphics = System.Drawing.Graphics.FromImage(image))
        {
            var dc = graphics.GetHdc();
            try { PrintWindow(window, dc, 2); } // PW_RENDERFULLCONTENT
            finally { graphics.ReleaseHdc(dc); }
        }
        image.Save(Path.Combine(folder, name + ".png"));
        Console.WriteLine("Captured " + name);
    }

    private static T WaitFor<T>(Func<T?> probe, string failure) where T : class
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            try { if (probe() is { } value) return value; }
            catch (ElementNotAvailableException) { }
            Thread.Sleep(100);
        }
        throw new TimeoutException(failure);
    }

    private static void WaitUntil(Func<bool> condition, string failure)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(failure);
            Thread.Sleep(100);
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    [StructLayout(LayoutKind.Sequential)] private struct Bounds { public int Left, Top, Right, Bottom; }
    private delegate bool WindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, WindowCallback callback, IntPtr state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Bounds bounds);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr word, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] private static extern IntPtr SendText(IntPtr window, uint message, IntPtr word, StringBuilder text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] private static extern IntPtr SendString(IntPtr window, uint message, IntPtr word, string text);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr word, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string windowName);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
}
