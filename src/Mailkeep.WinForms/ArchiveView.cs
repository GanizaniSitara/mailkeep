using Mailkeep.Core;
using Mailkeep.Infrastructure;

namespace Mailkeep.WinForms;

/// <summary>The archive, kept out of the everyday mailbox (docs/folder-store-redesign.md section 12): its own
/// window to browse and search it, and one screen to attach and detach archive locations. Archived mail is
/// read-only and never reaches the mail database; reading it goes through the same message window as stored mail.</summary>
internal sealed partial class MainForm
{
    private ArchiveStore? _archiveStore;
    private ArchiveStore Archives => _archiveStore ??= new ArchiveStore(_services.Paths);
    private ArchiveWindow? _archiveWindow;
    private bool _archiveTransferInProgress;

    private MessageContent? GetAnyMessage(long id, Action<string, double>? measure = null)
        => ArchiveStore.Owns(id) ? Archives.Get(id) : _services.Database.GetMessage(id, measure);

    private string ExtractAnyAttachment(long id, int index)
        => ArchiveStore.Owns(id) ? Archives.ExtractAttachment(id, index) : _services.Database.ExtractAttachment(id, index);

    private void ShowArchiveWindow()
    {
        if (_archiveWindow is { IsDisposed: false })
        {
            if (_archiveWindow.WindowState == FormWindowState.Minimized) _archiveWindow.WindowState = FormWindowState.Normal;
            _archiveWindow.Activate();
            return;
        }
        _archiveWindow = new ArchiveWindow(Archives, id => _ = OpenMessageAsync(id), ShowManageArchives, RestoreArchiveAsync);
        // An owned form minimizes with its owner. Archive is a separate taskbar window.
        _archiveWindow.Show();
    }

    private async Task RestoreArchiveAsync(ArchiveStore.Archive archive, string path, bool tree, bool copy)
    {
        if (!RequireReady() || _archiveTransferInProgress) return;
        var owner = _archiveWindow is { IsDisposed: false } window ? (IWin32Window)window : this;
        _archiveTransferInProgress = true;
        try
        {
            var pending = !copy ? ArchiveMoveBack.PendingPlan(archive.Location) : null;
            string into;
            if (pending is not null) into = pending.Into;
            else
            {
                var folders = _services.Database.ListLocalFolders();
                using var chooser = new FolderChoiceForm(folders.Where(folder => folder.Kind is not (LocalFolderKinds.Drafts or LocalFolderKinds.Outbox)).ToArray(),
                    folders, movingFolder: true, restoring: true, archiveCopy: copy);
                if (chooser.ShowDialog(owner) != DialogResult.OK) return;
                into = chooser.Chosen is { } chosen ? new MailFiling(_services).Folders().Single(folder => folder.Key == chosen.Key).Path : "";
            }
            ArchiveRestore.Result Transfer(bool execute) => copy
                ? new ArchiveRestore(_services).Run(archive.Location, path, tree, into, execute)
                : new ArchiveMoveBack(_services).Run(archive.Location, path, tree, into, execute);
            var preview = await Task.Run(() => Transfer(false));
            var destinations = preview.Plan.Folders.Select(folder => "All Local Mail > "
                + ((into.Length == 0 ? "" : into + "/") + folder.OriginalPath).Replace("/", " > ")).ToArray();
            var destinationText = string.Join("\r\n", destinations.Take(4))
                + (destinations.Length > 4 ? $"\r\nand {destinations.Length - 4:N0} more folders" : "");
            var title = copy ? "Copy from Archive" : pending is not null ? "Finish Interrupted Restore" : "Restore from Archive";
            if (MessageBox.Show(owner, $"{(copy ? "Copy" : "Move back")} {preview.Plan.Messages:N0} messages to:\r\n{destinationText}\r\n\r\n"
                + (copy ? "The originals will stay in Archive. You will have mail in both places."
                    : "The selected folders will leave Archive once their Local Mail copies are verified. You can archive them again later."),
                title, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            if (owner is Form form) { form.Enabled = false; form.UseWaitCursor = true; }
            var done = await Task.Run(() => Transfer(true));
            if (owner is Form finished) { finished.Enabled = true; finished.UseWaitCursor = false; }
            Archives.Reload();
            if (_archiveWindow is { IsDisposed: false }) _archiveWindow.Reload();
            await ReloadAccountsAsync();
            await LoadMessagesAsync();
            try { await Task.Run(() => _services.Catalog.SyncOperational()); }
            catch (Exception error) { ShowError("Mail restored, but search refresh failed", error, owner); }
            var copied = done.Restored > 0
                ? $"Copied {done.Restored:N0} messages to Local Mail."
                    + (done.AlreadyRestored > 0 ? $"\r\n{done.AlreadyRestored:N0} messages were already copied and were left as they are." : "")
                : "These messages have already been copied to Local Mail. No new copies were made.";
            MessageBox.Show(owner, (copy ? copied : preview.Plan.Folders.Length == 1
                    ? "Restored the folder to Local Mail." : $"Restored {preview.Plan.Folders.Length:N0} folders to Local Mail.")
                + $"\r\n\r\nFind the folders under:\r\n{destinationText}"
                + (copy ? "\r\n\r\nThe originals are still in Archive." : preview.Plan.Folders.Length == 1
                    ? "\r\n\r\nThis folder is no longer in Archive." : "\r\n\r\nThese folders are no longer in Archive."),
                copy ? "Copied to Local Mail" : "Restored to Local Mail", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception error)
        {
            if (owner is Form form) form.Enabled = true;
            ShowError(copy ? "Could not copy archived folders" : "Could not finish restoring archived folders", error, owner);
        }
        finally
        {
            _archiveTransferInProgress = false;
            if (owner is Form form && !form.IsDisposed) { form.Enabled = true; form.UseWaitCursor = false; }
        }
    }

    private void ShowManageArchives()
    {
        using var manager = new ManageArchivesForm(Archives);
        manager.ShowDialog(_archiveWindow is { IsDisposed: false, Visible: true } window ? window : this);
        if (_archiveWindow is { IsDisposed: false }) _archiveWindow.Reload();
    }

    /// <summary>For the VM test rig: once the mailbox has loaded, attaches a location (if given) and optionally
    /// shows the Archive window.</summary>
    internal void AttachArchiveWhenReady(string? location, bool show)
    {
        var timer = new System.Windows.Forms.Timer { Interval = 200 };
        var waited = 0;
        timer.Tick += (_, _) =>
        {
            waited += timer.Interval;
            if (!_ready && waited < 120_000 && !IsDisposed) return;
            timer.Stop();
            timer.Dispose();
            if (!_ready) return;
            if (location is not null)
            {
                try { Archives.Add(location); }
                catch (Exception error) { ShowError("Could not attach the archive", error); return; }
            }
            if (show) ShowArchiveWindow();
        };
        timer.Start();
    }
}

/// <summary>Browse and search the archive. Left: each archive and its folders; right: the messages, newest first.
/// Enter or double-click reads one in the usual message window. Search covers every attached archive and says
/// which were not searched.</summary>
internal sealed class ArchiveWindow : Form
{
    private sealed record Selection(ArchiveStore.Archive Archive, string Path, ArchiveStore.Folder? Folder);
    private readonly ArchiveStore _store;
    private readonly Action<long> _open;
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, AccessibleName = "Archive folders" };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
        AccessibleName = "Archived messages",
        // Rows are drawn on demand from _rows, as in the main list. Filling and clearing an item collection threw
        // inside WinForms when a search result was replaced by an empty folder (ArgumentOutOfRange in Clear).
        VirtualMode = true
    };
    private readonly TextBox _query = new() { Dock = DockStyle.Fill, AccessibleName = "Search archive" };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private IReadOnlyList<MessageSummary> _rows = [];
    private int _reloadGeneration;
    private int _viewGeneration;

    public ArchiveWindow(ArchiveStore store, Action<long> open, Action manage,
        Func<ArchiveStore.Archive, string, bool, bool, Task>? restore = null)
    {
        _store = store;
        _open = open;
        Text = "Archive - Mailkeep";
        Name = "MailkeepArchive";
        AccessibleName = "Mailkeep Archive";
        Icon = Dialog.WindowIcon();
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1100, 700);
        MinimumSize = new Size(700, 400);

        var searchButton = new Button { Text = "Search", AutoSize = true, Dock = DockStyle.Right, AccessibleName = "Search archive button" };
        var manageButton = new Button { Text = "Manage Archives…", AutoSize = true, Dock = DockStyle.Right, AccessibleName = "Manage archives button" };
        var searchBar = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(6, 4, 6, 4) };
        var label = new Label { Text = "Search archives:", AutoSize = true, Dock = DockStyle.Left, Padding = new Padding(0, 4, 6, 0) };
        searchBar.Controls.Add(_query);
        searchBar.Controls.Add(label);
        searchBar.Controls.Add(searchButton);
        searchBar.Controls.Add(manageButton);

        _list.Columns.Add("From", 200);
        _list.Columns.Add("Subject", 380);
        _list.Columns.Add("Received", 130);
        _list.Columns.Add("Size", 70, HorizontalAlignment.Right);
        _list.Columns.Add("Folder", 220);

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        // Set once the window has its size: set in the initializer it is clamped to the container's default width.
        Load += (_, _) => split.SplitterDistance = 300;
        split.Panel1.Controls.Add(_tree);
        split.Panel2.Controls.Add(_list);
        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);
        Controls.Add(split);
        Controls.Add(searchBar);
        Controls.Add(statusStrip);

        _tree.AfterSelect += async (_, e) => await ShowFolderAsync((e.Node?.Tag as Selection)?.Folder);
        // Right-click a folder to detach it (not opened or searched) or attach it again; the file stays put.
        _tree.NodeMouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.Node.Tag is not Selection selected) return;
            _tree.SelectedNode = e.Node;
            var menu = new ContextMenuStrip { Font = new Font("Tahoma", 8.25f) };
            if (selected.Folder is { } folder)
            {
                var item = new ToolStripMenuItem(folder.Attached ? "Detach Folder" : "Attach Folder")
                {
                    AccessibleName = folder.Attached ? "Detach archived folder" : "Attach archived folder"
                };
                item.Click += (_, _) => SetFolderAttached(folder, !folder.Attached);
                menu.Items.Add(item);
                if (restore is not null && folder.Attached)
                {
                    menu.Items.Add("Restore Folder to Local Mail…", null, async (_, _) => await restore(selected.Archive, selected.Path, false, false));
                    menu.Items.Add("Copy Folder to Local Mail…", null, async (_, _) => await restore(selected.Archive, selected.Path, false, true));
                }
            }
            if (restore is not null && e.Node.Nodes.Count > 0 && selected.Archive.Attached)
            {
                menu.Items.Add("Restore Tree to Local Mail…", null, async (_, _) => await restore(selected.Archive, selected.Path, true, false));
                menu.Items.Add("Copy Tree to Local Mail…", null, async (_, _) => await restore(selected.Archive, selected.Path, true, true));
            }
            if (restore is not null && selected.Path.Length == 0 && ArchiveMoveBack.PendingPlan(selected.Archive.Location) is { } pending)
                menu.Items.Add("Finish Interrupted Restore…", null, async (_, _) => await restore(selected.Archive, pending.Selection, pending.IncludeChildren, false));
            if (menu.Items.Count == 0) { menu.Dispose(); return; }
            menu.Show(_tree, e.Location);
        };
        _list.RetrieveVirtualItem += (_, e) =>
        {
            var row = _rows[e.ItemIndex];
            var item = new ListViewItem(row.DisplaySender) { Tag = row.Id };
            item.SubItems.Add(row.Subject);
            item.SubItems.Add(row.Date?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "");
            item.SubItems.Add(row.DisplaySize);
            item.SubItems.Add($"{row.Account} / {row.Folder}");
            e.Item = item;
        };
        _list.DoubleClick += (_, _) => OpenSelected();
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; OpenSelected(); } };
        searchButton.Click += async (_, _) => await SearchAsync();
        _query.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await SearchAsync(); } };
        manageButton.Click += (_, _) => manage();
        Shown += (_, _) => Reload();
    }

    /// <summary>Rebuilds the tree from the archive list: attached archives with their folders, detached and missing
    /// ones greyed so it is plain why they show nothing.</summary>
    public async void Reload()
    {
        var generation = ++_reloadGeneration;
        ++_viewGeneration;
        _status.Text = "Loading archive folders…";
        (ArchiveStore.Archive Archive, bool Available, IReadOnlyList<ArchiveStore.Folder> Folders)[] listing;
        try
        {
            var archives = _store.Archives.ToArray();
            listing = await Task.Run(() => archives.Select(archive =>
            {
                var available = ArchiveStore.IsAvailable(archive);
                return (archive, available, archive.Attached && available ? _store.Folders(archive) : (IReadOnlyList<ArchiveStore.Folder>)[]);
            }).ToArray());
        }
        catch (Exception error) { if (!IsDisposed && generation == _reloadGeneration) _status.Text = "Could not load archive folders: " + error.Message; return; }
        if (IsDisposed || Disposing || generation != _reloadGeneration) return;
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        foreach (var (archive, available, folders) in listing)
        {
            var state = !archive.Attached ? " (detached)" : !available ? " (not found)" : "";
            var root = new TreeNode(archive.Name + state) { ToolTipText = archive.Location, Tag = new Selection(archive, "", null) };
            if (archive.Attached && available)
            {
                TreeNode Node(ArchiveTree.Node branch)
                {
                    var folder = branch.Folder;
                    var text = branch.Name + (folder is { Attached: false } ? " (detached)" : folder is { Count: > 0 } ? $"  ({folder.Count:N0})" : "");
                    var node = new TreeNode(text) { Tag = new Selection(archive, branch.Path, folder), ToolTipText = branch.Path };
                    if (folder is { Attached: false }) node.ForeColor = SystemColors.GrayText;
                    node.Nodes.AddRange(branch.Children.Select(Node).ToArray());
                    return node;
                }
                root.Nodes.AddRange(ArchiveTree.Build(folders).Select(Node).ToArray());
            }
            else root.ForeColor = SystemColors.GrayText;
            root.Expand();
            _tree.Nodes.Add(root);
        }
        _tree.EndUpdate();
        _status.Text = listing.Length == 0
            ? "No archives yet. Use Manage Archives… to add an archive location."
            : $"{listing.Count(item => item.Archive.Attached):N0} of {listing.Length:N0} archives attached";
        Show([]);
    }

    private void SetFolderAttached(ArchiveStore.Folder folder, bool attached)
    {
        _store.SetFolderAttached(folder.Archive.Location, folder.Label, attached);
        Reload();
        _status.Text = attached ? $"{folder.Label} attached" : $"{folder.Label} detached — it is not shown or searched until attached again";
    }

    private async Task ShowFolderAsync(ArchiveStore.Folder? folder)
    {
        var generation = ++_viewGeneration;
        if (folder is null)
        {
            Show([]);
            _status.Text = "Select a folder to read its mail, or right-click to copy folders to Local Mail.";
            return;
        }
        if (!folder.Attached)
        {
            Show([]);
            _status.Text = $"{folder.Label} is detached. Right-click it and choose Attach Folder to read it.";
            return;
        }
        try
        {
            var rows = await Task.Run(() => _store.List(folder));
            if (IsDisposed || Disposing || generation != _viewGeneration) return;
            Show(rows);
            _status.Text = $"{folder.Archive.Name} / {folder.Label} — {rows.Count:N0} messages (archived, read-only)";
        }
        catch (Exception error) { if (!IsDisposed && generation == _viewGeneration) _status.Text = "Could not open archive folder: " + error.Message; }
    }

    private async Task SearchAsync()
    {
        var text = _query.Text.Trim();
        if (text.Length == 0) return;
        var generation = ++_viewGeneration;
        _status.Text = "Searching archives…";
        UseWaitCursor = true;
        try
        {
            var result = await Task.Run(() => _store.Search(text));
            if (IsDisposed || Disposing || generation != _viewGeneration) return;
            _tree.SelectedNode = null;
            Show(result.Hits);
            var missed = result.NotSearched.Count == 0 ? "" : " — not searched: " + string.Join(", ", result.NotSearched);
            _status.Text = $"{result.Hits.Count:N0} archived messages match \"{text}\"{missed}";
        }
        catch (Exception error) { if (!IsDisposed && generation == _viewGeneration) _status.Text = "Search failed: " + error.Message; }
        finally { if (!IsDisposed) UseWaitCursor = false; }
    }

    private void Show(IReadOnlyList<MessageSummary> rows)
    {
        _list.SelectedIndices.Clear();
        _list.VirtualListSize = 0;
        _rows = rows;
        _list.VirtualListSize = rows.Count;
        if (rows.Count > 0) _list.EnsureVisible(0);
        _list.Invalidate();
    }

    private void OpenSelected()
    {
        if (_list.SelectedIndices.Count == 1 && _list.SelectedIndices[0] < _rows.Count) _open(_rows[_list.SelectedIndices[0]].Id);
    }
}

/// <summary>Asks before a folder moves to the archive, says what that means, and offers the whole-mailbox backup.</summary>
internal sealed class ArchiveConfirmForm : Form
{
    private readonly CheckBox _backup = new()
    {
        Text = "Back up the whole mailbox first (takes a few minutes)", Checked = true, AutoSize = true,
        AccessibleName = "Back up the whole mailbox first"
    };

    public bool Backup => _backup.Checked;

    public ArchiveConfirmForm(string folderPath, int messages)
    {
        Text = "Move to Archive";
        Icon = Dialog.WindowIcon();
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        var text = new Label
        {
            AutoSize = true, MaximumSize = new Size(460, 0),
            Text = $"Move {folderPath} ({messages:N0} messages) to the archive?\r\n\r\n"
                + "It leaves the folder tree and normal search, and stays readable in Tools > Archive. "
                + "You can restore a folder or tree from the Archive window later."
        };
        var ok = new Button { Text = "Move", DialogResult = DialogResult.OK, AutoSize = true, AccessibleName = "Move to archive" };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([cancel, ok]);
        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(text);
        layout.Controls.Add(_backup);
        layout.Controls.Add(buttons);
        _backup.Margin = new Padding(0, 12, 0, 12);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

/// <summary>The one place archives are attached and detached: whole locations, never single files.</summary>
internal sealed class ManageArchivesForm : Form
{
    private int _fillGeneration;
    private readonly ArchiveStore _store;
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
        AccessibleName = "Archives"
    };
    private readonly Button _attach = new() { Text = "Detach", Width = 110, AccessibleName = "Attach or detach archive" };
    private readonly Button _remove = new() { Text = "Remove from list", Width = 110, AccessibleName = "Remove archive from list" };

    public ManageArchivesForm(ArchiveStore store)
    {
        _store = store;
        Text = "Manage Archives";
        Icon = Dialog.WindowIcon();
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(780, 360);
        MinimumSize = new Size(560, 260);
        MinimizeBox = false;
        _list.Columns.Add("Archive", 160);
        _list.Columns.Add("Location", 330);
        _list.Columns.Add("State", 90);
        _list.Columns.Add("Folders", 70, HorizontalAlignment.Right);

        var add = new Button { Text = "Add location…", Width = 110, AccessibleName = "Add archive location" };
        var close = new Button { Text = "Close", Width = 110, DialogResult = DialogResult.OK };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, Width = 124, Padding = new Padding(6) };
        buttons.Controls.AddRange([add, _attach, _remove, close]);
        var note = new Label
        {
            Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(6, 4, 6, 4),
            Text = "Detached archives are not shown or searched. Detaching or removing never deletes anything on disk."
        };
        Controls.Add(_list);
        Controls.Add(buttons);
        Controls.Add(note);
        AcceptButton = close;
        CancelButton = close;

        add.Click += (_, _) => AddLocation();
        _attach.Click += (_, _) => ToggleAttached();
        _remove.Click += (_, _) => RemoveSelected();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        Fill();
    }

    private ArchiveStore.Archive? Selected => _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as ArchiveStore.Archive : null;

    private async void Fill(string? select = null)
    {
        var generation = ++_fillGeneration;
        var archives = _store.Archives.ToArray();
        (ArchiveStore.Archive Archive, bool Available, int Count)[] listing;
        try
        {
            listing = await Task.Run(() => archives.Select(archive =>
            {
                var available = ArchiveStore.IsAvailable(archive);
                return (archive, available, archive.Attached && available ? _store.Folders(archive).Count : 0);
            }).ToArray());
        }
        catch (Exception error) { if (!IsDisposed) MessageBox.Show(this, error.Message, "Manage Archives", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (IsDisposed || Disposing || generation != _fillGeneration) return;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var (archive, available, count) in listing)
        {
            var item = new ListViewItem(archive.Name) { Tag = archive };
            item.SubItems.Add(archive.Location);
            item.SubItems.Add(!available ? "Not found" : archive.Attached ? "Attached" : "Detached");
            item.SubItems.Add(archive.Attached && available ? count.ToString("N0") : "");
            if (!archive.Attached || !available) item.ForeColor = SystemColors.GrayText;
            item.Selected = string.Equals(archive.Location, select, StringComparison.OrdinalIgnoreCase);
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var selected = Selected;
        _attach.Enabled = _remove.Enabled = selected is not null;
        _attach.Text = selected is { Attached: false } ? "Attach" : "Detach";
    }

    private void AddLocation()
    {
        using var browse = new FolderBrowserDialog { Description = "Choose the folder that holds the archive", UseDescriptionForTitle = true };
        if (browse.ShowDialog(this) != DialogResult.OK) return;
        try { Fill(_store.Add(browse.SelectedPath).Location); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Could not add archive", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ToggleAttached()
    {
        if (Selected is not { } archive) return;
        _store.SetAttached(archive.Location, !archive.Attached);
        Fill(archive.Location);
    }

    private void RemoveSelected()
    {
        if (Selected is not { } archive) return;
        if (MessageBox.Show(this, $"Remove \"{archive.Name}\" from the list?\r\n\r\nNothing is deleted: the archive stays at {archive.Location} and can be added again.",
                "Remove archive", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _store.Remove(archive.Location);
        Fill();
    }
}
