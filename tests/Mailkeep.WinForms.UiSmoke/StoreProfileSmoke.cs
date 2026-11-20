using System.Diagnostics;
using Mailkeep.Core;
using Mailkeep.Infrastructure;

/// <summary>Times the database reads the client makes on its UI thread after a delete or a filing, against a store
/// given by --root (the real one, to measure what the reader actually waits for). Read-only: it never initialises
/// the store or calls anything that writes, so pointing it at live mail is safe.</summary>
internal static class StoreProfileSmoke
{
    public static int Run(string root, int rounds)
    {
        var services = new MailkeepServices(root);
        if (!File.Exists(services.Paths.DatabasePath)) throw new FileNotFoundException("No store at " + services.Paths.DatabasePath);
        string[] Scoped() => services.Accounts.Load().Where(value => !value.IsConnected).Select(value => value.Name)
            .Concat(services.Identities.Load().Select(identity => identity.Id))
            .Append(MailContract.LocalCopyAccount)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var steps = new (string Name, Func<object?> Run)[]
        {
            ("LocalScopedAccounts (DPAPI)", () => Scoped()),
            ("LocalFolderCounts", () => services.Database.LocalFolderCounts(Scoped())),
            ("VisibleArchiveFolderCounts", () => services.Database.VisibleArchiveFolderCounts(Scoped())),
            ("ArchiveUnreadCounts", () => services.Database.ArchiveUnreadCounts(Scoped())),
            ("FolderCounts(archive)", () => services.Database.FolderCounts(MailContract.ArchiveSource)),
            ("AccountMessageCounts", () => services.Database.AccountMessageCounts()),
            ("AccountLatestDates", () => services.Database.AccountLatestDates()),
            ("ListHeads(live)", () => services.Database.ListHeads(MailContract.LiveSource)),
            ("ListLocalFolders", () => services.Database.ListLocalFolders()),
        };
        foreach (var round in Enumerable.Range(1, rounds))
            foreach (var (name, run) in steps)
            {
                var clock = Stopwatch.StartNew();
                var result = run();
                clock.Stop();
                var size = result is System.Collections.ICollection collection ? collection.Count : -1;
                Console.WriteLine($"round={round} step=\"{name}\" ms={clock.Elapsed.TotalMilliseconds:F1} rows={size}");
            }
        // The two answers must agree folder for folder, or the faster one is not a replacement.
        var slow = services.Database.VisibleArchiveFolderCounts(Scoped());
        var fast = services.Database.ArchiveUnreadCounts(Scoped());
        var mismatches = slow.Where(pair => fast.GetValueOrDefault(pair.Key) != pair.Value.Unread)
            .Select(pair => $"{pair.Key}: slow={pair.Value.Unread} fast={fast.GetValueOrDefault(pair.Key)}")
            .Concat(fast.Where(pair => pair.Value != 0 && slow.GetValueOrDefault(pair.Key).Unread != pair.Value)
                .Select(pair => $"{pair.Key}: fast-only={pair.Value}"))
            .Distinct().ToArray();
        Console.WriteLine($"unread-equivalence folders={slow.Count} unread_total={slow.Values.Sum(value => value.Unread)} mismatches={mismatches.Length}");
        foreach (var mismatch in mismatches.Take(20)) Console.WriteLine("  mismatch " + mismatch);
        return mismatches.Length == 0 ? 0 : 1;
    }
}
