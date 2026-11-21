using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

/// <summary>OE shortcuts on a disposable store: real compose actions, HTML accelerator events, list
/// navigation and reader ownership. SMTP is loopback only; no live account or desktop mail is touched.</summary>
internal static class ShortcutSmoke
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Run(string app, string root, MailkeepServices services, bool composeOnly = false, string? screenshots = null,
        bool sendOnly = false, bool recoverSend = false)
    {
        using var smtp = new TcpListener(IPAddress.Loopback, 0);
        smtp.Start();
        var received = 0;
        var awaitingAcceptance = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptDelivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = composeOnly ? Task.CompletedTask : Task.Run(async () =>
        {
            using var client = await smtp.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };
            await writer.WriteLineAsync("220 localhost fixture");
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("EHLO")) await writer.WriteAsync("250-localhost\r\n250 AUTH PLAIN\r\n");
                else if (line.StartsWith("AUTH")) await writer.WriteLineAsync("235 authenticated");
                else if (line == "DATA")
                {
                    await writer.WriteLineAsync("354 continue");
                    var data = new List<string>();
                    while (await reader.ReadLineAsync() is { } part && part != ".") data.Add(part);
                    Check(data.Any(part => part.Contains("shortcut immediate")), "SMTP received the wrong fixture");
                    if (sendOnly)
                    {
                        awaitingAcceptance.SetResult();
                        await acceptDelivery.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    }
                    Interlocked.Increment(ref received);
                    await writer.WriteLineAsync("250 accepted");
                }
                else if (line == "QUIT") { await writer.WriteLineAsync("221 bye"); break; }
                else await writer.WriteLineAsync("250 ok");
            }
        });
        var identity = services.Identities.Load().Single(value => value.IsReady) with
        {
            Provider = "custom", Smtp = new OutgoingServerSettings("127.0.0.1", ((IPEndPoint)smtp.LocalEndpoint).Port, MailSecurityModes.None)
        };
        services.Identities.Save([identity]);
        // Never let an automatic fetch try the fixture account hosts.
        services.Accounts.Save(services.Accounts.Load().Where(account => account.Name == "local-fixture").ToArray());
        services.Database.SetSetting("sync.auto", "off");
        services.Database.CreateLocalFolder("Filed fixture", null);
        OutgoingMessage? receipt = null;
        if (recoverSend)
        {
            var draft = services.Send.SaveDraft(identity.Id, ["recipient@example.invalid"], "shortcut immediate recovery", "Receipt fixture");
            services.Send.QueueAsync(draft.Id).GetAwaiter().GetResult();
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False");
            connection.Open();
            using var fault = connection.CreateCommand();
            fault.CommandText = """
                CREATE TRIGGER fail_sent BEFORE UPDATE OF status ON outgoing_messages WHEN NEW.status='sent'
                BEGIN SELECT RAISE(ABORT,'fixture receipt failure'); END;
                """;
            fault.ExecuteNonQuery();
            var accepted = services.Send.SendQueuedAsync([draft.Id]).GetAwaiter().GetResult().Single();
            receipt = services.Database.GetOutgoing(draft.Id)!;
            Check(accepted.Ok && accepted.Error?.Contains("fixture receipt failure") == true
                && receipt.Status == OutgoingStates.SentUnrecorded && receipt.SentAt.HasValue, "missing durable SMTP receipt");
            fault.CommandText = "DROP TRIGGER fail_sent";
            fault.ExecuteNonQuery();
        }
        if (sendOnly)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<100) INSERT INTO messages(source,account,folder,source_key,subject,sender,is_read,body_text,date_utc) SELECT $source,'local-fixture',$folder,'position-'||i,'Position fixture '||i,'fixture@example.invalid',1,'Position body',datetime('2026-01-01','+'||i||' minutes') FROM n";
            command.Parameters.AddWithValue("$source", MailContract.LiveSource);
            command.Parameters.AddWithValue("$folder", LocalFolderKinds.Inbox);
            command.ExecuteNonQuery();
        }
        Exception? failure = null;
        var thread = new Thread(() => { try { CheckUi(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(90))) throw new TimeoutException("shortcut regression timed out");
        if (failure is not null) throw new InvalidOperationException("shortcut regression failed", failure);
        if (composeOnly) { Console.WriteLine("PASS compose-input typing,paste,tools plain,html no-delivery"); return; }
        Check(server.Wait(TimeSpan.FromSeconds(5)), "SMTP did not finish");
        Check(received == 1, "Alt+S duplicated delivery");
        if (recoverSend)
        {
            Check(!smtp.Pending(), "recovery opened another SMTP connection");
            Console.WriteLine("PASS send-recovery SMTP deliveries=1 receipt survived reopening Sent copies=1");
            return;
        }
        if (sendOnly) { Console.WriteLine("PASS compose-send closed-before-SMTP-acceptance queued-durable ui-responsive sent-after-acceptance queued-setting=no-delivery"); return; }
        Console.WriteLine("PASS shortcuts plain=alt-s,ctrl-enter,ctrl-s html=alt-s,ctrl-enter,ctrl-s main=navigation,read,unread,search reader=own-message smtp=loopback-once queued=no-delivery");

        void CheckUi()
        {
            Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
            Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            var appDirectory = Path.GetDirectoryName(Path.GetFullPath(app))!;
            var loader = Path.Combine(appDirectory, "WebView2Loader.dll");
            if (!File.Exists(loader)) loader = Path.Combine(appDirectory, "runtimes", "win-x64", "native", "WebView2Loader.dll");
            System.Runtime.InteropServices.NativeLibrary.Load(loader);
            var assembly = Assembly.LoadFrom(Path.Combine(appDirectory, "Mailkeep.WinForms.dll"));
            var composeType = assembly.GetType("Mailkeep.WinForms.ComposeForm", true)!;
            var seedType = assembly.GetType("Mailkeep.WinForms.ComposeSeed", true)!;
            var mainType = assembly.GetType("Mailkeep.WinForms.MainForm", true)!;
            var conflicts = mainType.GetMethod("SearchShortcutConflicts", BindingFlags.Static | BindingFlags.NonPublic)!;
            foreach (var chord in new[] { "Alt+S", "Ctrl+F", "Ctrl+U", "Ctrl+S", "Ctrl+K", "Alt+1" })
                Check((bool)conflicts.Invoke(null, [chord])!, "global search can steal " + chord);
            Check(!(bool)conflicts.Invoke(null, ["Ctrl+Shift+F"])!, "Find shortcut incorrectly reserved");
            object Seed(string subject) => Activator.CreateInstance(seedType,
                ["New Message", identity.Id, "recipient@example.invalid", "", "", subject, "Shortcut body", null, null, null, null, null])!;
            object? Get(object target, string name) => target.GetType().GetField(name, Flags)!.GetValue(target);
            using var main = (Forms.Form)Activator.CreateInstance(mainType, [root])!;
            main.Show();
            Wait(() => (bool)Get(main, "_ready")!, "main not ready");
            if (receipt is not null)
            {
                var recover = (Task)mainType.GetMethod("SendOutboxAsync", Flags)!.Invoke(main, [true])!;
                Wait(() => recover.IsCompleted, "Send Outbox did not recover the receipt");
                recover.GetAwaiter().GetResult();
                var result = services.Database.GetOutgoing(receipt.Id)!;
                Check(result.Status == OutgoingStates.Sent && result.SentAt == receipt.SentAt && result.AttemptCount == 0,
                    "recovery changed the receipt or queued a new delivery");
                Check(services.Database.ListMessagesForAccounts([identity.Id], LocalFolderKinds.Sent)
                    .Count(row => row.Subject == receipt.Subject) == 1, "missing or duplicate Sent copy");
                var folders = (Forms.TreeView)Get(main, "_localTree")!;
                folders.SelectedNode = folders.Nodes.Find("LocalFolder:" + LocalFolderKinds.Sent, true).Single();
                Wait(() => ((IReadOnlyList<object>)Get(main, "_rows")!).OfType<MessageSummary>()
                    .Any(row => row.Subject == receipt.Subject), "recovered message missing in Sent");
                var sentList = (Forms.ListView)Get(main, "_messages")!;
                sentList.SelectedIndices.Clear();
                sentList.SelectedIndices.Add(0);
                Wait(() => Get(main, "_currentContent") is MessageContent content && content.Summary.Subject == receipt.Subject
                    && content.BodyText?.Contains("Receipt fixture") == true, "recovered message body missing in Sent");
                var body = (Forms.RichTextBox)Get(main, "_body")!;
                Wait(() => body.Visible && body.Text.Contains("Receipt fixture"), "recovered message preview not rendered");
                main.Update();
                Pump(200);
                AddressingSmoke.Capture(main.Handle, screenshots, "send-recovery-sent");
                main.Close();
                return;
            }
            var list = (Forms.ListView)Get(main, "_messages")!;
            Wait(() => list.VirtualListSize > 0, "fixture list not loaded");
            if (sendOnly)
            {
                Check(list.VirtualListSize > 50, "scroll fixture not loaded");
                list.SelectedIndices.Clear(); list.SelectedIndices.Add(40);
                list.TopItem = list.Items[30]; Pump(100);
                var selectedId = ((MessageSummary)((IReadOnlyList<object>)Get(main, "_rows")!)[40]).Id;
                var topId = ((MessageSummary)((IReadOnlyList<object>)Get(main, "_rows")!)[list.TopItem!.Index]).Id;
                void CheckPosition()
                {
                    var rows = (IReadOnlyList<object>)Get(main, "_rows")!;
                    Check(list.SelectedIndices.Count == 1 && rows[list.SelectedIndices[0]] is MessageSummary message && message.Id == selectedId, "send changed the selected message");
                    Check(rows[list.TopItem!.Index] is MessageSummary first && first.Id == topId, "send changed the scroll position");
                }
                var tree = (Forms.TreeView)Get(main, "_localTree")!;
                var selectedFolderNode = tree.SelectedNode;
                ((Task)mainType.GetMethod("ComposeAsync", Flags)!.Invoke(main, [Seed("save latency fixture")])!).GetAwaiter().GetResult();
                var savedCompose = Forms.Application.OpenForms.Cast<Forms.Form>().Single(form => form.GetType() == composeType);
                var savedEditor = Get(savedCompose, "_editor")!;
                Wait(() => (bool)Get(savedEditor, "_ready")!, "save editor not ready");
                Task<string>? loadedBody = null;
                var bodyWait = Stopwatch.StartNew();
                var firstBody = true;
                Wait(() =>
                {
                    loadedBody ??= (Task<string>)savedEditor.GetType().GetMethod("GetTextAsync")!.Invoke(savedEditor, null)!;
                    if (!loadedBody.IsCompleted) return false;
                    var text = loadedBody.GetAwaiter().GetResult();
                    if (firstBody) Console.WriteLine($"COMPOSE LOAD first_body_length={text.Length}");
                    firstBody = false;
                    loadedBody = null;
                    return text.Contains("Shortcut body");
                }, "seeded draft body never loaded");
                Console.WriteLine($"COMPOSE LOAD body_ready_ms={bodyWait.ElapsedMilliseconds}");
                var closedBeforeSidebarRefresh = false;
                savedCompose.FormClosed += (_, _) => closedBeforeSidebarRefresh = ReferenceEquals(tree.SelectedNode, selectedFolderNode);
                var saveWatch = Stopwatch.StartNew();
                var save = (Task)composeType.GetMethod("FinishAsync", Flags)!.Invoke(savedCompose, [false])!;
                Wait(() => savedCompose.IsDisposed, "Ctrl+S did not close its saved editor");
                saveWatch.Stop();
                save.GetAwaiter().GetResult();
                var savedDraft = services.Database.ListOutgoing().Single(value => value.Subject == "save latency fixture");
                Console.WriteLine($"COMPOSE SAVE durable_status={savedDraft.Status} body_length={savedDraft.BodyText.Length} body_matches={savedDraft.BodyText.Contains("Shortcut body")}");
                Check(savedDraft.Status == OutgoingStates.Draft && savedDraft.BodyText.Contains("Shortcut body"), "Ctrl+S did not durably preserve its draft");
                Console.WriteLine($"COMPOSE SAVE close_ms={saveWatch.ElapsedMilliseconds} closed_before_sidebar_refresh={closedBeforeSidebarRefresh}");
                Check(closedBeforeSidebarRefresh, "Ctrl+S rebuilt the entire sidebar before closing the saved editor");
                Wait(() => !ReferenceEquals(tree.SelectedNode, selectedFolderNode), "post-save sidebar did not eventually refresh");
                CheckPosition();
                foreach (var immediate in new[] { true, false })
                {
                    services.Database.SetSetting("send.immediately", immediate ? "true" : "false");
                    var subject = immediate ? "shortcut immediate" : "shortcut queued";
                    ((Task)mainType.GetMethod("ComposeAsync", Flags)!.Invoke(main, [Seed(subject)])!).GetAwaiter().GetResult();
                    var compose = Forms.Application.OpenForms.Cast<Forms.Form>().Single(form => form.GetType() == composeType);
                    var editor = Get(compose, "_editor")!;
                    Wait(() => (bool)Get(editor, "_ready")!, "send editor not ready");
                    var elapsed = Stopwatch.StartNew();
                    var finish = (Task)composeType.GetMethod("FinishAsync", Flags)!.Invoke(compose, [true])!;
                    Wait(() => compose.IsDisposed, "compose waited for SMTP");
                    Check(elapsed.Elapsed < TimeSpan.FromSeconds(2), "local queue/close took too long: " + elapsed.Elapsed);
                    finish.GetAwaiter().GetResult();
                    var reload = (Task)mainType.GetMethod("LoadMessagesAsync", Flags)!.Invoke(main, null)!;
                    Wait(() => reload.IsCompleted, "queue refresh did not finish"); reload.GetAwaiter().GetResult();
                    CheckPosition();
                    var outgoing = services.Database.ListOutgoing().Single(value => value.Subject == subject);
                    if (immediate)
                    {
                        Wait(() => awaitingAcceptance.Task.IsCompleted, "SMTP fixture did not receive data");
                        Check(services.Database.GetOutgoing(outgoing.Id)!.Status != OutgoingStates.Sent, "marked sent before SMTP acceptance");
                        Check(main.Enabled && !main.UseWaitCursor, "background send blocks main UI");
                        main.WindowState = Forms.FormWindowState.Minimized; Pump(50);
                        main.WindowState = Forms.FormWindowState.Normal;
                        acceptDelivery.SetResult();
                        Wait(() => services.Database.GetOutgoing(outgoing.Id)!.Status == OutgoingStates.Sent, "accepted delivery not marked sent");
                        Pump(200);
                        reload = (Task)mainType.GetMethod("LoadMessagesAsync", Flags)!.Invoke(main, null)!;
                        Wait(() => reload.IsCompleted, "send refresh did not finish"); reload.GetAwaiter().GetResult();
                        CheckPosition();
                    }
                    else Check(outgoing.Status == OutgoingStates.Outbox && outgoing.AttemptCount == 0, "queued-only setting sent mail");
                }
                return;
            }
            ComposeToolsSmoke.Run(assembly, main, root, services, identity, screenshots);
            if (composeOnly) return;

            foreach (var html in new[] { false, true })
            foreach (var keys in new[] { Forms.Keys.Alt | Forms.Keys.S, Forms.Keys.Control | Forms.Keys.Enter, Forms.Keys.Control | Forms.Keys.S })
            {
                using var compose = (Forms.Form)Activator.CreateInstance(composeType,
                    [new[] { identity }, Seed("standalone shortcut"), html ? Path.Combine(root, "editor") : null, null])!;
                compose.Show(main);
                var editor = (Forms.Control)Get(compose, "_editor")!;
                var plain = (Forms.TextBox)Get(compose, "_plain")!;
                if (html)
                {
                    Wait(() => (bool)Get(editor, "_ready")!, "HTML editor failed to start");
                    Pump(500);
                    var eventArgs = new Forms.KeyEventArgs(keys);
                    typeof(Forms.Control).GetMethod("OnKeyDown", Flags)!.Invoke(editor, [eventArgs]);
                    Check(eventArgs.Handled, "HTML did not suppress " + keys);
                    // The action must be deferred until the browser callback returns.
                    Check(compose.DialogResult == Forms.DialogResult.None, "HTML action ran inside synchronous callback");
                }
                else { Wait(() => plain.Visible, "plain fallback not shown"); Press(compose, keys); }
                Wait(() => compose.DialogResult == Forms.DialogResult.OK, "compose did not finish " + keys);
                Check((bool)composeType.GetProperty("ShouldQueue")!.GetValue(compose)! == (keys != (Forms.Keys.Control | Forms.Keys.S)), "Save sent mail");
                Check((string)composeType.GetProperty("Body")!.GetValue(compose)! == "Shortcut body", "body lost");
                compose.Close();
            }

            // Actual Windows keyboard dispatch with the HTML body focused, not just the handler seam.
            using (var compose = (Forms.Form)Activator.CreateInstance(composeType,
                [new[] { identity }, Seed("native Alt S"), Path.Combine(root, "editor"), null])!)
            {
                compose.Show(main);
                var editor = (Forms.Control)Get(compose, "_editor")!;
                Wait(() => (bool)Get(editor, "_ready")!, "native editor not ready");
                Pump(300);
                Wait(() => { compose.Activate(); return GetForegroundWindow() == compose.Handle; }, "fixture compose did not gain keyboard focus");
                editor.GetType().GetMethod("FocusBody")!.Invoke(editor, null);
                Pump(100);
                Wait(() => { compose.Activate(); return GetForegroundWindow() == compose.Handle; }, "fixture compose does not own keyboard focus");
                Forms.SendKeys.SendWait("%s");
                Wait(() => compose.DialogResult == Forms.DialogResult.OK, "native HTML Alt+S failed");
                Check((bool)composeType.GetProperty("ShouldQueue")!.GetValue(compose)!, "native Alt+S saved instead of Send");
                compose.Close();
            }

            // Full existing compose -> save -> queue -> SMTP path, with both send settings.
            foreach (var immediate in new[] { false, true })
            {
                services.Database.SetSetting("send.immediately", immediate ? "true" : "false");
                using var timer = new Forms.Timer { Interval = 100 };
                Exception? timerError = null;
                var fired = false;
                timer.Tick += (_, _) =>
                {
                    try
                    {
                        var compose = Forms.Application.OpenForms.Cast<Forms.Form>().FirstOrDefault(form => form.GetType() == composeType && form.Visible);
                        if (compose is null || fired) return;
                        var editor = (Forms.Control)Get(compose, "_editor")!;
                        if (!(bool)Get(editor, "_ready")!) return;
                        fired = true;
                        timer.Stop();
                        compose.BeginInvoke(() => Press(compose, Forms.Keys.Alt | Forms.Keys.S));
                    }
                    catch (Exception error) { timerError = error; timer.Stop(); }
                };
                timer.Start();
                var subject = immediate ? "shortcut immediate" : "shortcut queued";
                var composing = (Task)mainType.GetMethod("ComposeAsync", Flags)!.Invoke(main, [Seed(subject)])!;
                Wait(() => composing.IsCompleted, "compose/send did not finish");
                composing.GetAwaiter().GetResult();
                Wait(() => timerError is not null || services.Database.ListOutgoing().Any(value => value.Subject == subject
                    && value.Status == (immediate ? OutgoingStates.Sent : OutgoingStates.Outbox)), "modeless compose/send did not finish");
                if (timerError is not null) throw timerError;
                var outgoing = services.Database.ListOutgoing().Single(value => value.Subject == subject);
                Check(outgoing.Status == (immediate ? OutgoingStates.Sent : OutgoingStates.Outbox), "send setting ignored");
                Check(immediate || outgoing.AttemptCount == 0, "queued mail was attempted");
                if (!immediate) Check(received == 0, "queued message transmitted");
            }

            // Main Ctrl+F is Forward; Ctrl+Shift+F alone finds messages.
            var action = (Action)mainType.GetMethod("MainShortcut", Flags)!.Invoke(main, [Forms.Keys.Control | Forms.Keys.Shift | Forms.Keys.F])!;
            action();
            Check(Forms.Application.OpenForms.Cast<Forms.Form>().Any(form => form.GetType().Name == "FindMessageForm" && form.Visible), "find shortcut missing");
            foreach (var find in Forms.Application.OpenForms.Cast<Forms.Form>().Where(form => form.GetType().Name == "FindMessageForm").ToArray()) find.Hide();
            Press(main, Forms.Keys.Control | Forms.Keys.I);
            Wait(() => list.VirtualListSize >= 1, "Inbox shortcut failed");
            list.Focus();
            Press(main, Forms.Keys.Control | Forms.Keys.A);
            Check(list.SelectedIndices.Count == list.VirtualListSize, "select all did not select list");
            list.SelectedIndices.Clear(); list.SelectedIndices.Add(0);
            var first = services.Database.ListMessages("local-fixture").Single(value => value.Subject == "Automation fixture");
            var content = services.Database.GetMessage(first.Id)!;
            var second = services.Database.ListMessages("local-fixture").Single(value => value.Id != first.Id);
            // Main Ctrl+F must open a forward compose, not Find.
            Wait(() => Get(main, "_currentContent") is MessageContent, "preview not ready");
            VerifyCompose(() => Press(main, Forms.Keys.Control | Forms.Keys.F), "Fw:");
            // The opened reader's captured message wins over the different main selection.
            var rows = ((IEnumerable<object>)Get(main, "_rows")!).ToList();
            list.SelectedIndices.Clear();
            list.SelectedIndices.Add(rows.FindIndex(row => row is MessageSummary message && message.Id == second.Id));
            Pump(200);
            Action Reader(Forms.Keys keys) => (Action)mainType.GetMethod("ReadShortcut", Flags)!.Invoke(main, [content, keys, null])!;
            VerifyCompose(Reader(Forms.Keys.Control | Forms.Keys.R), "Re: Automation fixture");
            VerifyCompose(Reader(Forms.Keys.Control | Forms.Keys.Shift | Forms.Keys.R), "Re: Automation fixture");
            VerifyCompose(Reader(Forms.Keys.Control | Forms.Keys.F), "Fw: Automation fixture");
            var opening = (Task)mainType.GetMethod("OpenMessageAsync", Flags)!.Invoke(main, [first.Id])!;
            Wait(() => opening.IsCompleted, "reader did not open");
            using (var reader = Forms.Application.OpenForms.Cast<Forms.Form>().Single(form => form.GetType().Name == "MessageWindow"))
            {
                Press(reader, Forms.Keys.Alt | Forms.Keys.Enter);
                using (var properties = reader.OwnedForms.Single(form => form.Text == "Message Properties"))
                {
                    var text = ComposeToolsSmoke.Controls(properties).OfType<Forms.TextBox>().Single(control => control.AccessibleName == "Message general").Text;
                    Check(text.Contains("Automation fixture") && !text.Contains("Second reading fixture"), "reader properties used main selection");
                    var headers = ComposeToolsSmoke.Controls(properties).OfType<Forms.TextBox>().Single(control => control.AccessibleName == "Message headers").Text;
                    Check(headers.Contains("MIME-Version:") && !headers.Contains("Fixture body"), "properties headers missing or included body");
                    properties.Close();
                }
                var htmlReader = ComposeToolsSmoke.Controls(reader).First(control => control.GetType().Name == "HtmlView");
                Wait(() => (bool)Get(htmlReader, "_ready")!, "HTML reader not ready");
                Pump(300);
                Press(reader, Forms.Keys.F4);
                using (var find = reader.OwnedForms.Single(form => form.Text == "Find Text"))
                {
                    ComposeToolsSmoke.Controls(find).OfType<Forms.TextBox>().Single().Text = "Formatted";
                    var searching = (Task)find.GetType().GetMethod("FindNextAsync", Flags)!.Invoke(find, null)!;
                    Wait(() => searching.IsCompleted, "reader Find did not complete"); searching.GetAwaiter().GetResult();
                    Check(ComposeToolsSmoke.Controls(find).OfType<Forms.Label>().Single(label => label.AccessibleName == "Find result").Text == "", "reader Find failed with mail scripts disabled");
                    find.Close();
                }
                Press(reader, Forms.Keys.F1);
                using (var help = reader.OwnedForms.Single(form => form.Text == "Mailkeep Help")) help.Close();
                VerifyCompose(() => Press(reader, Forms.Keys.Control | Forms.Keys.R), "Re: Automation fixture");
                Press(reader, Forms.Keys.Escape);
                Check(!reader.Visible, "Esc did not close reader");
            }
            // Navigation supports both punctuation chords and restores native text editing.
            list.SelectedIndices.Clear(); list.SelectedIndices.Add(0);
            Press(main, Forms.Keys.Control | Forms.Keys.OemPeriod);
            Check(list.SelectedIndices[0] == 1, "next message failed");
            Press(main, Forms.Keys.Control | Forms.Keys.Shift | Forms.Keys.Oemcomma);
            Check(list.SelectedIndices[0] == 0, "previous message failed");
            Press(main, Forms.Keys.Control | Forms.Keys.Q);
            Pump(300);
            Press(main, Forms.Keys.Control | Forms.Keys.Shift | Forms.Keys.U);
            Pump(300);
            Check(services.Database.ListMessages("local-fixture").Any(value => !value.IsRead), "mark unread failed");
            var beforeUnread = services.Database.ListMessages("local-fixture").Count(value => !value.IsRead);
            Press(main, Forms.Keys.Control | Forms.Keys.U);
            Pump(100);
            Check(services.Database.ListMessages("local-fixture").Count(value => !value.IsRead) <= beforeUnread, "Ctrl+U marked unread instead of navigating");
            Reader(Forms.Keys.Control | Forms.Keys.D)();
            Wait(() => services.Database.GetMessage(first.Id)!.Summary.Folder == LocalFolderKinds.Deleted, "reader Delete missed its own message");
            Check(services.Database.GetMessage(second.Id)!.Summary.Folder == LocalFolderKinds.Inbox, "reader deleted main selection");
            var filed = services.Database.ListLocalFolders().Single(folder => folder.Name == "Filed fixture");
            using (var timer = new Forms.Timer { Interval = 50 })
            {
                timer.Tick += (_, _) =>
                {
                    var picker = Forms.Application.OpenForms.Cast<Forms.Form>().FirstOrDefault(form => form.GetType().Name == "FolderChoiceForm");
                    if (picker is null) return;
                    timer.Stop();
                    var destinations = picker.Controls.OfType<Forms.ListBox>().Single();
                    destinations.SelectedItem = destinations.Items.Cast<object>().Single(item => (string)item.GetType().GetProperty("Name")!.GetValue(item)! == filed.Name);
                    picker.DialogResult = Forms.DialogResult.OK;
                };
                timer.Start();
                list.SelectedIndices.Clear();
                var currentRows = ((IEnumerable<object>)Get(main, "_rows")!).ToList();
                list.SelectedIndices.Add(currentRows.FindIndex(row => row is MessageSummary message && message.Id == second.Id));
                Press(main, Forms.Keys.Control | Forms.Keys.Shift | Forms.Keys.V);
                Wait(() => services.Database.GetMessage(second.Id)!.Summary.Folder == filed.Key, "Ctrl+Shift+V did not move selection");
            }
            using (var timer = new Forms.Timer { Interval = 50 })
            {
                timer.Tick += (_, _) =>
                {
                    var picker = Forms.Application.OpenForms.Cast<Forms.Form>().FirstOrDefault(form => form.Text == "Go to Folder");
                    if (picker is null) return;
                    timer.Stop();
                    var tree = picker.Controls.OfType<Forms.TreeView>().Single();
                    var choices = tree.Nodes.Cast<Forms.TreeNode>().SelectMany(node => node.Nodes.Cast<Forms.TreeNode>());
                    tree.SelectedNode = choices.Single(node => node.Text.StartsWith(filed.Name));
                    picker.DialogResult = Forms.DialogResult.OK;
                };
                timer.Start();
                Press(main, Forms.Keys.Control | Forms.Keys.Y);
                Wait(() => (string)Get(main, "_selectedLocalFolder")! == filed.Key && list.VirtualListSize == 1, "Ctrl+Y did not navigate to chosen folder");
                Press(main, Forms.Keys.Control | Forms.Keys.I);
            }
            using (var editing = new Forms.TextBox { Text = "editing fixture", Dock = Forms.DockStyle.Top })
            {
                main.Controls.Add(editing);
                main.Activate();
                editing.Focus();
                Pump(100);
                Check(mainType.GetMethod("MainShortcut", Flags)!.Invoke(main, [Forms.Keys.Delete]) is null, "Delete while editing was a mailbox command");
                Check(mainType.GetMethod("MainShortcut", Flags)!.Invoke(main, [Forms.Keys.Control | Forms.Keys.A]) is null, "Ctrl+A while editing selected mail");
            }
            main.Close();
            Pump(100);

            void VerifyCompose(Action trigger, string prefix)
            {
                using var timer = new Forms.Timer { Interval = 50 };
                string? actual = null;
                timer.Tick += (_, _) =>
                {
                    var compose = Forms.Application.OpenForms.Cast<Forms.Form>().FirstOrDefault(form => form.GetType() == composeType && form.Visible);
                    if (compose is null) return;
                    timer.Stop();
                    actual = (string)composeType.GetProperty("Subject")!.GetValue(compose)!;
                    composeType.GetField("_discard", Flags)!.SetValue(compose, true);
                    compose.DialogResult = Forms.DialogResult.Cancel;
                    compose.Close();
                };
                timer.Start();
                trigger();
                Wait(() => actual is not null, "shortcut did not open compose");
                Check(actual!.StartsWith(prefix), "shortcut opened wrong compose: " + actual);
            }
        }
    }

    private static void Press(Forms.Form form, Forms.Keys keys)
    {
        var message = Forms.Message.Create(form.Handle, 0x100, (IntPtr)(int)(keys & Forms.Keys.KeyCode), IntPtr.Zero);
        Check((bool)form.GetType().GetMethod("ProcessCmdKey", Flags)!.Invoke(form, [message, keys])!, "shortcut not handled: " + keys);
    }
    private static void Pump(int ms)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ms) { Forms.Application.DoEvents(); Thread.Sleep(10); }
    }
    private static void Wait(Func<bool> done, string error)
    {
        var clock = Stopwatch.StartNew();
        while (!done()) { if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(error); Pump(10); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
