using Mailkeep.Core;
using Mailkeep.Infrastructure;

namespace Mailkeep.WinForms;

/// <summary>Making and moving folders, from the tree's own context menu, the Folder menu, and by dragging.
///
/// The two kinds of folder behave differently underneath and the UI has to respect that. A Local Mail folder
/// is a row of its own, so it can be created empty, renamed and deleted. An imported folder is nothing but the
/// path its messages carry, so there is no such thing as an empty one: it is created by moving something into
/// it, and moving it rewrites those paths for the folder and everything beneath it.</summary>
internal sealed partial class MainForm
{
    /// <summary>The folder a drop would land in, drawn as the drop mark. Not the selection: see EnableFolderDragging.</summary>
    private TreeNode? _dropTarget;
    private bool _folderChanging;

    private bool BeginFolderChange()
    {
        if (_folderChanging || _archiveTransferInProgress) { _status.Text = "A folder change is still being saved…"; return false; }
        _folderChanging = true;
        return true;
    }

    private ContextMenuStrip BuildFolderMenu()
    {
        var menu = new ContextMenuStrip { AccessibleName = "Folder menu" };
        var newFolder = new ToolStripMenuItem("&New Folder…", null, (_, _) => NewFolder()) { AccessibleName = "New Folder" };
        var rename = new ToolStripMenuItem("&Rename…", null, (_, _) => RenameFolder()) { AccessibleName = "Rename Folder" };
        var moveInto = new ToolStripMenuItem("&Move into…", null, (_, _) => MoveFolderInto()) { AccessibleName = "Move Folder Into" };
        var delete = new ToolStripMenuItem("&Delete", null, (_, _) => DeleteFolder()) { AccessibleName = "Delete Folder" };
        var archive = new ToolStripMenuItem("Move to &Archive…", null, async (_, _) => await MoveFolderToArchiveAsync()) { AccessibleName = "Move Folder to Archive" };
        menu.Items.AddRange([newFolder, rename, moveInto, archive, new ToolStripSeparator(), delete]);
        menu.Opening += (_, _) =>
        {
            // Right-clicking a folder should act on that folder, not on whatever was selected before.
            if (_localTree.GetNodeAt(_localTree.PointToClient(Cursor.Position)) is { } node) _localTree.SelectedNode = node;
            var target = SelectedFolder();
            newFolder.Enabled = target.CanHoldNewFolder;
            rename.Enabled = target.CanRename;
            moveInto.Enabled = target.CanMove;
            delete.Enabled = target.CanDelete;
            archive.Enabled = ArchiveKey(target) is not null;
        };
        return menu;
    }

    /// <summary>What the selected node is, and therefore what may be done to it.</summary>
    private FolderTarget SelectedFolder()
    {
        var node = _localTree.SelectedNode;
        return node?.Tag switch
        {
            LocalFolder folder => new FolderTarget(Local: folder, Archive: null,
                CanHoldNewFolder: true, CanRename: folder.Kind == LocalFolderKinds.User,
                CanMove: folder.Kind == LocalFolderKinds.User, CanDelete: folder.Kind == LocalFolderKinds.User),
            MailFolder archived when archived.ArchiveFolder.Length > 0 => new FolderTarget(null, archived,
                CanHoldNewFolder: true, CanRename: true, CanMove: true, CanDelete: true),
            _ => new FolderTarget(null, null, CanHoldNewFolder: true, false, false, false)
        };
    }

    /// <summary>Creates the folder straight away and opens its name for editing, the way Explorer does —
    /// no dialog to fill in first, and a name that is already valid if the user simply presses Enter.</summary>
    private async void NewFolder()
    {
        if (!RequireReady()) return;
        if (!BeginFolderChange()) return;
        var target = SelectedFolder();
        var parentNode = target.Local is null && target.Archive is null ? _localTree.Nodes.Cast<TreeNode>().FirstOrDefault() : _localTree.SelectedNode;
        try
        {
            LocalFolder? newLocal = null;
            string name;
            string findBy;
            if (target.Archive is { LocalKey: { } beneath })
            {
                // Inside a folder the mailbox shares with the import: the new folder is the mailbox's own.
                var created = await Task.Run(() => _services.Database.CreateLocalFolder(UnusedFolderName(beneath), beneath));
                newLocal = created;
                name = created.Name;
                findBy = "LocalFolder:" + created.Key;
            }
            else if (target.Archive is { } archived)
            {
                // Inside an imported folder: declare the path, so the folder exists before anything is in it.
                name = await Task.Run(() => UnusedArchiveName(archived.Account, archived.ArchiveFolder));
                await Task.Run(() => _services.Database.CreateArchiveFolder(MailContract.ArchiveSource, archived.Account,
                    archived.ArchiveFolder + "/" + name));
                findBy = name;
            }
            else
            {
                var created = await Task.Run(() => _services.Database.CreateLocalFolder(UnusedFolderName(target.Local?.Key), target.Local?.Key));
                newLocal = created;
                name = created.Name;
                findBy = "LocalFolder:" + created.Key;
            }
            if (newLocal is not null && parentNode?.TreeView == _localTree)
            {
                var child = new TreeNode(name) { Name = findBy, Tag = newLocal, ImageKey = FolderIcon(newLocal.Kind), SelectedImageKey = FolderIcon(newLocal.Kind) };
                InsertUserFolder(parentNode, child, name);
            }
            else await ReloadAccountsAsync();
            if (FindFolderNode(findBy, name) is { } node)
            {
                node.EnsureVisible();
                _localTree.SelectedNode = node;
                _localTree.Focus();
                node.BeginEdit();
            }
            _status.Text = $"Created {name} — type a name, or press Enter to keep it";
        }
        catch (Exception exception) { ShowError("Could not create the folder", exception); }
        finally { _folderChanging = false; }
    }

    /// <summary>Local folders are found by their key; an imported one has no key, so it is found by the label
    /// the tree just drew for it.</summary>
    private TreeNode? FindFolderNode(string key, string name)
    {
        if (_localTree.Nodes.Find(key, true).FirstOrDefault() is { } byKey) return byKey;
        return Walk(_localTree.Nodes).FirstOrDefault(node => node.Text.Split("  (")[0] == name);

        static IEnumerable<TreeNode> Walk(TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes)
            {
                yield return node;
                foreach (var nested in Walk(node.Nodes)) yield return nested;
            }
        }
    }

    private string UnusedArchiveName(string account, string parentFolder)
    {
        var taken = _services.Database.FolderCounts(MailContract.ArchiveSource)
            .Where(folder => folder.Account == account && folder.Folder.StartsWith(parentFolder + "/", StringComparison.Ordinal))
            .Select(folder => folder.Folder[(parentFolder.Length + 1)..].Split('/')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        const string basis = "New Folder";
        if (!taken.Contains(basis)) return basis;
        for (var index = 2; index < 1000; index++)
            if (!taken.Contains($"{basis} ({index})")) return $"{basis} ({index})";
        return $"{basis} ({Guid.NewGuid().ToString("n")[..6]})";
    }

    /// <summary>"New Folder", then "New Folder (2)" and so on, counting only the folders that share a parent —
    /// two different folders may each hold a New Folder without either being renamed.</summary>
    private string UnusedFolderName(string? parentKey)
    {
        var taken = _services.Database.ListLocalFolders()
            .Where(folder => folder.ParentKey == parentKey)
            .Select(folder => folder.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        const string basis = "New Folder";
        if (!taken.Contains(basis)) return basis;
        for (var index = 2; index < 1000; index++)
            if (!taken.Contains($"{basis} ({index})")) return $"{basis} ({index})";
        return $"{basis} ({Guid.NewGuid().ToString("n")[..6]})";
    }

    /// <summary>Renaming in place, by clicking a selected folder or pressing F2, as everywhere else in Windows.</summary>
    private void EnableFolderRenaming()
    {
        _localTree.LabelEdit = true;
        _localTree.BeforeLabelEdit += (_, e) =>
        {
            // Only the folders the user owns may be renamed; the standard ones keep their names.
            var editable = e.Node?.Tag switch
            {
                LocalFolder { Kind: LocalFolderKinds.User } => true,
                MailFolder { ArchiveFolder.Length: > 0 } => true,
                _ => false
            };
            e.CancelEdit = !editable;
        };
        _localTree.AfterLabelEdit += (_, e) =>
        {
            var name = e.Label?.Trim();
            if (e.Node is null || name is null) return;          // cancelled with Escape
            if (name.Length == 0 || name.Contains('/'))
            {
                e.CancelEdit = true;
                _status.Text = "A folder name cannot be empty or contain '/'.";
                return;
            }
            // The tree redraws from the database, so let the reload supply the label rather than the edit.
            e.CancelEdit = true;
            ApplyFolderRename(e.Node, name);
        };
        _localTree.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F2 && _localTree.SelectedNode is { } node) { node.BeginEdit(); e.Handled = true; }
            if (e.KeyCode == Keys.Delete && SelectedFolder().CanDelete) { DeleteFolder(); e.Handled = true; }
        };
    }

    private async void ApplyFolderRename(TreeNode node, string name)
    {
        if (!BeginFolderChange()) return;
        try
        {
            switch (node.Tag)
            {
                case LocalFolder { Kind: LocalFolderKinds.User } local:
                    if (local.Name == name) return;
                    if (await Task.Run(() => _services.Database.RenameLocalFolder(local.Key, name)) == 0)
                        throw new InvalidOperationException("This folder no longer exists.");
                    if (node.TreeView == _localTree && node.Parent is { } localParent)
                    {
                        var suffix = node.Text.StartsWith(local.Name, StringComparison.Ordinal) ? node.Text[local.Name.Length..] : "";
                        var selected = _localTree.SelectedNode;
                        node.Text = name + suffix;
                        node.Tag = local with { Name = name };
                        node.Remove();
                        InsertUserFolder(localParent, node, name);
                        if (selected?.TreeView == _localTree) _localTree.SelectedNode = selected;
                        _status.Text = $"Renamed to {name}";
                        return;
                    }
                    break;
                case MailFolder archived when archived.ArchiveFolder.Length > 0:
                    var leaf = archived.ArchiveFolder.Split('/')[^1];
                    var parent = archived.ArchiveFolder[..Math.Max(0, archived.ArchiveFolder.Length - leaf.Length)];
                    await Task.Run(() => _services.Database.MoveArchiveFolder(MailContract.ArchiveSource, archived.Account,
                        archived.ArchiveFolder, parent + name));
                    break;
                default: return;
            }
            await ReloadAccountsAsync();
            _status.Text = $"Renamed to {name}";
        }
        catch (Exception exception) { ShowError("Could not rename the folder", exception); }
        finally { _folderChanging = false; }
    }

    private static void InsertUserFolder(TreeNode parent, TreeNode child, string name)
    {
        var index = 0;
        foreach (TreeNode sibling in parent.Nodes)
        {
            var siblingName = sibling.Tag switch { LocalFolder f => f.Name, MailFolder f => f.Display, _ => sibling.Text };
            var pinned = parent.Level == 0 && (sibling.Tag is LocalFolder { Kind: not LocalFolderKinds.User }
                || sibling.Tag is MailFolder m && StandardFolderKey(m.Display) is not null);
            if (!pinned && StringComparer.InvariantCulture.Compare(name, siblingName) < 0) break;
            index++;
        }
        parent.Nodes.Insert(index, child);
    }

    /// <summary>The menu's Rename simply starts the same in-place edit the tree offers, rather than opening a
    /// dialog that does the same job differently.</summary>
    private void RenameFolder()
    {
        if (!RequireReady() || !SelectedFolder().CanRename) return;
        _localTree.SelectedNode?.BeginEdit();
    }

    /// <summary>Moves a folder under another by name, which is what makes "put these four inside Projects"
    /// a single step rather than four drags.</summary>
    private async void MoveFolderInto()
    {
        if (!RequireReady()) return;
        var target = SelectedFolder();
        if (!target.CanMove) return;
        if (!BeginFolderChange()) return;
        try
        {
            if (target.Local is { } local)
            {
                // Anything but itself and its own descendants, which it cannot be put inside.
                var allFolders = await Task.Run(_services.Database.ListLocalFolders);
                var descendants = Descendants(local.Key, allFolders);
                var choices = allFolders
                    .Where(folder => folder.Key != local.Key && !descendants.Contains(folder.Key))
                    .ToArray();
                using var dialog = new FolderChoiceForm(choices, allFolders, movingFolder: true);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var destination = dialog.Chosen?.Key;
                await Task.Run(() => _services.Database.MoveLocalFolder(local.Key, destination));
            }
            else if (target.Archive is { } archived)
            {
                var leaf = archived.ArchiveFolder.Split('/')[^1];
                if (Ask("Move into", $"Move {leaf} into which folder? (blank for the top level)", "") is not { } into) return;
                var destination = into.Trim().Trim('/');
                await Task.Run(() => _services.Database.MoveArchiveFolder(MailContract.ArchiveSource, archived.Account,
                    archived.ArchiveFolder, destination.Length == 0 ? leaf : destination + "/" + leaf));
            }
            else return;
            await ReloadAccountsAsync();
            _status.Text = "Folder moved";
        }
        catch (Exception exception) { ShowError("Could not move the folder", exception); }
        finally { _folderChanging = false; }
    }

    private async void DeleteFolder()
    {
        if (!RequireReady()) return;
        var target = SelectedFolder();
        var name = target.Local?.Name ?? target.Archive?.Display;
        if (!target.CanDelete || name is null) return;
        if (MessageBox.Show(this, $"Delete the folder {name}?", "Delete Folder",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        if (!BeginFolderChange()) return;
        var deletedKey = FolderKey(_localTree.SelectedNode!);
        _status.Text = $"Deleting {name}…";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var pendingFiling = _filing;
            if (!pendingFiling.IsCompleted)
            {
                _status.Text = "Finishing the message change before deleting the folder…";
                try { await pendingFiling; }
                catch
                {
                    if (!IsDisposed) _status.Text = "Folder not deleted because the preceding message change failed.";
                    return;
                }
                if (IsDisposed || Disposing) return;
                TimingLog.MarkBackground($"folder delete: waited for message change {clock.ElapsedMilliseconds} ms");
                clock.Restart();
                _status.Text = $"Deleting {name}…";
            }
            if (target.Local is { } local)
                await Task.Run(() => _services.Database.DeleteLocalFolder(local.Key));
            else if (target.Archive is { } archived)
                await Task.Run(() => _services.Database.DeleteArchiveFolder(MailContract.ArchiveSource, archived.Account, archived.ArchiveFolder));
            if (IsDisposed || Disposing) return;
            // The deletion invalidates the saved archive shape in the store. Do
            // not write the pre-delete in-memory snapshot back when closing.
            _archiveFolderCounts = null;
            _archiveUnreadCounts = null;
            TimingLog.Mark($"folder delete: saved {clock.ElapsedMilliseconds} ms");
            clock.Restart();
            // Both commands remove a leaf. Keep the surviving nodes and select a
            // neighbour before removing it; rebuilding the tree falls back to Inbox.
            {
                ++_sidebarReloadGeneration;
                var deletedNode = Walk(_localTree.Nodes).FirstOrDefault(node => FolderKey(node) == deletedKey);
                if (deletedNode is not null)
                {
                    _expandedFolders.Remove(FolderKey(deletedNode));
                    if (_localTree.SelectedNode == deletedNode)
                        _localTree.SelectedNode = deletedNode.NextNode ?? deletedNode.PrevNode ?? deletedNode.Parent;
                    deletedNode.Remove();
                }
                if (_ready) await RefreshFindFoldersAsync();
            }
            TimingLog.Mark($"folder delete: refreshed {clock.ElapsedMilliseconds} ms");
            _status.Text = $"Deleted {name}";
        }
        catch (Exception exception) { ShowError("Could not delete the folder", exception); }
        finally { _folderChanging = false; }
    }

    /// <summary>The database key of a folder that can be moved to the archive: a user folder, or a folder from the
    /// import that has its own row. Standard folders and import-only paths cannot.</summary>
    private static string? ArchiveKey(FolderTarget target) => target switch
    {
        { Local: { Kind: LocalFolderKinds.User } local } => local.Key,
        { Archive: { LocalKey: { } key } } => key,
        _ => null
    };

    /// <summary>Right-click > Move to Archive: the folder leaves the tree and normal search and stays readable in
    /// Tools > Archive (docs/folder-store-redesign.md, decided 2026-10-23). The same steps as 'mailkeep folder
    /// archive': its file is written and checked message by message, then its rows leave the database, each
    /// recorded. The whole-mailbox backup is offered, ticked.</summary>
    private async Task MoveFolderToArchiveAsync()
    {
        if (!RequireReady() || _archiveTransferInProgress) return;
        if (!BeginFolderChange()) return;
        try
        {
            var target = SelectedFolder();
            if (ArchiveKey(target) is not { } key) return;
            var location = Path.Combine(_services.Paths.Root, "archive");
            Directory.CreateDirectory(location);
            var archive = new FolderArchive(_services.Paths);
            FolderArchive.Result preview;
            try { preview = await Task.Run(() => archive.Move(key, location, execute: false)); }
            catch (Exception error) { ShowError("Could not move the folder to the archive", error); return; }
            var plan = preview.Plan;
            if (plan.Refusals.Length > 0)
            {
                MessageBox.Show(this, $"{plan.FolderPath} cannot be moved to the archive: {string.Join("; ", plan.Refusals)}.",
                    "Move to Archive", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using var confirm = new ArchiveConfirmForm(plan.FolderPath, plan.Visible);
            if (confirm.ShowDialog(this) != DialogResult.OK) return;
            var backup = confirm.Backup;
            FolderArchive.Result? done = null;
            await RunBusyAsync(null, backup ? $"Backing up, then moving {plan.FolderPath} to the archive…" : $"Moving {plan.FolderPath} to the archive…",
                async () => done = await Task.Run(() => archive.Move(key, location, execute: true, backup: backup, pruneEmptyParents: true)));
            if (done is null) return;
            Archives.Reload();
            if (_archiveWindow is { IsDisposed: false } window) window.Reload();
            await ReloadAccountsAsync();
            // After the list has loaded, or loading it would overwrite the message straight away.
            await LoadMessagesAsync();
            try
            {
                await Task.Run(() => { _services.Catalog.SyncOperational(); _services.Catalog.SyncArchive(); });
            }
            catch (Exception error) { ShowError("Folder archived, but search refresh failed", error); }
            var moved = $"Moved {done.Plan.Visible:N0} messages from {plan.FolderPath} to the archive. They are in Tools > Archive.";
            _status.Text = moved;
            // Said in a box as well: the status bar is rewritten as the reloaded folder list settles.
            MessageBox.Show(this, moved, "Moved to Archive", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally { _folderChanging = false; }
    }

    /// <summary>Every folder beneath this one, so a folder is never offered a home inside itself.</summary>
    private static HashSet<string> Descendants(string folderKey, IReadOnlyList<LocalFolder> all)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>([folderKey]);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var child in all.Where(folder => folder.ParentKey == current))
                if (found.Add(child.Key)) pending.Enqueue(child.Key);
        }
        return found;
    }

    // --- dragging ---------------------------------------------------------------------------------------

    private void EnableFolderDragging()
    {
        _localTree.AllowDrop = true;
        _messages.ItemDrag += (_, _) =>
        {
            if (SelectedDrafts() is { Length: > 0 } drafts) { _messages.DoDragDrop(drafts, DragDropEffects.Move); return; }
            var selected = Selected();
            if (CanFileLocally(selected)) _messages.DoDragDrop(selected, DragDropEffects.Move);
        };
        _localTree.ItemDrag += (_, e) =>
        {
            if (e.Item is TreeNode node && Draggable(node)) _localTree.DoDragDrop(node, DragDropEffects.Move);
        };
        _localTree.DragOver += (_, e) =>
        {
            var node = NodeUnder(e);
            AssistDrag(e, node);
            // Highlighting the target as the pointer passes is what makes a drop feel aimed rather than hopeful.
            // It must NOT be done by selecting the node: selecting a folder opens it, so every twitch of the
            // mouse mid-drag was loading a folder of mail — a drag across the tree fired dozens of those and
            // the drag stalled behind them, which is why dropping appeared not to work at all.
            var messages = DraggedMessages(e);
            var drafts = DraggedDrafts(e);
            SetDropTarget(drafts is not null ? CanDropDrafts(drafts, node) ? node : null : messages is not null
                ? CanDropMessages(messages, node) ? node : null
                : CanDrop(Dragged(e), node) ? node : null);
            e.Effect = _dropTarget is null ? DragDropEffects.None : DragDropEffects.Move;
        };
        _localTree.DragEnter += (_, e) => e.Effect = DragDropEffects.Move;
        _localTree.DragLeave += (_, _) => { SetDropTarget(null); _dragHoverNode = null; };
        _localTree.DragDrop += (_, e) =>
        {
            var target = _dropTarget;
            SetDropTarget(null);
            var destination = target ?? NodeUnder(e);
            if (DraggedDrafts(e) is { } drafts)
            {
                if (CanDropDrafts(drafts, destination) && DraftDestination(destination) is { } key)
                    BeginInvoke(new Action(() => _ = FileDraftsAsync(drafts, key, destination!.Text.Split("  (")[0])));
            }
            else if (DraggedMessages(e) is { } messages)
            {
                if (ImportedMessageDestination(messages, destination) is { } imported)
                {
                    BeginInvoke(new Action(() => _ = FileImportedMessagesAsync(messages, imported)));
                    return;
                }
                // Filed once the drag has let go of the mouse: run inside the drop, the work held the drag
                // itself open, and the window with it.
                if (CanDropMessages(messages, destination) && MessageDestination(destination) is { } key)
                {
                    var name = destination!.Text.Split("  (")[0];
                    BeginInvoke(new Action(() => _ = FileMessagesAsync(messages, key, name)));
                }
            }
            else DropFolder(Dragged(e), destination);
        };
    }

    private static MessageSummary[]? DraggedMessages(DragEventArgs e)
        => e.Data?.GetData(typeof(MessageSummary[])) as MessageSummary[];

    private static OutgoingMessage[]? DraggedDrafts(DragEventArgs e)
        => e.Data?.GetData(typeof(OutgoingMessage[])) as OutgoingMessage[];

    private static string? DraftDestination(TreeNode? node) => node?.Tag switch
    {
        LocalFolder { Kind: not LocalFolderKinds.Outbox } folder => folder.Key,
        MailFolder { LocalKey: { } key } when key != LocalFolderKinds.Outbox => key,
        _ => null
    };

    private bool CanDropDrafts(OutgoingMessage[] drafts, TreeNode? node)
        => drafts.Length > 0 && drafts.All(draft => draft.Status == OutgoingStates.Draft)
            && DraftDestination(node) is { } key && key != (_selectedArchive?.LocalKey ?? _selectedLocalFolder);

    private static string? MessageDestination(TreeNode? node) => node?.Tag switch
    {
        LocalFolder { Kind: not (LocalFolderKinds.Drafts or LocalFolderKinds.Outbox) } folder => folder.Key,
        MailFolder { LocalKey: { } key } when key is not (LocalFolderKinds.Drafts or LocalFolderKinds.Outbox) => key,
        _ => null
    };

    private bool CanDropMessages(MessageSummary[] messages, TreeNode? node)
        => ImportedMessageDestination(messages, node) is not null
            || MessageDestination(node) is { } key && CanFileLocally(messages) && messages.Any(message => message.Folder != key);

    private static MailFolder? ImportedMessageDestination(MessageSummary[] messages, TreeNode? node)
        => node?.Tag is MailFolder { LocalKey: null } folder && messages.Length > 0
            && messages.All(message => message.Source == MailContract.ArchiveSource && message.Account == folder.Account)
            && messages.Any(message => message.Folder != folder.ArchiveFolder) ? folder : null;

    private async Task FileImportedMessagesAsync(MessageSummary[] messages, MailFolder target)
    {
        messages = messages.Where(message => message.Folder != target.ArchiveFolder).ToArray();
        if (messages.Length == 0) return;
        var ids = messages.Select(message => message.Id).ToArray();
        var from = messages.ToDictionary(message => message.Id, message => message.Folder);
        var undo = new UndoableMove(target.Display, () =>
        {
            var current = _services.Database.MessageFolders(ids);
            return from.Where(pair => current.GetValueOrDefault(pair.Key) == target.ArchiveFolder)
                .GroupBy(pair => pair.Value).Sum(group => _services.Database.MoveImportedMessages(
                    group.Select(pair => pair.Key).ToArray(), target.Account, group.Key));
        });
        PushUndo(undo);
        RemoveRows(ids);
        _status.Text = $"Moving to {target.Display}…";
        var writing = Task.Run(() => _services.Database.MoveImportedMessages(ids, target.Account, target.ArchiveFolder));
        _filing = writing;
        try
        {
            var moved = await writing;
            if (IsDisposed) return;
            _status.Text = $"{moved:N0} messages moved to {target.Display} — Ctrl+Z to undo";
            await RefreshReadStatesAsync([]);
        }
        catch (Exception error)
        {
            if (_undoMoves.TryPeek(out var top) && ReferenceEquals(top, undo)) _undoMoves.Pop();
            if (IsDisposed) return;
            ShowError("Could not move the messages", error);
            await LoadMessagesAsync();
        }
    }

    /// <summary>Marks where the drop would land, repainting only the two nodes that change.</summary>
    private void SetDropTarget(TreeNode? node)
    {
        if (ReferenceEquals(_dropTarget, node)) return;
        var previous = _dropTarget;
        _dropTarget = node;
        if (previous is not null) _localTree.Invalidate(previous.Bounds);
        if (node is not null) _localTree.Invalidate(node.Bounds);
    }

    /// <summary>The folders the user made. A standard folder stays where it is — including the Inbox an import
    /// merged into, which carries an imported folder's tag but is still the mailbox's Inbox.</summary>
    private static bool Draggable(TreeNode node)
        => node.Tag is LocalFolder { Kind: LocalFolderKinds.User } or MailFolder { ArchiveFolder.Length: > 0, LocalKey: null };

    private static TreeNode? Dragged(DragEventArgs e)
        => e.Data?.GetData(typeof(TreeNode)) as TreeNode;

    /// <summary>The folder whose row the pointer is on — anywhere on the row, not only its name. GetNodeAt
    /// answered only for the name itself, so the highlight dropped out over the icon, the indent or just past
    /// a short name, which made aiming a drop feel loose.</summary>
    private TreeNode? NodeUnder(DragEventArgs e)
        => _localTree.HitTest(_localTree.PointToClient(new Point(e.X, e.Y))).Node;

    private long _dragHoverSince;
    private TreeNode? _dragHoverNode;
    private long _dragScrolledAt;

    /// <summary>What a tree does under a drag besides highlighting: scrolls when the pointer nears its top or
    /// bottom edge, and opens a closed folder the pointer rests on, so a drop can reach a folder that is not
    /// on screen or not yet opened without putting the message down first.</summary>
    private void AssistDrag(DragEventArgs e, TreeNode? node)
    {
        var point = _localTree.PointToClient(new Point(e.X, e.Y));
        var now = Environment.TickCount64;
        const int edge = 18;
        if (now - _dragScrolledAt > 120)
        {
            if (point.Y < edge) { SendMessage(_localTree.Handle, WM_VSCROLL, SB_LINEUP, IntPtr.Zero); _dragScrolledAt = now; }
            else if (point.Y > _localTree.ClientSize.Height - edge) { SendMessage(_localTree.Handle, WM_VSCROLL, SB_LINEDOWN, IntPtr.Zero); _dragScrolledAt = now; }
        }
        if (!ReferenceEquals(node, _dragHoverNode)) { _dragHoverNode = node; _dragHoverSince = now; return; }
        if (node is { IsExpanded: false } && node.Nodes.Count > 0 && now - _dragHoverSince > 700)
        {
            node.Expand();
            _dragHoverSince = now;
        }
    }

    private const int WM_VSCROLL = 0x0115;
    private static readonly IntPtr SB_LINEUP = 0;
    private static readonly IntPtr SB_LINEDOWN = 1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    /// <summary>A drop only makes sense onto a folder of the same kind — and, for imported mail, the same
    /// account — and never onto the folder's own descendant, which would detach the subtree from the tree.</summary>
    private bool CanDrop(TreeNode? dragged, TreeNode? target)
    {
        if (dragged is null || target is null || ReferenceEquals(dragged, target)) return false;
        for (var ancestor = target; ancestor is not null; ancestor = ancestor.Parent)
            if (ReferenceEquals(ancestor, dragged)) return false;
        return (dragged.Tag, target.Tag) switch
        {
            (LocalFolder { Kind: LocalFolderKinds.User }, LocalFolder) => true,
            // A standard folder that an import merged into carries the imported folder's tag, but it is still
            // the mailbox's own Inbox and a folder of one's own belongs inside it.
            (LocalFolder { Kind: LocalFolderKinds.User }, MailFolder { LocalKey: not null }) => true,
            (LocalFolder { Kind: LocalFolderKinds.User }, string tag) => tag == "local-all",
            (MailFolder from, MailFolder to) => from.Account == to.Account && to.ArchiveFolder.Length > 0,
            // A mailbox's own heading: dropping there takes the folder back out to its top level. The primary
            // import has no heading, being the tree itself, so the tree's root serves as its top level.
            (MailFolder from, null) => target.Name == ArchiveRootName(from.Account),
            (MailFolder from, string tag) => tag == "local-all" && from.Account == _primaryArchive,
            _ => false
        };
    }

    internal static string ArchiveRootName(string account) => "ArchiveRoot:" + account;

    private async void DropFolder(TreeNode? dragged, TreeNode? target)
    {
        if (!CanDrop(dragged, target) || dragged is null || target is null) return;
        if (!BeginFolderChange()) return;
        try
        {
            switch (dragged.Tag, target.Tag)
            {
                case (LocalFolder moving, LocalFolder into):
                    await Task.Run(() => _services.Database.MoveLocalFolder(moving.Key, into.Key));
                    _status.Text = $"{moving.Name} moved into {into.Name}";
                    break;
                case (LocalFolder moving, MailFolder { LocalKey: { } into, Display: var display }):
                    await Task.Run(() => _services.Database.MoveLocalFolder(moving.Key, into));
                    _status.Text = $"{moving.Name} moved into {display}";
                    break;
                case (LocalFolder moving, string):
                    await Task.Run(() => _services.Database.MoveLocalFolder(moving.Key, null));
                    _status.Text = $"{moving.Name} moved to the top level";
                    break;
                case (MailFolder moving, MailFolder into):
                    var leaf = moving.ArchiveFolder.Split('/')[^1];
                    await Task.Run(() => _services.Database.MoveArchiveFolder(MailContract.ArchiveSource, moving.Account,
                        moving.ArchiveFolder, into.ArchiveFolder + "/" + leaf));
                    _status.Text = $"{leaf} moved into {into.Display}";
                    break;
                case (MailFolder moving, null or string):
                    var name = moving.ArchiveFolder.Split('/')[^1];
                    await Task.Run(() => _services.Database.MoveArchiveFolder(MailContract.ArchiveSource, moving.Account,
                        moving.ArchiveFolder, name));
                    _status.Text = $"{name} moved to the top of {moving.Account}";
                    break;
                default: return;
            }
            await ReloadAccountsAsync();
        }
        catch (Exception exception) { ShowError("Could not move the folder", exception); }
        finally { _folderChanging = false; }
    }

    /// <summary>A one-line prompt. Deliberately plain: naming a folder should not need a dialog with sections.</summary>
    private string? Ask(string title, string prompt, string initial)
    {
        using var form = new Form
        {
            Text = title, AccessibleName = title, ClientSize = new Size(380, 116),
            FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, Font = new Font("Tahoma", 8.25f)
        };
        var label = new Label { Text = prompt, Dock = DockStyle.Top, Height = 22, Padding = new Padding(2, 4, 0, 0) };
        var box = new TextBox { Text = initial, Dock = DockStyle.Top, AccessibleName = "Folder name" };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(0, 6, 0, 0) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 82, AccessibleName = "OK" };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 82, AccessibleName = "Cancel" };
        buttons.Controls.AddRange([ok, cancel]);
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 10, 10, 4) };
        body.Controls.AddRange([box, label]);
        form.Controls.AddRange([body, buttons]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Shown += (_, _) => { box.Focus(); box.SelectAll(); };
        return form.ShowDialog(this) == DialogResult.OK && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }

    private readonly record struct FolderTarget(LocalFolder? Local, MailFolder? Archive,
        bool CanHoldNewFolder, bool CanRename, bool CanMove, bool CanDelete);
}
