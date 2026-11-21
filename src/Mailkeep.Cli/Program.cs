using System.Text.Json;
using Mailkeep.Core;
using Mailkeep.Infrastructure;

const string Help = """
Mailkeep — local-first Windows mail client and agent command surface

Usage: mailkeep <command> [options]

Global: --root PATH

  accounts                        List configured accounts and server modes (never credentials)
  accounts-add --address A [--provider gmail|outlook|microsoft|custom] [--mode local|connected]
             [--protocol pop3|imap] [--host H] [--port P] [--security S] [--name N] [--execute]
                                  Create an account record; authorization still happens in the app
  accounts-keepalive NAME [--hours N] [--off] [--execute]
                                  How often an unattended pass collects the account (or take it off the timer)
  senders                         List configured Sending Identities (never credentials)
  senders set ID [--display-name N] [--reply-to A] [--smtp-host H] [--smtp-port P]
             [--security ssl_on_connect|starttls|auto|none] [--username U] [--password-stdin] [--execute]
  senders authorize ID [--execute]  Grant SMTP send access through the browser (Gmail/Microsoft)
  senders test ID [--execute]     Authenticate against SMTP without sending; marks the identity ready
  senders default ID [--execute]  Make one identity the default From address
  sync [ACCOUNT] [--due]          Sync one account, all accounts, or only due accounts
  baseline ACCOUNT [--execute]    Treat everything now on the server as already collected and download none
                                  of it, so collection starts from now. For running alongside another client
                                  that already holds the history.
  search QUERY [--source connected|local|archive|all] [--limit N] [--folder F] [--account A]
             [--match all|message|attachment-name|attachment-content]
             Query syntax: "exact phrase" · AND/OR/NOT · prefix* · subject: sender: recipient: body:
             filters · folder:"Archive/Receipts" · e.g. search "subject:invoice OR sender:amazon"
  unread [--limit N]              Newest unread live mail subjects; archive and Deleted Items excluded
  get ID|MESSAGE_REF [--timings]   Get a message; --timings reports read phases without message content
  index status                    Show unified search source generations
  index sync|rebuild [--attachments] Refresh the search catalog from the local store
  index attachments status|sync|estimate Inspect, build, or sample the attachment index
  index attachments rebuild --extractor ID [--execute]
                                  Dry-run or invalidate and rebuild one extractor version
  drafts                          List saved drafts
  outbox                          List queued/retrying messages
  recover-send ID --mark-sent|--return-to-outbox [--execute]
                                  Resolve an uncertain send explicitly; never redeliver automatically
  prepare --identity I --to X --subject S (--body B|--body-file PATH) [--html-file PATH] [--attach PATH]... [--execute]
                                  Save a draft; with --html-file it goes formatted, text derived if not given;
                                  --attach repeats, one file each, and a missing file fails before saving
  discard DRAFT_ID [--execute]    Delete a draft that has not been queued
  queue DRAFT_ID [--execute]      Freeze a draft as exact MIME in Outbox
  send [MESSAGE_ID] [--execute]   Send only that Outbox message, or all due messages if no ID
  send-receive [--execute]        Send Outbox, then receive all accounts
  send-self --identity I ...      Prepare, queue, and send only this message to the identity
  folders                         List local source/account/folder counts
  folders --local                 List local destination keys and full folder paths, including empty folders
  move ID|MESSAGE_REF --folder KEY|PATH [--execute]
                                  Preview local filing; --execute applies the app's move/adoption behavior
  move ID|MESSAGE_REF --folder SERVER_PATH --server [--execute]
                                  Queue a durable Connected IMAP move; the running app sends it in the background
  stats                           Show local message counts
  identity report                 Read-only byte-identity and OE/Mailkeep precedence report
  migration oe-drafts report       Read-only OE Drafts/Outbox conversion candidates
  migration oe-drafts retire --report PATH --existing-backup PATH --execute  Remove exact hidden originals; keep active drafts and receipts
  migration sent report            Read-only delivered-mail retention audit; never sends or restores mail
  migration adoptions report [--original-ids FILE]  Read-only original/adopted links; optional exact JSON ID list
  migration adoptions apply --report PATH --execute [--existing-backup PATH]  Remove reviewed hidden originals; fresh backup by default
  migration oe-drafts apply --report PATH --execute  Convert exactly the reviewed candidates to unsent drafts
  log [--limit N]                 Show recent fetch attempts and errors
  folder convert KEY|PATH --to DIR [--execute]
                                  Copy one folder out of the database (read-only) into DIR/<name>/folder.mkdb
  folder merge-imported --from KEY --into KEY --root ROOT
             [--execute --confirm-root ROOT --confirm-plan SHA256]
                                  Merge a small misplaced imported root into an existing nested folder; checked backup
  folder archive KEY|PATH --to LOCATION [--execute] [--no-backup] [--prune-empty-parents]
                                  Verify, back up, then move one folder to the archive; --no-backup uses an existing batch backup
  folder prune-archived-parents KEY [--execute]
                                  Preview removal of empty imported parents left above archived folders
  folder retire-import ACCOUNTS_JSON --folder KEY [--execute]
                                  File named imports into Local Mail, verify and back up, then remove their empty branches
  folder restore LOCATION [--path LABEL] [--tree] [--into PARENT_PATH] [--execute]
  folder copy-from-archive LOCATION [--path LABEL] [--tree] [--into PARENT_PATH] [--execute]
  folder storage status|rebuild --root ROOT [--execute --confirm-root ROOT]
                                  Common portable-writer status or offline projection rebuild
  folder storage bulk plan|status|run|publish|recover --root ROOT
                                  Offline upgrade of older coordinator stores; exact root/plan confirmation required
  folder storage consolidate-pilots plan|status|run|recover --root ROOT
                                  Offline reader/upgrade for older pilot folder files; no legacy live writer
  folder manifest LOCATION [--execute]
                                  Inspect or write the portable archive tree inventory
  folder list FILE [--limit N] [--all]   List a folder file's messages, newest first
  folder get FILE SEQ [--raw PATH]       One message from a folder file; --raw writes its exact bytes
  folder verify FILE [--against-database] Check a folder file; optionally compare it with its source folder
  folder rebuild-cache FILE --execute    Rebuild a folder file's header/search caches from its message bytes

JSON is the default and only machine-output format. Errors are JSON on stdout and exit non-zero.
""";

var json = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented = true
};

if (args.Length > 0 && args[0].Equals("attachment-worker", StringComparison.OrdinalIgnoreCase))
{
    await AttachmentWorkerProtocol.RunAsync();
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    var commandArgs = WithoutGlobalOptions(args);
    if (commandArgs.Length > 0 && commandArgs[0].Equals("unread", StringComparison.OrdinalIgnoreCase))
    {
        for (var index = 1; index < commandArgs.Length; index++)
        {
            if (commandArgs[index] == "--limit" && index + 1 < commandArgs.Length) { index++; continue; }
            throw new ArgumentException("Usage: mailkeep unread [--limit 1..20]");
        }
        var limit = IntOption(commandArgs, "--limit", 10);
        if (limit < 1 || limit > 20) throw new ArgumentException("unread limit must be 1..20.");
        // Reads the existing projection without schema initialization or mail sync.
        var database = new MailkeepServices(StringOption(args, "--root")).Database;
        var messages = database.ListUnreadMessages(limit + 1);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Source = "mailkeep", Shown = Math.Min(messages.Count, limit), Truncated = messages.Count > limit,
            Messages = messages.Take(limit).Select(m => new { m.Id, m.Subject, m.Sender, m.Date, m.Account, m.Folder })
        }, json));
        return 0;
    }
    if (commandArgs.Length > 0 && commandArgs[0].Equals("move", StringComparison.OrdinalIgnoreCase))
    {
        if (commandArgs.Length < 2 || commandArgs[1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Usage: mailkeep move ID|MESSAGE_REF --folder KEY|PATH [--server] [--execute]");
        for (var index = 2; index < commandArgs.Length; index++)
        {
            if (commandArgs[index] == "--folder" && index + 1 < commandArgs.Length) { index++; continue; }
            if (commandArgs[index] is "--server" or "--execute") continue;
            throw new ArgumentException("move accepts one message ID, --folder, --server and --execute.");
        }
        var filing = new MailFiling(new MailkeepServices(StringOption(args, "--root")));
        Console.WriteLine(JsonSerializer.Serialize(await filing.MoveAsync(commandArgs[1], RequiredOption(commandArgs, "--folder"),
            commandArgs.Contains("--execute", StringComparer.OrdinalIgnoreCase), commandArgs.Contains("--server", StringComparer.OrdinalIgnoreCase), cancellation.Token), json));
        return 0;
    }
    if (commandArgs.Length > 0 && commandArgs[0].Equals("folders", StringComparison.OrdinalIgnoreCase)
        && commandArgs.Contains("--local", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine(JsonSerializer.Serialize(new MailFiling(new MailkeepServices(StringOption(args, "--root"))).Folders(), json));
        return 0;
    }
    // Migration diagnostics must not run either store's schema initialization.
    if (commandArgs.Length > 0 && commandArgs[0].Equals("migration", StringComparison.OrdinalIgnoreCase))
    {
        if (commandArgs.Length >= 3 && commandArgs[1] == "oe-drafts" && commandArgs[2] == "retire")
        {
            var reviewed = JsonSerializer.Deserialize<OeDraftConversion.RetirementOriginal[]>(File.ReadAllText(RequiredOption(args, "--report")), json)
                ?? throw new InvalidDataException("Invalid reviewed retirement list.");
            if (!args.Contains("--execute", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine(JsonSerializer.Serialize(new { ExecuteRequired = true, Originals = reviewed }, json));
                return 0;
            }
            if (System.Diagnostics.Process.GetProcessesByName("Mailkeep.WinForms").Length > 0)
                throw new InvalidOperationException("Close Mailkeep politely before retiring hidden originals.");
            Console.WriteLine(JsonSerializer.Serialize(new OeDraftConversion(new AppPaths(StringOption(args, "--root")))
                .RetireOriginals(reviewed, RequiredOption(args, "--existing-backup"), cancellation.Token), json));
            return 0;
        }
        if (commandArgs.Length >= 3 && commandArgs[1] == "adoptions" && commandArgs[2] == "apply")
        {
            var paths = new AppPaths(StringOption(args, "--root"));
            if (!args.Contains("--execute", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine(JsonSerializer.Serialize(new { ExecuteRequired = true, Report = new AdoptionMigrationReport(paths).Run(cancellation.Token) }, json));
                return 0;
            }
            var approved = JsonSerializer.Deserialize<AdoptionMigrationReport.Report>(File.ReadAllText(RequiredOption(args, "--report")), json)
                ?? throw new InvalidDataException("Invalid reviewed report.");
            if (System.Diagnostics.Process.GetProcessesByName("Mailkeep.WinForms").Length > 0)
                throw new InvalidOperationException("Close Mailkeep politely before adoption cleanup.");
            var cleanup = new AdoptionCleanup(paths);
            var existingBackup = StringOption(args, "--existing-backup");
            Console.WriteLine(JsonSerializer.Serialize(existingBackup is null
                ? cleanup.Apply(approved, cancellation.Token)
                : cleanup.ApplyUsingExistingBackup(approved, existingBackup, cancellation.Token), json));
            return 0;
        }
        if ((commandArgs.Length == 3 || commandArgs.Length == 5 && commandArgs[3] == "--original-ids")
            && commandArgs[1] == "adoptions" && commandArgs[2] == "report")
        {
            var idsFile = StringOption(args, "--original-ids");
            var ids = idsFile is null ? null : JsonSerializer.Deserialize<long[]>(File.ReadAllText(idsFile), json)
                ?? throw new InvalidDataException("Invalid original ID list.");
            Console.WriteLine(JsonSerializer.Serialize(new AdoptionMigrationReport(new AppPaths(StringOption(args, "--root"))).Run(cancellation.Token, null, ids), json));
            return 0;
        }
        if (commandArgs.Length == 3 && commandArgs[1] == "sent" && commandArgs[2] == "report")
        {
            Console.WriteLine(JsonSerializer.Serialize(new SentCopyReport(new AppPaths(StringOption(args, "--root"))).Run(cancellation.Token), json));
            return 0;
        }
        if (commandArgs.Length >= 3 && commandArgs[1] == "oe-drafts" && commandArgs[2] == "apply")
        {
            var paths = new AppPaths(StringOption(args, "--root"));
            if (!args.Contains("--execute", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine(JsonSerializer.Serialize(new { ExecuteRequired = true, Report = new OeDraftConversionReport(paths).Run(cancellation.Token) }, json));
                return 0;
            }
            var approved = JsonSerializer.Deserialize<OeDraftConversionReport.Report>(File.ReadAllText(RequiredOption(args, "--report")), json)
                ?? throw new InvalidDataException("Invalid reviewed report.");
            Console.WriteLine(JsonSerializer.Serialize(new OeDraftConversion(paths).Apply(approved, cancellation.Token), json));
            return 0;
        }
        if (commandArgs.Length != 3 || commandArgs[1] != "oe-drafts" || commandArgs[2] != "report")
            throw new ArgumentException("Usage: mailkeep migration oe-drafts report [--root PATH]");
        Console.WriteLine(JsonSerializer.Serialize(new OeDraftConversionReport(new AppPaths(StringOption(args, "--root")))
            .Run(cancellation.Token), json));
        return 0;
    }
    // This diagnostic must not run either store's schema initialization.
    if (commandArgs.Length > 0 && commandArgs[0].Equals("identity", StringComparison.OrdinalIgnoreCase))
    {
        if (commandArgs.Length != 2 || !commandArgs[1].Equals("report", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Usage: mailkeep identity report [--root PATH]");
        var report = new MessageIdentityReport(new AppPaths(StringOption(args, "--root")))
            .Run(cancellation.Token, message => Console.Error.WriteLine(message));
        Console.WriteLine(JsonSerializer.Serialize(report, json));
        return 0;
    }
    // Folder commands bypass normal database initialization; archive/prune/retire --execute change mailbox rows.
    if (commandArgs.Length > 0 && commandArgs[0].Equals("folder", StringComparison.OrdinalIgnoreCase))
    {
        const string FolderUsage = "Usage: mailkeep folder convert KEY|PATH --to DIR [--execute] | archive KEY|PATH --to LOCATION [--execute] [--no-backup] [--prune-empty-parents] "
            + "| prune-archived-parents KEY [--execute] | retire-import ACCOUNTS_JSON --folder KEY [--execute] "
            + "| restore LOCATION [--path LABEL] [--tree] [--into PARENT_PATH] [--execute] | copy-from-archive LOCATION [--path LABEL] [--tree] [--into PARENT_PATH] [--execute] | manifest LOCATION [--execute] | list FILE [--limit N] [--all] "
            + "| get FILE SEQ [--raw PATH] | verify FILE [--against-database] | rebuild-cache FILE --execute";
        if (commandArgs.Length < 3) throw new ArgumentException(FolderUsage);
        if (commandArgs[1].Equals("merge-imported", StringComparison.OrdinalIgnoreCase))
        {
            var mergePaths = new AppPaths(RequiredOption(args, "--root"));
            var merge = new ImportedFolderMerge(mergePaths);
            var from = RequiredOption(commandArgs, "--from"); var into = RequiredOption(commandArgs, "--into");
            var plan = merge.Preview(from, into);
            if (!commandArgs.Contains("--execute", StringComparer.OrdinalIgnoreCase))
                Console.WriteLine(JsonSerializer.Serialize(new { Executed = false, Plan = plan }, json));
            else
            {
                if (StringOption(commandArgs, "--confirm-root") != mergePaths.Root || StringOption(commandArgs, "--confirm-plan") != plan.Confirmation)
                    throw new InvalidOperationException("Confirm the exact root and reviewed merge plan.");
                if (System.Diagnostics.Process.GetProcessesByName("Mailkeep.WinForms").Length > 0)
                    throw new InvalidOperationException("Close Mailkeep politely first.");
                Console.WriteLine(JsonSerializer.Serialize(new { Executed = true, Result = merge.Apply(from, into, plan.Confirmation) }, json));
            }
            return 0;
        }
        if (commandArgs[1].Equals("storage", StringComparison.OrdinalIgnoreCase))
        {
            var prototypeRoot = StringOption(args, "--root") ?? throw new ArgumentException("Storage prototype requires an explicit --root.");
            var prototypePaths = new AppPaths(prototypeRoot);
            var storage = new WritableFolderStorage(prototypePaths, new RawBlobStore(prototypePaths));
            var action = commandArgs[2].ToLowerInvariant();
            if (action == "consolidate-pilots")
            {
                var migration = new BulkFolderMigration(prototypePaths,
                    allowLive: commandArgs.Contains("--allow-live", StringComparer.OrdinalIgnoreCase));
                var operation = commandArgs.Length > 3 ? commandArgs[3].ToLowerInvariant() : "status";
                if (operation == "plan") Console.WriteLine(JsonSerializer.Serialize(migration.PreviewPilots(), json));
                else if (operation == "status") Console.WriteLine(JsonSerializer.Serialize(migration.PilotStatus(), json));
                else
                {
                    if (!commandArgs.Contains("--execute", StringComparer.OrdinalIgnoreCase)
                        || StringOption(commandArgs, "--confirm-root") != prototypePaths.Root)
                        throw new InvalidOperationException("Pilot consolidation requires --execute and the exact --confirm-root.");
                    if (System.Diagnostics.Process.GetProcessesByName("Mailkeep.WinForms").Length > 0)
                        throw new InvalidOperationException("Close Mailkeep politely first.");
                    var pilotResult = operation switch
                    {
                        "run" => migration.ConsolidatePilots(RequiredOption(commandArgs, "--confirm-plan"),
                            p => Console.Error.WriteLine(p)),
                        "recover" => migration.RecoverPilots(),
                        _ => throw new ArgumentException("Use consolidate-pilots plan|run|status|recover.")
                    };
                    Console.WriteLine(JsonSerializer.Serialize(pilotResult, json));
                }
                return 0;
            }
            if (action == "bulk")
            {
                var bulk = new BulkFolderMigration(prototypePaths,
                    allowLive: commandArgs.Contains("--allow-live", StringComparer.OrdinalIgnoreCase));
                var operation = commandArgs.Length > 3 ? commandArgs[3].ToLowerInvariant() : "status";
                if (operation == "plan") Console.WriteLine(JsonSerializer.Serialize(bulk.Preview(), json));
                else if (operation == "status") Console.WriteLine(JsonSerializer.Serialize(bulk.Status(), json));
                else
                {
                    if (!commandArgs.Contains("--execute", StringComparer.OrdinalIgnoreCase)
                        || StringOption(commandArgs, "--confirm-root") != prototypePaths.Root)
                        throw new InvalidOperationException("Bulk writes require --execute and the exact --confirm-root.");
                    if (System.Diagnostics.Process.GetProcessesByName("Mailkeep.WinForms").Length > 0)
                        throw new InvalidOperationException("Close Mailkeep politely first.");
                    object bulkResult = operation switch
                    {
                        "run" => bulk.Run(RequiredOption(commandArgs, "--confirm-plan"),
                            p => Console.Error.WriteLine(JsonSerializer.Serialize(p, json)), cancellation.Token,
                            publish: !commandArgs.Contains("--stage-only", StringComparer.OrdinalIgnoreCase)),
                        "publish" => bulk.Publish(),
                        "recover" => bulk.Recover(),
                        _ => throw new ArgumentException("Use bulk plan|run|status|publish|recover.")
                    };
                    Console.WriteLine(JsonSerializer.Serialize(bulkResult, json));
                }
                return 0;
            }
            if (action is not ("status" or "rebuild"))
                throw new InvalidOperationException("Legacy pilot enrollment/rollback/probe commands are retired. Use the common folder writer; older pilot stores require consolidate-pilots.");
            if (action == "rebuild" && commandArgs.Contains("--execute", StringComparer.OrdinalIgnoreCase))
            {
                if (System.Diagnostics.Process.GetProcessesByName("Mailkeep.WinForms").Length > 0)
                    throw new InvalidOperationException("Close Mailkeep politely before rebuilding its projection.");
                if (StringOption(commandArgs, "--confirm-root") != prototypePaths.Root)
                    throw new InvalidOperationException("Rebuild requires the exact --confirm-root.");
                Console.WriteLine(JsonSerializer.Serialize(new { Rebuilt = storage.RebuildProjection(), Status = storage.Inspect() }, json));
            }
            else Console.WriteLine(JsonSerializer.Serialize(new { ExecuteRequired = action == "rebuild", Status = storage.Inspect() }, json));
            return 0;
        }
        var file = commandArgs[2];
        object folderResult = commandArgs[1].ToLowerInvariant() switch
        {
            "convert" => new FolderConversion(new AppPaths(StringOption(args, "--root"))).Convert(commandArgs[2],
                RequiredOption(commandArgs, "--to"), commandArgs.Contains("--execute", StringComparer.OrdinalIgnoreCase), cancellation.Token),
            // Moves the folder out of the database: preview by default; --execute writes and checks the file,
            // backs up the database, then removes the folder's rows. Close the Mailkeep window first.
            "archive" => commandArgs.Contains("--execute", StringComparer.OrdinalIgnoreCase)
                    && System.Diagnostics.Process.GetProcessesByName("Mailkeep.WinForms").Length > 0
                ? throw new InvalidOperationException("Close the Mailkeep window before archiving a folder; nothing changed.")
                : new FolderArchive(new AppPaths(StringOption(args, "--root"))).Move(commandArgs[2],
                RequiredOption(commandArgs, "--to"), commandArgs.Contains("--execute", StringComparer.OrdinalIgnoreCase), cancellation.Token,
                backup: !commandArgs.Contains("--no-backup", StringComparer.OrdinalIgnoreCase),
                pruneEmptyParents: commandArgs.Contains("--prune-empty-parents", StringComparer.OrdinalIgnoreCase)),
            "prune-archived-parents" => commandArgs.Contains("--execute", StringComparer.OrdinalIgnoreCase)
                    && System.Diagnostics.Process.GetProcessesByName("Mailkeep.WinForms").Length > 0
                ? throw new InvalidOperationException("Close the Mailkeep window before pruning archived parents; nothing changed.")
                : new { Executed = commandArgs.Contains("--execute", StringComparer.OrdinalIgnoreCase),
                    Parents = new FolderArchive(new AppPaths(StringOption(args, "--root")))
                        .PruneEmptyParents(commandArgs[2], commandArgs.Contains("--execute", StringComparer.OrdinalIgnoreCase)) },
            "retire-import" => commandArgs.Contains("--execute", StringComparer.OrdinalIgnoreCase)
                    && System.Diagnostics.Process.GetProcessesByName("Mailkeep.WinForms").Length > 0
                ? throw new InvalidOperationException("Close Mailkeep before retiring imported branches; nothing changed.")
                : new ImportRetirement(new MailkeepServices(StringOption(args, "--root"))).Run(
                    JsonSerializer.Deserialize<string[]>(File.ReadAllText(file)) ?? throw new ArgumentException("Expected a JSON array of exact account names."),
                    RequiredOption(commandArgs, "--folder"), commandArgs.Contains("--execute", StringComparer.OrdinalIgnoreCase), cancellation.Token),
            "restore" or "copy-from-archive" => commandArgs.Contains("--execute", StringComparer.OrdinalIgnoreCase)
                    && System.Diagnostics.Process.GetProcessesByName("Mailkeep.WinForms").Length > 0
                ? throw new InvalidOperationException("Close Mailkeep before a CLI archive restore; nothing changed.")
                : commandArgs[1].Equals("restore", StringComparison.OrdinalIgnoreCase)
                ? new ArchiveMoveBack(new MailkeepServices(StringOption(args, "--root"))).Run(file,
                    StringOption(commandArgs, "--path") ?? "", commandArgs.Contains("--tree", StringComparer.OrdinalIgnoreCase),
                    StringOption(commandArgs, "--into") ?? "", commandArgs.Contains("--execute", StringComparer.OrdinalIgnoreCase), cancellation.Token)
                : new ArchiveRestore(new MailkeepServices(StringOption(args, "--root"))).Run(file,
                    StringOption(commandArgs, "--path") ?? "", commandArgs.Contains("--tree", StringComparer.OrdinalIgnoreCase),
                    StringOption(commandArgs, "--into") ?? "", commandArgs.Contains("--execute", StringComparer.OrdinalIgnoreCase), cancellation.Token),
            "manifest" => FolderManifest(file, commandArgs.Contains("--execute", StringComparer.OrdinalIgnoreCase), new AppPaths(StringOption(args, "--root"))),
            "list" => FolderList(file, IntOption(commandArgs, "--limit", 50), commandArgs.Contains("--all", StringComparer.OrdinalIgnoreCase)),
            "get" => FolderGet(file, commandArgs.Length > 3 && long.TryParse(commandArgs[3], out var seq)
                ? seq : throw new ArgumentException(FolderUsage), StringOption(commandArgs, "--raw")),
            "verify" => commandArgs.Contains("--against-database", StringComparer.OrdinalIgnoreCase)
                ? new { File = FolderVerify(file, cancellation.Token), Database = new FolderConversion(new AppPaths(StringOption(args, "--root"))).Compare(file, cancellation.Token) }
                : FolderVerify(file, cancellation.Token),
            "rebuild-cache" => commandArgs.Contains("--execute", StringComparer.OrdinalIgnoreCase)
                ? FolderRebuild(file)
                : new { ExecuteRequired = true, File = file },
            _ => throw new ArgumentException(FolderUsage)
        };
        Console.WriteLine(JsonSerializer.Serialize(folderResult, json));
        return 0;
    }
    var services = new MailkeepServices(StringOption(args, "--root"));
    services.Database.Initialize();
    if (commandArgs.Length == 0 || commandArgs[0] is "help" or "--help" or "-h")
    {
        Console.WriteLine(Help);
        return 0;
    }

    object result = commandArgs[0].ToLowerInvariant() switch
    {
        "accounts" => services.Accounts.Load().Select(a => new
        {
            a.Name,
            a.Provider,
            a.Address,
            a.Mailbox,
            a.FolderLabel,
            a.Host,
            MailboxMode = a.EffectiveMailboxMode,
            Protocol = a.EffectiveProtocol,
            Username = a.EffectiveUsername,
            Incoming = a.Incoming,
            Auth = a.OAuth is not null ? "oauth2" : a.AppPassword is not null ? "app_password" : "none",
            KeepAlive = a.EffectiveKeepAlive
        }),
        "accounts-add" => AddAccount(services, commandArgs),
        "accounts-keepalive" => SetKeepAlive(services, commandArgs),
        "senders" => await SendersAsync(services, commandArgs),
        "folders" => services.Database.Folders().Select(f => new { f.Source, f.Account, f.Folder, f.Count }),
        "stats" => new { Sources = services.Database.Stats(), Accounts = services.Accounts.Load().Count },
        "log" => services.Database.RecentFetches(IntOption(commandArgs, "--limit", 100)),
        "search" => await SearchAsync(services, commandArgs),
        "get" => await GetAsync(services, commandArgs),
        "sync" => await SyncAsync(services, commandArgs),
        "baseline" => await BaselineAsync(services, commandArgs),
        "index" => Index(services, commandArgs, cancellation.Token),
        "drafts" => services.Database.ListOutgoing(OutgoingStates.Draft, IntOption(commandArgs, "--limit", 100)),
        "outbox" => services.Database.ListOutbox(IntOption(commandArgs, "--limit", 100)),
        "recover-send" => await RecoverSendAsync(services, commandArgs),
        "prepare" => Prepare(services, commandArgs),
        "discard" => Discard(services, commandArgs),
        "queue" => await QueueAsync(services, commandArgs),
        "send" => await SendAsync(services, commandArgs),
        "send-receive" => await SendReceiveAsync(services, commandArgs),
        "send-self" => await SendSelfAsync(services, commandArgs),
        _ => throw new ArgumentException($"Unknown command '{commandArgs[0]}'. Run mailkeep --help.")
    };

    Console.WriteLine(JsonSerializer.Serialize(result, json));
    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine(JsonSerializer.Serialize(new { Cancelled = true }, json));
    return 130;
}
catch (Exception exception)
{
    Console.WriteLine(JsonSerializer.Serialize(new { Error = exception.Message, Type = exception.GetType().Name }, json));
    return 1;
}

static async Task<object> SearchAsync(MailkeepServices services, string[] args)
{
    if (args.Length < 2) throw new ArgumentException("search requires a query.");
    var query = args[1];
    var source = StringOption(args, "--source") ?? "all";
    var limit = IntOption(args, "--limit", 20);
    services.Catalog.SyncOperational();
    services.Catalog.SyncArchive();
    var filters = new SearchFilters(MatchScope: MatchScope(StringOption(args, "--match")));
    var catalog = services.Catalog.Search(query, source, limit,
        StringOption(args, "--folder"), StringOption(args, "--account"), filters);
    return new { catalog.Query, catalog.Count, catalog.Truncated, Results = catalog.Results.Select(SearchResult) };
}

static async Task<object> GetAsync(MailkeepServices services, string[] args)
{
    var timings = args.Contains("--timings", StringComparer.OrdinalIgnoreCase) ? new List<object>() : null;
    MessageContent? Read(long id) => services.Database.GetMessage(id, timings is null ? null : (phase, milliseconds) =>
    {
        var sample = new { phase, milliseconds };
        timings.Add(sample);
        Console.Error.WriteLine(JsonSerializer.Serialize(sample));
    });
    object Result(MessageContent content) => timings is null ? MessageResult(content)
        : new { id = content.Summary.Id, read_timings = timings };
    if (args.Length >= 2 && args[1].StartsWith("msg_", StringComparison.Ordinal))
    {
        var locator = services.Catalog.Resolve(args[1]) ?? throw new KeyNotFoundException("Message reference not found.");
        if (locator.LocalId is null) throw new KeyNotFoundException("Message row not found.");
        var resolved = Read(locator.LocalId.Value) ?? throw new KeyNotFoundException("Message not found.");
        return Result(resolved with { Summary = resolved.Summary with { MessageRef = args[1], Locations = [locator.Location] } });
    }
    if (args.Length < 2 || !long.TryParse(args[1], out var id)) throw new ArgumentException("get requires a numeric message id or msg_ reference.");
    var message = Read(id) ?? throw new KeyNotFoundException("Message not found.");
    return Result(message);
}

static async Task<object> SyncAsync(MailkeepServices services, string[] args)
{
    var due = args.Contains("--due", StringComparer.OrdinalIgnoreCase);
    var account = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;
    object result = account is null
        ? await services.Sync.SyncAllAsync(due)
        : await services.Sync.SyncAsync(account, "cli");
    var failures = result switch
    {
        SyncResult one when !one.Ok => [one],
        IReadOnlyList<SyncResult> many => many.Where(item => !item.Ok).ToArray(),
        _ => []
    };
    if (failures.Length > 0)
        throw new InvalidOperationException(string.Join("; ", failures.Select(item => $"{item.Account}: {item.Error}")));
    RefreshOperationalCatalog(services);
    return result;
}

// Delivery/receipt is already committed. A busy derived index must not turn a
// successful send into an apparent failure that a scheduled caller might resend.
static string? RefreshOperationalCatalog(MailkeepServices services)
{
    try { services.Catalog.SyncOperational(); return null; }
    catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or IOException)
    {
        var warning = "Mail operation completed; search update remains pending: " + error.Message;
        Console.Error.WriteLine(warning);
        return warning;
    }
}

/// <summary>Records where a mailbox has got to without collecting any of it, so an account whose history is
/// already held elsewhere starts from now rather than downloading itself all over again.</summary>
static async Task<object> BaselineAsync(MailkeepServices services, string[] args)
{
    if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException("baseline requires an account name.");
    var name = args[1];
    var account = services.Accounts.Load().FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"No account named {name}.");
    if (!args.Contains("--execute", StringComparer.OrdinalIgnoreCase))
        return new
        {
            ExecuteRequired = true,
            Account = account.Name,
            account.Address,
            Protocol = account.EffectiveProtocol,
            Effect = "Everything currently on the server is recorded as collected. Nothing is downloaded, "
                   + "nothing already stored is removed, and later syncs bring only mail arriving after now."
        };
    var result = await services.Sync.BaselineAsync(account.Name);
    return new { Account = result.Account, MarkedAsCollected = result.Scanned, result.MaxUid };
}

static object Index(MailkeepServices services, string[] args, CancellationToken cancellationToken)
{
    if (args.Length >= 2 && args[1].Equals("attachments", StringComparison.OrdinalIgnoreCase))
    {
        var action = args.Length >= 3 ? args[2].ToLowerInvariant() : "status";
        if (action == "status") return services.Catalog.AttachmentStatus();
        if (action == "sync") return new
        {
            Scanned = services.Catalog.SyncAttachments(IntOption(args, "--max-items", 0) is > 0 and var maximum ? maximum : null,
                Report, cancellationToken),
            Status = services.Catalog.AttachmentStatus()
        };
        if (action == "estimate")
            return services.Catalog.EstimateAttachments(IntOption(args, "--sample", 1000), Report);
        if (action == "rebuild")
        {
            var extractor = RequiredOption(args, "--extractor");
            var affected = services.Catalog.RebuildAttachmentExtractor(extractor, execute: false);
            if (!args.Contains("--execute", StringComparer.OrdinalIgnoreCase))
                return new { ExecuteRequired = true, Extractor = extractor, Payloads = affected };
            services.Catalog.RebuildAttachmentExtractor(extractor, execute: true);
            return new
            {
                Extractor = extractor,
                PayloadsInvalidated = affected,
                RawBlobsScanned = services.Catalog.SyncAttachments(progress: Report, cancellationToken: cancellationToken),
                Status = services.Catalog.AttachmentStatus()
            };
        }
        if (action == "open")
        {
            if (args.Length < 4 || !long.TryParse(args[3], out var occurrenceId))
                throw new ArgumentException("index attachments open requires an occurrence id.");
            return new { occurrenceId, PreviewPath = services.Catalog.OpenAttachment(occurrenceId) };
        }
        throw new ArgumentException("index attachments accepts status, sync, estimate, rebuild, or open.");
    }
    if (args.Length < 2 || args[1].Equals("status", StringComparison.OrdinalIgnoreCase))
        return new
        {
            Sources = services.Catalog.Status(),
            Attachments = services.Catalog.AttachmentStatus(),
            Path = services.Paths.SearchCatalogPath,
        };
    if (args[1].Equals("sync", StringComparison.OrdinalIgnoreCase))
        return new
        {
            OperationalIndexed = services.Catalog.SyncOperational(),
            ArchiveIndexed = services.Catalog.SyncArchive(progress: Report),
            AttachmentsScanned = args.Contains("--attachments", StringComparer.OrdinalIgnoreCase)
                ? services.Catalog.SyncAttachments(progress: Report, cancellationToken: cancellationToken) : 0,
            Sources = services.Catalog.Status()
        };
    if (args[1].Equals("rebuild", StringComparison.OrdinalIgnoreCase))
        return new
        {
            OperationalIndexed = services.Catalog.SyncOperational(force: true),
            ArchiveIndexed = services.Catalog.SyncArchive(force: true, progress: Report),
            AttachmentsScanned = args.Contains("--attachments", StringComparer.OrdinalIgnoreCase)
                ? services.Catalog.SyncAttachments(progress: Report, cancellationToken: cancellationToken) : 0,
            Sources = services.Catalog.Status()
        };
    throw new ArgumentException("index accepts status, sync, or rebuild.");
}

/// <summary>Indexing a quarter of a million messages takes long enough that saying nothing looks like a hang.
/// It goes to stderr so stdout stays the JSON result and nothing else.</summary>
static void Report(CatalogProgress step) => Console.Error.WriteLine(
    step.Total > 0 ? $"index {step.Stage}: {step.Done:N0} of {step.Total:N0}" : $"index {step.Stage}: {step.Done:N0}");

static async Task<object> SendersAsync(MailkeepServices services, string[] args)
{
    var command = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1].ToLowerInvariant() : "list";
    if (command == "list") return services.Identities.Load().Select(SenderResult);

    if (args.Length < 3 || args[2].StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException($"senders {command} requires a sending identity id.");
    var identity = services.Identities.Require(args[2]);

    if (command == "default")
    {
        if (!args.Contains("--execute", StringComparer.OrdinalIgnoreCase))
            return new { ExecuteRequired = true, identity.Id, identity.Email };
        services.Identities.SetDefault(identity.Id);
        return SenderResult(services.Identities.Require(identity.Id));
    }
    if (command == "test")
    {
        RequireExecute(args);
        var verdict = await services.Send.TestAsync(identity.Id);
        if (!verdict.Ok) throw new InvalidOperationException($"{verdict.Email}: {verdict.Error}");
        return verdict;
    }
    if (command == "authorize")
    {
        if (!identity.SupportsOAuth)
            throw new InvalidOperationException($"{identity.Email} uses provider '{identity.Provider}'; set an SMTP password instead.");
        RequireExecute(args);
        var credential = await services.OAuth.AuthorizeAsync(identity.Provider, identity.Email, null, includeSmtp: true);
        var authorized = SendingIdentityStore.ResetTestIfConnectionChanged(identity,
            identity with { OAuth = credential, Password = null });
        services.Identities.Upsert(authorized);
        return SenderResult(services.Identities.Require(identity.Id));
    }
    if (command != "set") throw new ArgumentException($"senders accepts list, set, authorize, test, or default.");

    var updated = identity with
    {
        DisplayName = StringOption(args, "--display-name") ?? identity.DisplayName,
        ReplyTo = StringOption(args, "--reply-to") ?? identity.ReplyTo,
        Username = StringOption(args, "--username") ?? identity.Username,
        Smtp = new OutgoingServerSettings(
            StringOption(args, "--smtp-host") ?? identity.Smtp.Host,
            IntOption(args, "--smtp-port", identity.Smtp.Port),
            Security(StringOption(args, "--security")) ?? identity.Smtp.Security)
    };
    if (await Mailkeep.Cli.PasswordInput.ReadAsync(args, Console.In) is { Length: > 0 } password) updated = updated with { Password = password, OAuth = null };
    updated = SendingIdentityStore.ResetTestIfConnectionChanged(identity, updated);
    if (!args.Contains("--execute", StringComparer.OrdinalIgnoreCase))
        return new { ExecuteRequired = true, Current = SenderResult(identity), Proposed = SenderResult(updated) };
    services.Identities.Upsert(updated);
    return SenderResult(services.Identities.Require(identity.Id));
}

static string? Security(string? value) => value switch
{
    null => null,
    MailSecurityModes.SslOnConnect or MailSecurityModes.StartTls or MailSecurityModes.Auto or MailSecurityModes.None => value,
    _ => throw new ArgumentException($"--security accepts ssl_on_connect, starttls, auto, or none (got '{value}').")
};

static object SenderResult(SendingIdentity identity) => new
{
    identity.Id,
    identity.OeAccountId,
    identity.DisplayName,
    identity.Email,
    identity.ReplyTo,
    identity.IsDefault,
    identity.Provider,
    identity.Smtp,
    identity.Status,
    identity.LastTestError,
    identity.IsReady,
    Auth = identity.OAuth is not null ? "oauth2" : identity.Password is not null ? "password" : "none"
};

static object Discard(MailkeepServices services, string[] args)
{
    if (args.Length < 2) throw new ArgumentException("discard requires a draft id.");
    var draft = services.Database.GetOutgoing(args[1]) ?? throw new KeyNotFoundException("Draft not found.");
    if (draft.Status != OutgoingStates.Draft) throw new InvalidOperationException("Only a draft can be discarded.");
    if (!args.Contains("--execute", StringComparer.OrdinalIgnoreCase)) return new { ExecuteRequired = true, Draft = draft };
    return new { Discarded = services.Send.DiscardDraft(draft.Id), draft.Id };
}

static object Prepare(MailkeepServices services, string[] args)
{
    var identity = RequiredOption(args, "--identity");
    var to = Addresses(RequiredOption(args, "--to"));
    var cc = Addresses(StringOption(args, "--cc"));
    var bcc = Addresses(StringOption(args, "--bcc"));
    var subject = RequiredOption(args, "--subject");
    var html = HtmlBody(args);
    var body = Body(args, html);
    var attachments = AttachmentArguments.Parse(args);
    if (!args.Contains("--execute", StringComparer.OrdinalIgnoreCase))
        return new { ExecuteRequired = true, Identity = identity, To = to, Cc = cc, Bcc = bcc, Subject = subject, BodyLength = body.Length, HtmlLength = html?.Length ?? 0,
            Attachments = attachments.Select(Path.GetFileName) };
    return services.Send.SaveDraft(identity, to, subject, body, cc, bcc, attachmentPaths: attachments, bodyHtml: html);
}

static async Task<object> RecoverSendAsync(MailkeepServices services, string[] args)
{
    if (args.Length < 2) throw new ArgumentException("recover-send requires a message id.");
    var mark = args.Contains("--mark-sent", StringComparer.OrdinalIgnoreCase);
    var requeue = args.Contains("--return-to-outbox", StringComparer.OrdinalIgnoreCase);
    if (mark == requeue) throw new ArgumentException("Choose exactly one of --mark-sent or --return-to-outbox.");
    var message = services.Database.ListStaleSending().FirstOrDefault(message => message.Id == args[1])
        ?? throw new InvalidOperationException("No stale uncertain send with this id.");
    if (!args.Contains("--execute", StringComparer.OrdinalIgnoreCase))
        return new { ExecuteRequired = true, Warning = MailDatabase.UncertainSendWarning, Message = message, MarkAsSent = mark };
    services.Database.RecoverStaleSending(message.Id, mark);
    if (mark) await services.Send.CompleteAcceptedAsync(message.Id);
    return new { Recovered = true, Message = services.Database.GetOutgoing(message.Id) };
}

static async Task<object> QueueAsync(MailkeepServices services, string[] args)
{
    if (args.Length < 2) throw new ArgumentException("queue requires a draft id.");
    var draft = services.Database.GetOutgoing(args[1]) ?? throw new KeyNotFoundException("Draft not found.");
    if (!args.Contains("--execute", StringComparer.OrdinalIgnoreCase))
        return new { ExecuteRequired = true, Draft = draft };
    return await services.Send.QueueAsync(draft.Id);
}

static async Task<object> SendAsync(MailkeepServices services, string[] args)
{
    RequireExecute(args);
    var ids = args.Skip(1).Where(value => !value.Equals("--execute", StringComparison.OrdinalIgnoreCase)).ToArray();
    if (ids.Length > 1 || ids.Any(value => string.IsNullOrWhiteSpace(value) || value.StartsWith('-')))
        throw new ArgumentException("send accepts one optional message id and --execute.");
    var results = ids.Length == 0
        ? await services.Send.SendPendingAsync()
        : await services.Send.SendQueuedAsync(ids);
    var failures = results.Where(result => !result.Ok).ToArray();
    if (failures.Length > 0) throw new InvalidOperationException(string.Join("; ", failures.Select(result => $"{result.Id}: {result.Error}")));
    var catalogWarning = RefreshOperationalCatalog(services);
    return new { Sent = results.Count, Results = results, CatalogWarning = catalogWarning };
}

static async Task<object> SendReceiveAsync(MailkeepServices services, string[] args)
{
    RequireExecute(args);
    var sent = await services.Send.SendPendingAsync();
    if (sent.Any(result => !result.Ok))
        throw new InvalidOperationException(string.Join("; ", sent.Where(result => !result.Ok).Select(result => $"{result.Id}: {result.Error}")));
    var received = await services.Sync.SyncAllAsync(false);
    var failed = received.Where(result => !result.Ok).ToArray();
    if (failed.Length > 0)
        throw new InvalidOperationException(string.Join("; ", failed.Select(result => $"{result.Account}: {result.Error}")));
    var catalogWarning = RefreshOperationalCatalog(services);
    return new { Sent = sent.Count, Received = received.Sum(result => result.NewCount), SendResults = sent, SyncResults = received, CatalogWarning = catalogWarning };
}

static async Task<object> SendSelfAsync(MailkeepServices services, string[] args)
{
    RequireExecute(args);
    var identityId = RequiredOption(args, "--identity");
    var identity = services.Identities.Load().FirstOrDefault(value => value.Id.Equals(identityId, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Sending identity {identityId} not found.");
    var html = HtmlBody(args);
    var draft = services.Send.SaveDraft(identity.Id, [identity.Email], RequiredOption(args, "--subject"), Body(args, html), bodyHtml: html);
    await services.Send.QueueAsync(draft.Id);
    return await SendAsync(services, ["send", draft.Id, "--execute"]);
}

static object SearchResult(MessageSummary message) => new
{
    message.Id,
    message.Source,
    message.Account,
    message.Folder,
    message.Subject,
    message.Sender,
    message.SenderName,
    message.Recipients,
    message.Date,
    message.Size,
    message.Snippet,
    message.AttachmentCount,
    message.MessageRef,
    message.Locations,
    message.AttachmentMatch,
    ArchiveId = message.Source == MailContract.ArchiveSource ? message.SourceKey : null
};

static object MessageResult(MessageContent message) => new
{
    Message = SearchResult(message.Summary),
    Body = message.BodyText,
    BodyHtml = message.BodyHtml,
    message.RawPath,
    message.Attachments
};

/// <summary>Creates the account record and nothing else: no credential is granted here, so the mailbox stays
/// unreachable until someone authorizes it in the app. Sending likewise waits for its own test.</summary>
static object AddAccount(MailkeepServices services, string[] args)
{
    var address = RequiredOption(args, "--address");
    var provider = (StringOption(args, "--provider") ?? "custom").ToLowerInvariant();
    var local = !string.Equals(StringOption(args, "--mode"), "connected", StringComparison.OrdinalIgnoreCase);
    var protocol = (StringOption(args, "--protocol") ?? (local ? MailProtocols.Pop3 : MailProtocols.Imap)).ToLowerInvariant();
    var host = StringOption(args, "--host") ?? (provider is "gmail" or "outlook" or "microsoft"
        ? OAuthClient.DefaultHost(provider == "microsoft" ? "outlook" : provider, protocol)
        : throw new ArgumentException("--host is required for a custom provider."));
    var port = IntOption(args, "--port", protocol == MailProtocols.Pop3 ? 995 : 993);
    var name = StringOption(args, "--name") ?? (local ? "local:" : "connected:") + address.ToLowerInvariant();
    var account = new MailAccount(name, provider, address, "INBOX", local ? LocalFolderKinds.Inbox : address,
        null, null, null, new KeepAliveSettings(), local ? MailboxModes.LocalKeep : MailboxModes.Connected,
        new IncomingServerSettings(protocol, host, port, StringOption(args, "--security") ?? MailSecurityModes.SslOnConnect));

    if (services.Accounts.Load().Any(value => value.Address.Equals(address, StringComparison.OrdinalIgnoreCase)
        && value.EffectiveMailboxMode == account.EffectiveMailboxMode))
        throw new ArgumentException($"{address} already has a {(local ? "Local Mail" : "Connected")} account.");
    if (!args.Contains("--execute", StringComparer.OrdinalIgnoreCase))
        return new { ExecuteRequired = true, Account = account.Name, account.Address, MailboxMode = account.EffectiveMailboxMode, account.Incoming };
    services.Accounts.Upsert(account);
    return new { Added = account.Name, account.Address, MailboxMode = account.EffectiveMailboxMode, account.Incoming, NeedsAuthorization = true };
}

/// <summary>Sets how often an unattended pass collects one account, or takes it off the timer.</summary>
static object SetKeepAlive(MailkeepServices services, string[] args)
{
    var name = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : throw new ArgumentException("accounts-keepalive needs an account name.");
    var account = services.Accounts.Load().FirstOrDefault(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Account {name} was not found.");
    var hours = IntOption(args, "--hours", account.EffectiveKeepAlive.IntervalHours);
    if (hours < 1) throw new ArgumentException("--hours must be at least 1.");
    var keepAlive = new KeepAliveSettings(!args.Contains("--off", StringComparer.OrdinalIgnoreCase), hours);
    if (!args.Contains("--execute", StringComparer.OrdinalIgnoreCase))
        return new { ExecuteRequired = true, Account = account.Name, KeepAlive = keepAlive };
    services.Accounts.SetKeepAlive(account.Name, keepAlive.Enabled, keepAlive.IntervalHours);
    return new { Updated = account.Name, KeepAlive = keepAlive };
}

static string? StringOption(string[] values, string name)
{
    var index = Array.FindIndex(values, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
}

static int IntOption(string[] values, string name, int fallback)
    => int.TryParse(StringOption(values, name), out var result) ? result : fallback;

static SearchMatchScope MatchScope(string? value) => value?.ToLowerInvariant() switch
{
    null or "all" => SearchMatchScope.All,
    "message" => SearchMatchScope.Message,
    "attachment-name" => SearchMatchScope.AttachmentName,
    "attachment-content" => SearchMatchScope.AttachmentContent,
    _ => throw new ArgumentException("--match accepts all, message, attachment-name, or attachment-content.")
};

static string RequiredOption(string[] values, string name)
    => StringOption(values, name) is { Length: > 0 } value ? value : throw new ArgumentException($"{name} is required.");

static string[] Addresses(string? value) => string.IsNullOrWhiteSpace(value) ? []
    : value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

/// <summary>The plain-text body: given directly, read from a file, or, when only HTML was given, the words of
/// that HTML, so a plain-text reader still gets them.</summary>
static string Body(string[] values, string? html = null)
{
    if (StringOption(values, "--body-file") is { } path) return File.ReadAllText(Path.GetFullPath(path));
    if (StringOption(values, "--body") is { } body) return body;
    if (html is not null) return MailText.FromHtml(html);
    return RequiredOption(values, "--body");
}

static string? HtmlBody(string[] values)
    => StringOption(values, "--html-file") is { } path ? File.ReadAllText(Path.GetFullPath(path)) : null;

static object FolderManifest(string location, bool execute, AppPaths paths)
{
    ArchiveMoveBack.EnsureNoPending(location);
    var inventory = ArchiveManifest.Capture(location);
    var root = Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var files = inventory.Folders.Select(entry => ArchiveManifest.ResolveFile(location, entry.File)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    // Upgrade older archives only after checking that the mailbox's retained receipts don't name a missing file.
    if (File.Exists(paths.DatabasePath))
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = paths.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='archived_messages'";
        if (Convert.ToInt64(command.ExecuteScalar()) > 0)
        {
            command.CommandText = "SELECT DISTINCT archive_file FROM archived_messages";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var file = Path.GetFullPath(reader.GetString(0));
                if (file.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !files.Contains(file))
                    throw new InvalidDataException("An archive receipt names a missing folder file. Inventory not written: " + file);
            }
        }
    }
    if (execute) ArchiveManifest.Save(location, inventory);
    return new { Executed = execute, Inventory = inventory };
}

static object FolderList(string file, int limit, bool includeDeleted)
{
    using var bundle = FolderBundle.Open(file);
    var messages = bundle.List(includeDeleted, limit);
    return new { Folder = bundle.Folder(), Shown = messages.Count, Messages = messages };
}

static object FolderGet(string file, long seq, string? rawPath)
{
    using var bundle = FolderBundle.Open(file);
    var message = bundle.Get(seq) ?? throw new ArgumentException($"No message {seq} in {file}.");
    if (rawPath is not null) File.WriteAllBytes(Path.GetFullPath(rawPath), message.Raw);
    return new { message.Summary, RawWrittenTo = rawPath is null ? null : Path.GetFullPath(rawPath) };
}

static FolderBundle.VerifyResult FolderVerify(string file, CancellationToken cancellationToken)
{
    using var bundle = FolderBundle.Open(file);
    return bundle.Verify(cancellationToken);
}

static object FolderRebuild(string file)
{
    using var bundle = FolderBundle.Open(file, readOnly: false);
    return new { Rebuilt = bundle.RebuildCaches(), Verify = bundle.Verify() };
}

static void RequireExecute(string[] values)
{
    if (!values.Contains("--execute", StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException("This write requires --execute.");
}

static string[] WithoutGlobalOptions(string[] values)
{
    var result = new List<string>();
    for (var index = 0; index < values.Length; index++)
    {
        if (values[index] is "--root")
        {
            index++;
            continue;
        }
        result.Add(values[index]);
    }
    return result.ToArray();
}
