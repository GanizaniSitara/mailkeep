using Mailkeep.Core;

namespace Mailkeep.WinForms;

internal sealed partial class MainForm
{
    private bool _changingReadState;

    private ToolStripMenuItem ReadStateCommand(bool isRead, bool shortcut = false)
        => new(isRead ? "Mark as &Read" : "Mark as &Unread", null, async (_, _) => await MarkSelectionReadAsync(isRead))
        {
            AccessibleName = isRead ? "Mark as Read" : "Mark as Unread",
            ShortcutKeyDisplayString = shortcut ? (isRead ? "Ctrl+Q" : "Ctrl+Shift+U") : ""
        };

    private bool CanChangeReadState(bool isRead)
    {
        var selected = Selected();
        return _ready && !_changingReadState && selected.Length > 0
            && selected.All(message => message.Source == MailContract.LiveSource)
            && selected.Any(message => message.IsRead != isRead);
    }

    private void EnableMessageReadMenu()
    {
        var menu = new ContextMenuStrip();
        var read = ReadStateCommand(true);
        var unread = ReadStateCommand(false);
        menu.Items.AddRange([read, unread]);
        menu.Opening += (_, _) => { read.Enabled = CanChangeReadState(true); unread.Enabled = CanChangeReadState(false); };
        _messages.ContextMenuStrip = menu;
        _messages.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || _messages.GetItemAt(e.X, e.Y) is not { } row || row.Selected) return;
            ListSelection.ClearSelection(_messages);
            row.Selected = true;
        };
    }

    private async Task MarkSelectionReadAsync(bool isRead)
    {
        if (!CanChangeReadState(isRead)) return;
        var chosen = Selected();
        _changingReadState = true;
        _status.Text = isRead ? "Marking messages as read…" : "Marking messages as unread…";
        try
        {
            var changed = await Task.Run(() => _services.Sync.SetReadManyAsync(chosen, isRead));
            if (!IsDisposed) _status.Text = $"{changed:N0} messages marked as {(isRead ? "read" : "unread")}";
        }
        catch (Exception error) { if (!IsDisposed) ShowError("Could not change read state", error); }
        finally
        {
            _changingReadState = false;
            if (!IsDisposed) _ = RefreshReadStatesAsync(chosen.Select(message => message.Id).ToArray());
        }
    }

    /// <summary>Marks one message read because it was read, with none of the ceremony of the command: no
    /// status line, and a failure is not worth a dialog over a message that is plainly on screen.</summary>
    private async Task MarkReadQuietlyAsync(MessageSummary message)
    {
        if (_changingReadState || message.IsRead) return;
        try { await Task.Run(() => _services.Sync.SetReadManyAsync([message], true)); }
        catch (Exception) { return; }
        if (!IsDisposed) await RefreshReadStatesAsync([message.Id]);
    }

    /// <summary>Which count refresh is the latest. One started after another supersedes it, so a slow refresh
    /// that finishes late never paints older counts over newer ones.</summary>
    private int _readCountsGeneration;

    private sealed record ReadCounts(IReadOnlyDictionary<long, bool> States,
        IReadOnlyDictionary<string, (int Total, int New)>? AccountCounts,
        IReadOnlyDictionary<string, (int Total, int Unread)> Local,
        IReadOnlyDictionary<(string Account, string Folder), int> Archive,
        IReadOnlyDictionary<string, int>? AccountUnread = null);

    /// <summary>Everything a count refresh reads, and nothing it draws, so it can run off the window's thread.</summary>
    private ReadCounts LoadReadCounts(long[] ids, bool includeAccounts, string[] scoped)
    {
        var archive = _services.Database.ArchiveUnreadCounts(scoped);
        // Kept for writing down at close, so the next start opens on these.
        _archiveUnreadCounts = archive;
        return new(_services.Database.ReadStates(ids),
            includeAccounts ? _services.Database.AccountMessageCounts() : null,
            _services.Database.LocalFolderCounts(scoped), archive,
            includeAccounts ? _services.Database.AccountUnreadCounts() : null);
    }

    /// <summary>The Connected list is redrawn only when a Connected account could have changed: redrawing it for
    /// every local message read made the whole left-hand side flicker.</summary>
    private bool TouchesConnected(long[] ids) => _rows.OfType<MessageSummary>().Any(message => ids.Contains(message.Id)
        && _loadedAccounts.Any(account => account.IsConnected && account.Name.Equals(message.Account, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Brings the rows, the folder counts and the account badges up to date after read states or
    /// folder contents changed, without rebuilding anything: the tree keeps its nodes and the list its rows.
    /// This one waits for the answer, for the tree's builder, which must not show its raw counts even briefly.</summary>
    private void RefreshReadStates(long[] ids)
    {
        ++_readCountsGeneration;
        ApplyReadCounts(LoadReadCounts(ids, TouchesConnected(ids), LocalScopedAccounts()));
    }

    /// <summary>The same, with the counting done off the window's thread. Counting the archive's unread mail is
    /// a read of the whole import; done on the window's thread after every delete and every message read, it
    /// froze the window for over a second each time — the delay felt on pressing Delete.</summary>
    private async Task RefreshReadStatesAsync(long[] ids)
    {
        var generation = ++_readCountsGeneration;
        var includeAccounts = TouchesConnected(ids);
        ReadCounts counts;
        try { counts = await Task.Run(() => LoadReadCounts(ids, includeAccounts, LocalScopedAccounts())); }
        catch (Exception) { return; } // the counts stay as they were; the next refresh brings them right
        if (IsDisposed || generation != _readCountsGeneration) return;
        ApplyReadCounts(counts);
    }

    /// <summary>Counts are set on <paramref name="nodes"/> when given: a tree still being built takes them as
    /// plain values, where a tree on screen redraws and re-measures for every label and font changed — over
    /// half a second for a whole tree, all of it on the window's thread.</summary>
    private void ApplyReadCounts(ReadCounts counts, TreeNodeCollection? nodes = null)
    {
        var states = counts.States;
        if (states.Count > 0)
        {
            _rows = _rows.Select(row => row is MessageSummary message && states.TryGetValue(message.Id, out var isRead)
                ? message with { IsRead = isRead } : row).ToArray();
            RedrawMessageList();
        }
        if (_remoteLocalWrites.Count == 0 && counts.AccountCounts is { } accountCounts)
        {
            _accountCounts = accountCounts;
            if (counts.AccountUnread is { } unread) _accountUnreadCounts = unread;
            PopulateAccountList();
        }
        UpdateMessageCount();
        var local = counts.Local;
        var archive = counts.Archive;
        RefreshNodes(nodes ?? _localTree.Nodes);
        if (nodes is null) _localTree.Invalidate();

        void RefreshNodes(TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes)
            {
                var unread = node.Tag switch
                {
                    LocalFolder folder => local.GetValueOrDefault(folder.Key).Unread,
                    MailFolder folder => archive.GetValueOrDefault((folder.Account, folder.ArchiveFolder))
                        + (folder.LocalKey is { } key ? local.GetValueOrDefault(key).Unread : 0),
                    _ => -1
                };
                if (unread >= 0)
                {
                    // Only a change is applied: a new font or label on every node redrew the whole tree for
                    // every message read, whether or not any count had moved.
                    var bold = unread > 0;
                    if (bold != (node.NodeFont?.Bold == true))
                    {
                        node.NodeFont?.Dispose();
                        node.NodeFont = bold ? new Font(_localTree.Font, FontStyle.Bold) : null;
                    }
                    // The count beside the name is the unread count, except for Drafts and the Outbox, whose
                    // count is the mailbox's own queued mail and is left to the tree's builder.
                    var queued = node.Tag is LocalFolder { Key: LocalFolderKinds.Drafts or LocalFolderKinds.Outbox }
                        or MailFolder { LocalKey: LocalFolderKinds.Drafts or LocalFolderKinds.Outbox };
                    if (!queued)
                    {
                        var name = node.Text.Split("  (")[0];
                        var text = unread > 0 ? $"{name}  ({unread})" : name;
                        if (node.Text != text) node.Text = text;
                    }
                }
                RefreshNodes(node.Nodes);
            }
        }
    }
}
