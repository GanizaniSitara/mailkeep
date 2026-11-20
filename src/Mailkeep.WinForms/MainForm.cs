using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Mailkeep.Core;
using Mailkeep.Infrastructure;

namespace Mailkeep.WinForms;

internal sealed partial class MainForm : Form
{
    private readonly MailkeepServices _services;
    private readonly BufferedTreeView _localTree = new() { Dock = DockStyle.Fill, Margin = Padding.Empty, HideSelection = false, BorderStyle = BorderStyle.None, Font = new Font("Tahoma", 8.25f), ItemHeight = 19, Indent = 19, DrawMode = TreeViewDrawMode.OwnerDrawText, AccessibleName = "Local mail folders" };
    private readonly BufferedListView _accounts = new() { Dock = DockStyle.Fill, Margin = Padding.Empty, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.None, HideSelection = false, MultiSelect = false, ShowItemToolTips = true, BorderStyle = BorderStyle.None, Font = new Font("Tahoma", 8.25f), AccessibleName = "Connected accounts" };
    private readonly BufferedListView _messages = new() { Dock = DockStyle.Fill, Margin = Padding.Empty, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true, ShowItemToolTips = false, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Tahoma", 8.25f), AccessibleName = "Message list" };
    private readonly ToolTip _messageHoverTip = new();
    private readonly BufferedSplitContainer _folderSplit = new() { Dock = DockStyle.Fill, Margin = Padding.Empty, Orientation = Orientation.Horizontal, BorderStyle = BorderStyle.None, SplitterWidth = 4, BackColor = SystemColors.Control, AccessibleName = "Local and connected mail splitter" };
    private readonly BufferedSplitContainer _previewSplit = new() { Dock = DockStyle.Fill, Margin = Padding.Empty, Orientation = Orientation.Horizontal, BorderStyle = BorderStyle.None, SplitterWidth = 6, BackColor = SystemColors.Control, AccessibleName = "Messages and reading pane splitter" };
    // A RichTextBox purely for its line spacing: OE's reading pane has visibly more leading than a TextBox
    // can render, and that air is most of what makes its body text comfortable.
    private readonly RichTextBox _body = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White, ScrollBars = RichTextBoxScrollBars.Vertical, WordWrap = true, Font = new Font("Tahoma", 9f), AccessibleName = "Message body" };
    private readonly ClassicBand _folderBand = BandLabel("Inbox", ContentAlignment.MiddleLeft);
    private readonly ClassicBand _identityBand = BandLabel("Mailkeep", ContentAlignment.MiddleRight);
    private readonly Label _subject = new() { Dock = DockStyle.Fill, Font = new Font("Tahoma", 10.5f), ForeColor = SystemColors.GrayText, AutoEllipsis = true, Text = "Select a message to read it" };
    private readonly AddressLabel _from = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Tahoma", 8.25f), Margin = new Padding(0, 0, 3, 0) };
    private readonly AddressLabel _to = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Tahoma", 8.25f), Margin = new Padding(0, 0, 3, 0) };
    private readonly Label _date = ValueLabel();
    private readonly AttachmentBar _attachments;
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripProgressBar _syncProgress = new() { Visible = false, Width = 140, AccessibleName = "Send and Receive progress" };
    private bool _syncRunning;
    private readonly System.Windows.Forms.Timer _autoSync = new() { Interval = 3 * 60 * 1000 };
    private ToolStripMenuItem _autoSyncItem = null!;
    private ToolStripSplitButton _receive = null!;
    private ToolStrip _mainToolbar = null!;
    private int _accountBadgeWidth = 25;
    /// <summary>The pane's HTML view, with the text box behind it for mail that was never a page and
    /// for a machine whose runtime will not start.</summary>
    private readonly HtmlView _html;
    private readonly RemoteImageBar _remoteImages = new();
    private readonly RemoteImagePermissions _imagePermissions;
    private readonly List<Label> _previewLabels = [];
    private readonly ToolStripStatusLabel _count = new();
    private readonly ToolStripButton _delete;
    private readonly ToolStripButton _openHtml;
    private ToolStripButton _print = null!;
    private ToolStripButton _move = null!;
    private readonly List<ToolStripItem> _readyItems = [];
    private readonly List<ToolStripItem> _composeItems = [];
    private readonly List<ToolStripItem> _replyItems = [];
    private IReadOnlyDictionary<string, (int Total, int New)> _accountCounts = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, int> _accountUnreadCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    // Held so that re-sorting the list costs nothing but the sort itself.
    private IReadOnlyList<MailAccount> _loadedAccounts = [];
    /// <summary>Which folders are open in the tree, by a key that survives the tree being rebuilt. Rebuilding
    /// used to close every branch, so a reader who had the archive opened out for filing lost it on every
    /// sync; and the layout is saved with the window, so it is there again next time.</summary>
    private readonly HashSet<string> _expandedFolders = new(StringComparer.OrdinalIgnoreCase);
    private bool _repopulatingAccounts;
    private IReadOnlyDictionary<string, DateTimeOffset> _accountLatest = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
    private readonly List<Button> _accountSortButtons = [];
    private AccountSort _accountSort = AccountSort.New;
    private MailAccount? _selectedAccount;
    private string _selectedLocalFolder = LocalFolderKinds.Inbox;
    private MailFolder? _selectedArchive;
    /// <summary>The import whose folders are the tree itself rather than a branch of it. See AddArchiveNodes.</summary>
    private string? _primaryArchive;
    /// <summary>What the message list is showing: a summary per row, or an outgoing message in Drafts and the
    /// Outbox. The list itself holds no rows — see the virtual mode set up in BuildMessageList.</summary>
    private IReadOnlyList<object> _rows = [];
    /// <summary>A folder lists every message it holds. The list is virtual, so a 70,000-message folder costs only
    /// the read; a cap here once left everything older than the newest 1,000 out of reach.</summary>
    private const int WholeFolder = int.MaxValue;
    /// <summary>Replied and forwarded marks for the rows on show, read with them.</summary>
    private IReadOnlyDictionary<long, MessageMarks> _marks = new Dictionary<long, MessageMarks>();
    private IReadOnlyList<SendingIdentity> _rowIdentities = [];
    private Font? _unreadFont;
    private MessageContent? _currentContent;
    private readonly HashSet<ComposeForm> _composes = [];
    private OutgoingMessage? _currentOutgoing;
    private bool _ready;
    private ToolStripButton _archiveToolbar = null!;
    private readonly BufferedSplitContainer _mainSplit;
    private bool _lastMaximized;
    private bool _windowRestored;

    public MainForm(string? root = null)
        : this(root, false) { }

    public MainForm(string? root, bool openWithoutActivation)
    {
        _openWithoutActivation = openWithoutActivation;
        _services = StartupDiagnostics.Time("Services constructor", () => new MailkeepServices(root));
        _attachments = new AttachmentBar(ExtractAnyAttachment);
        _html = new HtmlView(OpenLink);
        _html.ShortcutAction = MainShortcut;
        _imagePermissions = new RemoteImagePermissions(_services.Database);
        _imagePermissions.Changed += (_, _) => { if (_currentContent is { } content) { _html.Clear(); ShowBody(content); } };
        Text = "Inbox - Mailkeep";
        Name = "MailkeepWinForms";
        AccessibleName = "Mailkeep WinForms";
        Icon = StartupDiagnostics.Time("WindowIcon", Dialog.WindowIcon);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1280, 820);
        MinimumSize = new Size(900, 580);
        Opacity = StartupDiagnostics.Skip("OPACITY") ? 1 : 0; // until filled; see Reveal
        // Read before the window first appears, so it opens at its saved size. A new store has no settings
        // table until it is initialised; the layout is then put on later, with everything else.
        WindowLayout? openingLayout = null;
        try
        {
            if (StartupDiagnostics.Time("Read saved window", () => WindowLayout.Read(_services.Database.GetSetting(WindowLayout.Setting))) is { } saved)
            {
                openingLayout = saved;
                // WinForms maximization activates even when ShowWithoutActivation is true.
                // A background launch restores it on the first user activation instead.
                _maximizeOnActivation = _openWithoutActivation && saved.Maximized;
                var placement = _openWithoutActivation ? saved with { Maximized = false } : saved;
                StartupDiagnostics.Time("Restore saved window", () => placement.RestoreWindow(this));
                _windowRestored = true;
            }
        }
        catch (Exception) { }
        if (StartupDiagnostics.Skip("SAVED_WINDOW"))
        {
            WindowState = FormWindowState.Normal;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1280, 820);
            _windowRestored = true;
        }
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = SystemColors.Control;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        KeyPreview = true;

        var menu = BuildMenu();
        var toolbar = BuildToolbar();
        var bands = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Color.FromArgb(47, 94, 160), Margin = Padding.Empty };
        bands.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        bands.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bands.Controls.Add(_folderBand, 0, 0);
        bands.Controls.Add(_identityBand, 1, 0);

        var statusStrip = new StatusStrip { SizingGrip = false };
        statusStrip.Items.AddRange([_status, _syncProgress, _remoteStatus, _count]);
        var main = _mainSplit = new BufferedSplitContainer { Dock = DockStyle.Fill, Margin = Padding.Empty, FixedPanel = FixedPanel.Panel1, BorderStyle = BorderStyle.None, SplitterWidth = 4, BackColor = SystemColors.Control, AccessibleName = "Folders and messages splitter" };
        main.Panel1.Controls.Add(BuildFolderPane());
        main.Panel2.Controls.Add(BuildMessagePane());
        InitializePaneNavigation();
        main.SplitterMoved += (_, _) => { bands.ColumnStyles[0].Width = main.SplitterDistance; SaveLayoutSoon(); };
        _folderSplit.SplitterMoved += (_, _) => SaveLayoutSoon();
        _previewSplit.SplitterMoved += (_, _) => SaveLayoutSoon();
        _messages.ColumnWidthChanged += (_, _) => SaveLayoutSoon();
        _layoutSave.Tick += (_, _) =>
        {
            _layoutSave.Stop();
            if (!_layoutWrite.IsCompleted) { _layoutSave.Start(); return; }
            try
            {
                var layout = CaptureLayout();
                if (layout is not null)
                    _layoutWrite = Task.Run(() =>
                    {
                        try { _services.Database.SetSetting(WindowLayout.Setting, layout); }
                        catch (Exception) { } // retried on another layout change and on closing
                    });
            }
            catch (Exception) { } // tried again at the next move, and on closing
        };

        var content = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(bands, 0, 0);
        content.Controls.Add(main, 0, 1);

        var shell = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, Margin = Padding.Empty, Padding = Padding.Empty };
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.Controls.Add(menu, 0, 0);
        shell.Controls.Add(toolbar, 0, 1);
        shell.Controls.Add(content, 0, 2);
        shell.Controls.Add(statusStrip, 0, 3);
        Controls.Add(shell);
        MainMenuStrip = menu;

        _delete = toolbar.Items.OfType<ToolStripButton>().First(button => button.Text == "Delete");
        _openHtml = toolbar.Items.OfType<ToolStripButton>().First(button => button.Text == "Open HTML");
        ApplyNativeFont(_body, 3);
        _body.LinkClicked += (_, e) => OpenLink(e.LinkText);
        SetReadyControls(false);
        _accounts.ItemSelectionChanged += (_, e) => 
        {
            if (e.IsSelected && e.Item?.Tag is MailAccount account) SelectConnectedAccount(account);
        };
        _accounts.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && _accounts.GetItemAt(e.X, e.Y) is { Tag: MailAccount account } && account == _selectedAccount)
                SelectConnectedAccount(account);
        };
        _localTree.AfterSelect += (_, e) => SelectTreeNode(e.Node);
        _localTree.NodeMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && e.Node == _localTree.SelectedNode) SelectTreeNode(e.Node);
        };
        _localTree.ContextMenuStrip = BuildFolderMenu();
        EnableFolderDragging();
        EnableFolderRenaming();
        EnableMessageReadMenu();
        _messages.SelectedIndexChanged += (_, _) => QueuePreviewUpdate();
        // Native keyboard tooltips retain virtual items from the previous folder and
        // can ask for an index that no longer exists. Keep mouse hints without retaining rows.
        _messages.ItemMouseHover += (_, e) =>
        {
            if (e.Item?.ToolTipText is { Length: > 0 } text)
                _messageHoverTip.Show(text, _messages,
                    _messages.PointToClient(Cursor.Position) + new Size(12, 18), 5000);
        };
        _messages.MouseLeave += (_, _) => _messageHoverTip.Hide(_messages);
        _messages.MouseDown += (_, _) => _messageHoverTip.Hide(_messages);
        _messages.DoubleClick += async (_, _) => await OpenSelectedMessageAsync();
        // A virtual list reports one row changing and a range of rows changing as two different events, and
        // Select All is a range. Without this the rows highlight and nothing else notices.
        _messages.VirtualItemsSelectionRangeChanged += (_, _) => QueuePreviewUpdate();
        // Enter opens too, the way it does in every mail client.
        _messages.KeyDown += async (_, e) => { _messageHoverTip.Hide(_messages); if (e.KeyCode == Keys.Enter) { e.Handled = true; await OpenSelectedMessageAsync(); } };
        Shown += async (_, _) =>
        {
            TimingLog.Mark("shown (transparent)");
            if (Opacity >= 1) StartupDiagnostics.Revealed(this);
            _ = Task.Delay(1500).ContinueWith(_ => Reveal(), TaskScheduler.FromCurrentSynchronizationContext());
            main.SplitterDistance = Math.Min(260, main.Width - 450);
            main.Panel1MinSize = 180;
            main.Panel2MinSize = 450;
            // Local Mail is a handful of folders; the connected list is dozens of accounts, so it takes the larger share.
            _folderSplit.SplitterDistance = Math.Max(120, (int)(_folderSplit.Height * 0.38));
            _folderSplit.Panel1MinSize = 100;
            _folderSplit.Panel2MinSize = 100;
            _folderSplit.FixedPanel = FixedPanel.Panel1;
            _previewSplit.SplitterDistance = Math.Max(180, (int)(_previewSplit.Height * 0.40));
            _previewSplit.Panel1MinSize = 180;
            _previewSplit.Panel2MinSize = 160;
            // The list keeps its height when the window changes size; the reading pane takes the difference.
            // Left proportional, the split crept every time the window was resized or maximised.
            _previewSplit.FixedPanel = FixedPanel.Panel1;
            openingLayout?.Restore(this,
                _mainSplit, _folderSplit, _previewSplit, _messages, _expandedFolders, restoreWindow: !_windowRestored);
            _status.Text = "Opening your mailbox…";
            // Restore queues the saved splitter positions. Reveal after that callback, while data loads.
            BeginInvoke(Reveal);
            await InitializeAsync(openingLayout);
        };
        Resize += (_, _) =>
        {
            if (WindowState != FormWindowState.Minimized) _lastMaximized = WindowState == FormWindowState.Maximized;
        };
        TrackMailActivity();
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip { Dock = DockStyle.Top };
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(Item("Send and &Receive", async () => await SyncAllAsync(), "Send and Receive", "Ctrl+M"));
        file.DropDownItems.Add(Item("Send &Outbox…", async () => await SendOutboxAsync(true), "Send Outbox menu item"));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("E&xit", Close, "Exit"));
        var edit = new ToolStripMenuItem("&Edit");
        edit.DropDownItems.Add(Item("Select &All", async () => await SelectAllMessagesAsync(), "Select All menu item",
            "Ctrl+A"));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Item("&Find…", async () => await ShowSearchPromptAsync(), "Find menu item", "Ctrl+Shift+F"));
        var findText = Item("Find &Text…", () => MainShortcut(Keys.F4)?.Invoke(), "Find Text menu item");
        findText.ShortcutKeyDisplayString = "F4";
        edit.DropDownItems.Add(findText);
        edit.DropDownItems.Add(Item("&Delete", async () => await DeleteAsync(), "Delete menu item", "Ctrl+D"));
        var moveToFolder = Item("&Move to Folder…", MoveMessage, "Move to Folder menu item");
        moveToFolder.ShortcutKeyDisplayString = "Ctrl+Shift+V";
        edit.DropDownItems.Add(moveToFolder);
        var folder = new ToolStripMenuItem("F&older");
        folder.DropDownItems.Add(Item("&New Folder…", NewFolder, "New Folder menu item"));
        folder.DropDownItems.Add(Item("&Rename…", RenameFolder, "Rename Folder menu item"));
        folder.DropDownItems.Add(Item("&Move into…", MoveFolderInto, "Move Folder Into menu item"));
        folder.DropDownItems.Add(new ToolStripSeparator());
        folder.DropDownItems.Add(Item("&Delete", DeleteFolder, "Delete Folder menu item"));
        folder.DropDownOpening += (_, _) =>
        {
            var selected = SelectedFolder();
            folder.DropDownItems[0].Enabled = selected.CanHoldNewFolder;
            folder.DropDownItems[1].Enabled = selected.CanRename;
            folder.DropDownItems[2].Enabled = selected.CanMove;
            folder.DropDownItems[4].Enabled = selected.CanDelete;
        };
        var view = new ToolStripMenuItem("&View");
        view.DropDownItems.Add(Item("&Refresh", RefreshCurrent, "Refresh"));
        var inbox = Item("Go to &Inbox", GoToInbox, "Go to Inbox");
        inbox.ShortcutKeyDisplayString = "Ctrl+I";
        view.DropDownItems.Add(inbox);
        var goToFolder = Item("Go to &Folder…", GoToFolder, "Go to Folder");
        goToFolder.ShortcutKeyDisplayString = "Ctrl+Y";
        view.DropDownItems.Add(goToFolder);
        view.DropDownItems.Add(Item("Fetch &log", ShowFetchLog, "Fetch log menu item"));
        view.DropDownItems.Add(Item("&Download progress", ShowDownloadProgress, "Download progress menu item"));
        var tools = new ToolStripMenuItem("&Tools");
        tools.DropDownItems.Add(Item("&Accounts…", async () => await ShowAccountsAsync(), "Accounts menu item"));
        tools.DropDownItems.Add(Item("Address &Book…", ShowAddressBook, "Address Book menu item"));
        tools.DropDownItems.Add(Item("A&rchive…", ShowArchiveWindow, "Archive menu item"));
        tools.DropDownItems.Add(Item("&Manage Archives…", ShowManageArchives, "Manage Archives menu item"));
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add(Item("Connected account &keep-alive…", ShowKeepAlive, "Connected keep-alive menu item"));
        _autoSyncItem = new ToolStripMenuItem("Refresh connected accounts a&utomatically") { CheckOnClick = true, AccessibleName = "Automatic refresh menu item" };
        _autoSyncItem.CheckedChanged += (_, _) => SetAutoSync(_autoSyncItem.Checked);
        tools.DropDownItems.Add(_autoSyncItem);
        tools.DropDownItems.Add(Item("Search &shortcut…", ChangeSearchShortcut, "Search shortcut menu item"));
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add(Item("&Options…", ShowOptions, "Options menu item"));
        var message = new ToolStripMenuItem("&Message");
        message.DropDownItems.Add(ComposeItem("&New Mail…", async () => await ComposeNewAsync(), "New Mail menu item", false));
        message.DropDownItems.Add(ComposeItem("&Reply", async () => await ReplyAsync(false), "Reply menu item", true));
        message.DropDownItems.Add(ComposeItem("Reply to &All", async () => await ReplyAsync(true), "Reply All menu item", true));
        message.DropDownItems.Add(ComposeItem("F&orward", async () => await ForwardAsync(), "Forward menu item", true));
        message.DropDownItems.Add(new ToolStripSeparator());
        message.DropDownItems.Add(Item("Open &HTML in Brave", OpenHtml, "Open HTML menu item"));
        message.DropDownItems.Add(Item("&Delete", async () => await DeleteAsync(), "Delete message menu item"));
        var serverMove = Item("Move to server folder…", async () => await MoveToServerFolderAsync(), "Move to server folder");
        message.DropDownItems.Add(serverMove);
        message.DropDownItems.Add(new ToolStripSeparator());
        var markRead = ReadStateCommand(true, shortcut: true);
        var markUnread = ReadStateCommand(false, shortcut: true);
        message.DropDownItems.AddRange([markRead, markUnread]);
        var properties = Item("&Properties…", () => MainShortcut(Keys.Alt | Keys.Enter)?.Invoke(), "Message Properties menu item");
        properties.ShortcutKeyDisplayString = "Alt+Enter";
        message.DropDownItems.Add(properties);
        message.DropDownOpening += (_, _) =>
        {
            markRead.Enabled = CanChangeReadState(true);
            properties.Enabled = Current() is MessageSummary;
            var selection = Selected();
            serverMove.Enabled = !_serverMoveRunning && selection.Length > 0 && selection.All(IsConnectedMessage)
                && selection.Select(m => m.Account).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
            markUnread.Enabled = CanChangeReadState(false);
        };
        var help = new ToolStripMenuItem("&Help");
        var helpTopics = Item("Mailkeep &Help", () => MailHelp.Show(this), "Mailkeep Help menu item");
        helpTopics.ShortcutKeyDisplayString = "F1";
        help.DropDownItems.Add(helpTopics);
        help.DropDownItems.Add(Item("&About Mailkeep", () => MessageBox.Show(this, "Mailkeep\nNative Windows mail and local archive client", "About Mailkeep"), "About"));
        menu.Items.AddRange([file, edit, view, folder, tools, message, help]);
        return menu;
    }

    private ToolStrip BuildToolbar()
    {
        var toolbar = _mainToolbar = new ToolStrip
        {
            Dock = DockStyle.Fill,
            GripStyle = ToolStripGripStyle.Hidden,
            ImageScalingSize = new Size(20, 20),
            RenderMode = ToolStripRenderMode.System,
            Font = new Font("Tahoma", 8.25f),
            // Keep the system renderer's two-pixel etched edge below the item hover frames.
            Padding = new Padding(5, 1, 5, 3),
            AccessibleName = "Main toolbar"
        };
        ApplyNativeFont(toolbar);

        // The order a mail toolbar has always had: write, answer, pass on, print, throw away, then file.
        var newMail = CommandItem("New Mail", ToolbarImage(ToolbarGlyph.Mail), async (_, _) => await ComposeNewAsync(), false);
        var reply = CommandItem("Reply", ToolbarImage(ToolbarGlyph.Reply), async (_, _) => await ReplyAsync(false), false);
        var replyAll = CommandItem("Reply All", ToolbarImage(ToolbarGlyph.ReplyAll), async (_, _) => await ReplyAsync(true), false);
        var forward = CommandItem("Forward", ToolbarImage(ToolbarGlyph.Forward), async (_, _) => await ForwardAsync(), false);
        _composeItems.AddRange([newMail, reply, replyAll, forward]);
        _replyItems.AddRange([reply, replyAll, forward]);
        toolbar.Items.Add(newMail);
        toolbar.Items.Add(reply);
        toolbar.Items.Add(replyAll);
        toolbar.Items.Add(forward);
        toolbar.Items.Add(new ToolStripSeparator());
        _print = CommandItem("Print", ToolbarImage(ToolbarGlyph.Print), (_, _) => PrintMessage(), false);
        _move = CommandItem("Move", ToolbarImage(ToolbarGlyph.Move), (_, _) => MoveMessage(), false);
        toolbar.Items.Add(_print);
        toolbar.Items.Add(CommandItem("Delete", ToolbarImage(ToolbarGlyph.Delete), async (_, _) => await DeleteAsync(), false));
        toolbar.Items.Add(_move);
        toolbar.Items.Add(CommandItem("Find", ToolbarImage(ToolbarGlyph.Find), async (_, _) => await ShowSearchPromptAsync()));
        toolbar.Items.Add(new ToolStripSeparator());

        // OE Classic's shape: the button does everything; the drop-down offers All and then each
        // sending-capable account by name. Entries per account are rebuilt as accounts change.
        _receive = new ToolStripSplitButton("Send/Receive", ToolbarImage(ToolbarGlyph.Sync))
        {
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            TextImageRelation = TextImageRelation.ImageAboveText,
            ToolTipText = "Send the Outbox, then receive all accounts",
            Padding = new Padding(16, 0, 16, 0),
            Margin = new Padding(2, 0, 2, 0)
        };
        _receive.ButtonClick += async (_, _) => await SyncAllAsync();
        RebuildReceiveMenu([]);
        _readyItems.Add(_receive);
        toolbar.Items.Add(_receive);

        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(CommandItem("Fetch Log", ToolbarImage(ToolbarGlyph.Log), (_, _) => ShowFetchLog()));
        toolbar.Items.Add(CommandItem("Refresh Cnnct'd", ToolbarImage(ToolbarGlyph.Clock), async (_, _) => await SyncAccountsAsync(true)));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(CommandItem("Open HTML", ToolbarImage(ToolbarGlyph.Html), (_, _) => OpenHtml(), false));

        _archiveToolbar = CommandItem("Archive", SmallImage(SmallGlyph.Folder), (_, _) => ShowArchiveWindow());
        _archiveToolbar.AccessibleName = "Open Archive";
        _archiveToolbar.Visible = false; // Loaded with the other preferences after the store is initialized.
        toolbar.Items.Add(_archiveToolbar);
        return toolbar;
    }

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr handle, string? appName, string? idList);

    private Control BuildFolderPane()
    {
        _localTree.ImageList = NavigationImages();
        // The classic tree — dotted lines, +/− expanders — is what OE itself shows; the themed Win10 tree is
        // the single biggest visual difference between the two.
        _localTree.HandleCreated += (_, _) => SetWindowTheme(_localTree.Handle, "", "");
        _localTree.DrawNode += DrawFolderNode;
        _localTree.AfterExpand += (_, e) => { if (e.Node is not null) _expandedFolders.Add(FolderKey(e.Node)); SaveLayoutSoon(); };
        _localTree.AfterCollapse += (_, e) => { if (e.Node is not null) _expandedFolders.Remove(FolderKey(e.Node)); SaveLayoutSoon(); };
        _accounts.SmallImageList = NavigationImages();
        _accounts.OwnerDraw = true;
        _accounts.Columns.Add("Account", 190);
        _accounts.Columns.Add("Count", 58, HorizontalAlignment.Right);
        _accounts.DrawItem += (_, e) => { if (_accounts.View != View.Details) e.DrawDefault = true; };
        _accounts.DrawSubItem += DrawAccountSubItem;
        _accounts.Resize += (_, _) => ResizeAccountColumns();
        _folderSplit.Panel1.Controls.Add(FolderSection(SectionLabel("Local Mail"), _localTree));
        _folderSplit.Panel2.Controls.Add(FolderSection(BuildAccountHeader(), _accounts));
        return _folderSplit;
    }

    private Control BuildAccountHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        panel.Controls.Add(SectionLabel("Connected Accounts"));
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 117,
            Margin = Padding.Empty,
            Padding = new Padding(0, 1, 2, 1),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.FromArgb(218, 218, 218)
        };
        buttons.Controls.Add(AccountSortButton("A–Z", "Sort connected accounts alphabetically", AccountSort.Alphabetical));
        buttons.Controls.Add(AccountSortButton("Vol", "Sort connected accounts by message volume", AccountSort.Volume));
        buttons.Controls.Add(AccountSortButton("New", "Sort connected accounts by new mail", AccountSort.New));
        panel.Controls.Add(buttons);
        UpdateAccountSortButtons();
        return panel;
    }

    private Button AccountSortButton(string text, string accessibleName, AccountSort sort)
    {
        var button = new Button
        {
            Text = text,
            AccessibleName = accessibleName,
            Tag = sort,
            // Fixed on every axis: the active pill is bold, and autosizing would let it grow past its neighbours.
            AutoSize = false,
            Size = new Size(37, 21),
            MinimumSize = new Size(37, 21),
            MaximumSize = new Size(37, 21),
            Margin = new Padding(1, 0, 0, 0),
            Padding = Padding.Empty,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            Font = new Font("Tahoma", 7.5f),
            TabStop = false
        };
        button.FlatAppearance.BorderColor = SystemColors.ControlDark;
        button.Click += (_, _) => SetAccountSort(sort);
        _accountSortButtons.Add(button);
        return button;
    }

    /// <summary>Re-ordering a list that is already on screen is a view change, not a data change. Reloading
    /// everything for it meant re-counting a quarter of a million archived messages and rebuilding the whole
    /// folder tree — over a second of work to move some rows around.</summary>
    private void SetAccountSort(AccountSort sort)
    {
        if (_accountSort == sort) return;
        _accountSort = sort;
        _ = SettingsWrites.Save(_services.Database, "AccountSort", sort.ToString());
        UpdateAccountSortButtons();
        PopulateAccountList();
    }

    /// <summary>Fills the connected-account list from what is already in hand, in the chosen order.</summary>
    private void PopulateAccountList()
    {
        var selected = _accounts.SelectedItems.Count > 0 ? _accounts.SelectedItems[0].Name : null;
        var focused = _accounts.FocusedItem?.Name;
        _repopulatingAccounts = true;
        try { FillAccountList(selected, focused); }
        finally { _repopulatingAccounts = false; }
    }

    private void FillAccountList(string? selected, string? focused)
    {
        // One width for every count pill — the widest total decides, so the column changes in bulk, never per row.
        var widest = _accountCounts.Values.Select(value => value.Total).DefaultIfEmpty(0).Max();
        _accountBadgeWidth = Math.Max(25, TextRenderer.MeasureText(widest.ToString("N0"),
            new Font(_accounts.Font, FontStyle.Bold), Size.Empty, TextFormatFlags.NoPadding).Width + 12);

        var connected = _loadedAccounts.Where(value => value.IsConnected);
        connected = _accountSort switch
        {
            AccountSort.Volume => connected.OrderByDescending(value => _accountCounts.GetValueOrDefault(value.Name).Total)
                .ThenBy(value => value.Address, StringComparer.OrdinalIgnoreCase),
            // "New" means most recent activity: an account whose newest mail is oldest sinks, whether or not
            // anything there has been read. Unread counts go stale the moment a folder is opened.
            AccountSort.New => connected.OrderByDescending(value => _accountLatest.GetValueOrDefault(value.Name))
                .ThenBy(value => value.Address, StringComparer.OrdinalIgnoreCase),
            _ => connected.OrderBy(value => value.Address, StringComparer.OrdinalIgnoreCase)
        };

        _accounts.BeginUpdate();
        _accounts.Items.Clear();
        foreach (var account in connected)
        {
            var count = _accountCounts.GetValueOrDefault(account.Name);
            var item = new ListViewItem(account.Address)
            {
                Tag = account,
                Name = account.Name,
                ImageKey = "account",
                ToolTipText = AccountCountDescription(account.Name)
            };
            item.SubItems.Add(count.Total.ToString("N0"));
            _accounts.Items.Add(item);
        }
        // Both of these repaint, so they belong inside the update: outside it the pane is drawn three times
        // over — once for the rows, again for the column width, again for the selection.
        ResizeAccountColumns();
        // Re-sorting must not lose which account was being read.
        if (selected is not null && _accounts.Items.Find(selected, false).FirstOrDefault() is { } row)
        {
            row.Selected = true;
            row.EnsureVisible();
        }
        if ((focused ?? selected) is { } focusedName
            && _accounts.Items.Find(focusedName, false).FirstOrDefault() is { } focusedRow)
            focusedRow.Focused = true;
        _accounts.EndUpdate();
    }

    private void UpdateAccountSortButtons()
    {
        foreach (var button in _accountSortButtons)
        {
            var active = button.Tag is AccountSort sort && sort == _accountSort;
            button.BackColor = active ? Color.White : Color.FromArgb(224, 224, 224);
            button.ForeColor = active ? Color.FromArgb(30, 75, 135) : SystemColors.ControlText;
            button.Font = new Font(button.Font, active ? FontStyle.Bold : FontStyle.Regular);
        }
    }

    private static Control FolderSection(Control header, Control content)
    {
        var panel = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty, RowCount = 2 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(header, 0, 0);
        panel.Controls.Add(content, 0, 1);
        return panel;
    }

    private Control BuildMessagePane()
    {
        _messages.SmallImageList = MessageImages();
        // The list keeps no rows of its own: it asks for the ones it is about to draw. A folder of a thousand
        // messages used to mean a thousand ListViewItems built before the first one appeared — five sub-items
        // and, for anything unread, a font of its own, all for twenty rows anybody could see.
        _messages.VirtualMode = true;
        _messages.RetrieveVirtualItem += (_, e) => e.Item = Row(e.ItemIndex);
        // Clicking a heading sorts by it; clicking the same one again turns the order round. Newest first to
        // begin with, which is the order mail is read in.
        _messages.ColumnClick += (_, e) =>
        {
            if (e.Column == _sortColumn) _sortDescending = !_sortDescending;
            else { _sortColumn = e.Column; _sortDescending = e.Column is ReceivedColumn or SizeColumn; }
            Show(_rows);
        };
        // Headings drag into whatever order the reader likes; the order is kept with the window layout. Column
        // indexes stay put whatever the display order, so sorting and the width sums below are unaffected.
        _messages.AllowColumnReorder = true;
        _messages.Columns.Add("From", 210);
        _messages.Columns.Add("Subject", 410);
        _messages.Columns.Add("Account", 120);
        _messages.Columns.Add("Received", 140);
        _messages.Columns.Add("Size", 72);
        // Subject soaks up spare width so the list has no dead space; it never shrinks below its default.
        _messages.Resize += (_, _) =>
        {
            var taken = _messages.Columns[0].Width + _messages.Columns[2].Width + _messages.Columns[3].Width + _messages.Columns[4].Width;
            _messages.Columns[1].Width = Math.Max(410, _messages.ClientSize.Width - taken - 4);
        };
        _previewSplit.Panel1.Controls.Add(_messages);
        // OE's divider: a pale line closing the list, the band, then the reading pane's two-tone sunken edge.
        _previewSplit.Paint += (_, e) =>
        {
            var bar = _previewSplit.SplitterRectangle;
            using var pale = new Pen(Color.FromArgb(227, 227, 227));
            e.Graphics.DrawLine(pale, bar.Left, bar.Top, bar.Right, bar.Top);
            e.Graphics.DrawLine(SystemPens.ControlDark, bar.Left, bar.Bottom - 2, bar.Right, bar.Bottom - 2);
            e.Graphics.DrawLine(SystemPens.ControlDarkDark, bar.Left, bar.Bottom - 1, bar.Right, bar.Bottom - 1);
        };

        // OE's reading pane: the subject in plain blue, then From and To running on one line, the date
        // beneath in the system short form, a thin rule, and the body with room to breathe.
        var preview = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, RowCount = 6, ColumnCount = 1, Padding = new Padding(0, 5, 0, 0), BackColor = Color.White };
        preview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        preview.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        preview.RowStyles.Add(new RowStyle(SizeType.Absolute, 15));
        preview.RowStyles.Add(new RowStyle(SizeType.Absolute, 15));
        preview.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
        preview.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        preview.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        // The subject, and at the far right of the same line the paperclip for a message with files — where
        // OE puts it, rather than a row of buttons under the message.
        var subjectLine = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty, Padding = new Padding(6, 0, 8, 0) };
        subjectLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        subjectLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        subjectLine.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        subjectLine.Controls.Add(_subject, 0, 0);
        _attachments.Anchor = AnchorStyles.Right;
        subjectLine.Controls.Add(_attachments, 1, 0);
        preview.Controls.Add(subjectLine, 0, 0);

        var senderLine = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = Padding.Empty, Padding = new Padding(6, 0, 8, 0) };
        senderLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        senderLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        senderLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        senderLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // The one row is exactly the slot the pane gives it; left to size itself it takes the labels' default
        // height, taller than the slot, and the values sit centred in a row whose bottom is cut off.
        senderLine.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        // To follows From wherever From ends, as OE lays it; a very long sender still leaves To room.
        _from.AutoSize = true;
        _from.Margin = new Padding(0, 1, 3, 0);
        senderLine.SizeChanged += (_, _) => _from.MaximumSize = new Size(Math.Max(120, senderLine.Width * 3 / 5), 0);
        var fromCaption = HeaderCaption("From:");
        var toCaption = HeaderCaption("To:");
        senderLine.Controls.Add(fromCaption, 0, 0);
        senderLine.Controls.Add(_from, 1, 0);
        senderLine.Controls.Add(toCaption, 2, 0);
        senderLine.Controls.Add(_to, 3, 0);
        preview.Controls.Add(senderLine, 0, 1);

        var dateLine = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty, Padding = new Padding(6, 0, 8, 0) };
        dateLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        dateLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        dateLine.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var dateCaption = HeaderCaption("Date:");
        dateLine.Controls.Add(dateCaption, 0, 0);
        dateLine.Controls.Add(_date, 1, 0);
        preview.Controls.Add(dateLine, 0, 2);
        _previewLabels.AddRange([fromCaption, toCaption, dateCaption]);
        SetPreviewLabels(false);

        var rule = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = ClassicTheme.SeparatorGrey, Margin = Padding.Empty };
        var ruleHost = new BufferedPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(6, 0, 8, 0) };
        ruleHost.Controls.Add(rule);
        preview.Controls.Add(ruleHost, 0, 3);

        // Only the active body view remains visible once the HTML runtime has initialized.
        var bodyHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        bodyHost.Controls.Add(_body);
        bodyHost.Controls.Add(_html);
        var reading = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, RowCount = 2, ColumnCount = 1 };
        reading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        reading.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        reading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _remoteImages.VisibleChanged += (_, _) => reading.RowStyles[0].Height = _remoteImages.Visible ? 32 : 0;
        reading.Controls.Add(_remoteImages, 0, 0);
        reading.Controls.Add(bodyHost, 0, 1);
        preview.Controls.Add(reading, 0, 4);
        _previewSplit.Panel2.Controls.Add(preview);
        return _previewSplit;
    }

    private async Task InitializeAsync(WindowLayout? openingLayout)
    {
        try
        {
            _status.Text = "Opening your mailbox…";
            if (!StartupDiagnostics.Skip("WEBVIEW")) _ = StartupDiagnostics.TimeAsync("StartReadingPaneAsync", StartReadingPaneAsync);
            var startup = await Task.Run(() =>
            {
                _services.Database.Initialize();
                return (Layout: openingLayout ?? WindowLayout.Read(_services.Database.GetSetting(WindowLayout.Setting)),
                    Sort: _services.Database.GetSetting("AccountSort"),
                    ShowArchive: OptionsForm.ToolbarSettings.ShowArchive(_services.Database));
            });
            TimingLog.Mark("store opened");
            if (IsDisposed || Disposing) return;
            var layout = startup.Layout;
            _archiveToolbar.Visible = startup.ShowArchive;
            ToolbarOrder.Attach(_mainToolbar, _services.Database);
            if (openingLayout is null) layout?.Restore(this,
                _mainSplit, _folderSplit, _previewSplit, _messages, _expandedFolders, restoreWindow: !_windowRestored);
            if (Enum.TryParse<AccountSort>(startup.Sort, out var loadedSort)) _accountSort = loadedSort;
            var sidebar = await Task.Run(() => LoadSidebarData(fromSaved: true));
            TimingLog.Mark("folders read");
            if (IsDisposed || Disposing) return;
            StartupDiagnostics.Time("Sidebar.Render", () => ReloadAccounts(sidebar));
            // Restore the last mailbox; older or missing choices fall back to Inbox.
            var connected = _accounts.Items.Cast<ListViewItem>().FirstOrDefault(item => item.Tag is MailAccount account && account.Name == layout?.SelectedAccount);
            if (connected?.Tag is MailAccount selectedAccount)
            {
                _repopulatingAccounts = true;
                try
                {
                    _localTree.SelectedNode = null; _selectedAccount = selectedAccount; _selectedArchive = null;
                    connected.Selected = true; connected.Focused = true; connected.EnsureVisible();
                }
                finally { _repopulatingAccounts = false; }
            }
            else
                _localTree.SelectedNode = Walk(_localTree.Nodes).FirstOrDefault(node => FolderKey(node) == layout?.SelectedFolder)
                    ?? _localTree.Nodes.Find("LocalFolder:" + LocalFolderKinds.Inbox, true).FirstOrDefault();
            // Fill the chosen mailbox once. Tree construction must not briefly show the default Inbox first.
            TimingLog.Mark("folder tree drawn");
            await LoadMailboxAsync(duringStartup: true);
            TimingLog.Mark("mailbox listed");
            if (IsDisposed || Disposing) return;
            await ShowSelectedMessageCoreAsync(waitForReadMark: false);
            if (IsDisposed || Disposing) return;
            TimingLog.Mark("first message read");
            _ready = true;
            SetReadyControls(true);
            UpdateComposeState();
            if (!StartupDiagnostics.Skip("HOTKEY")) StartupDiagnostics.Time("RegisterSearchHotkey", RegisterSearchHotkey);
            // Find is made now, while nothing is waiting, so the first Ctrl+F is as quick as the rest.
            if (!StartupDiagnostics.Skip("FIND")) BeginInvoke(async () =>
            {
                await StartupDiagnostics.TimeAsync("RefreshFindFoldersAsync", RefreshFindFoldersAsync);
                if (!IsDisposed) StartupDiagnostics.Time("FindForm creation", () => FindForm());
            });
            // Search setup and indexing must not keep the mailbox from opening.
            if (!StartupDiagnostics.Skip("INDEX")) _ = Task.Run(() => StartupDiagnostics.Time("Catalog.SyncOperational", () => _services.Catalog.SyncOperational()));
            if (!StartupDiagnostics.Skip("SYNC")) StartupDiagnostics.Time("StartAutoSync", StartAutoSync);
            StartRemoteOperations();
            StartMessageMarksRefresh();
            if (!StartupDiagnostics.Skip("RECOUNT")) _ = StartupDiagnostics.TimeAsync("RecountAfterStartupAsync", () => RecountAfterStartupAsync(sidebar));
            StartupDiagnostics.Mark("ready");
            StartupDiagnostics.Resources(this, "ready");
        }
        catch (Exception exception) { if (!IsDisposed && !Disposing) { Reveal(); ShowError("Mailkeep could not start", exception); } }
        finally { Reveal(); }
    }

    /// <summary>Show the restored window while its mailbox opens. Data and preview readiness are separate
    /// from visibility; the opening status and disabled actions remain until initialization finishes.</summary>
    private void Reveal()
    {
        if (IsDisposed || Disposing || Opacity >= 1) return;
        Opacity = 1;
        StartupDiagnostics.Revealed(this);
        TimingLog.Mark(_ready ? "revealed" : "revealed while opening mailbox");
        // Idle comes once the queue is empty, after the window's paints.
        Application.Idle += Painted;
        static void Painted(object? sender, EventArgs e)
        {
            Application.Idle -= Painted;
            TimingLog.Mark("painted");
            StartupDiagnostics.Mark("application idle after reveal (not presentation)");
        }
    }

    private async Task StartReadingPaneAsync()
    {
        await _html.StartAsync(Path.Combine(_services.Paths.Root, "reading-pane"));
        TimingLog.Mark("reading pane started");
        if (!IsDisposed && !Disposing && _currentContent is { } content) ShowBody(content);
    }

    private sealed record SidebarData(IReadOnlyList<MailAccount> Accounts,
        IReadOnlyDictionary<string, (int Total, int New)> AccountCounts,
        IReadOnlyDictionary<string, int> AccountUnreadCounts,
        IReadOnlyDictionary<string, DateTimeOffset> Latest,
        IGrouping<string, (string Account, string Folder, int Total, int Unread)>[] Archived,
        IReadOnlyList<LocalFolder> Folders, int OutboxCount, ReadCounts ReadCounts, IReadOnlySet<string> WritableFolders);

    // Read and adopt folders on a worker at startup; only building native controls belongs on the UI thread.
    private SidebarData LoadSidebarData() => LoadSidebarData(fromSaved: false);

    /// <summary>With <paramref name="fromSaved"/>, the import's folder counts are the ones written down last
    /// time rather than counted again. Counting them reads a quarter of a million imported rows: from a warm
    /// disk cache that is half a second, from a cold one five to fifteen, and the whole window waited on it.
    /// The import barely changes between one run and the next, so the tree opens on what it last showed and
    /// is counted afresh once the window is in use.</summary>
    private SidebarData LoadSidebarData(bool fromSaved)
    {
        var saved = fromSaved ? StartupDiagnostics.Time("Sidebar.SavedCounts", ReadSavedCounts) : null;
        var accounts = StartupDiagnostics.Time("Sidebar.Accounts", _services.Accounts.Load);
        IReadOnlyList<(string Account, string Folder, int Total, int Unread)> folderCounts =
            saved?.Folders.Select(folder => (folder.Account, folder.Folder, folder.Total, folder.Unread)).ToArray()
            ?? StartupDiagnostics.Time("Sidebar.FolderCounts", () => _services.Database.FolderCounts(MailContract.ArchiveSource));
        var archived = ArchivedByAccount(folderCounts);
        StartupDiagnostics.Time("Sidebar.AdoptFolders", () => AdoptPrimaryArchiveFolders(archived));
        var scoped = StartupDiagnostics.Time("Sidebar.LocalScope", LocalScopedAccounts);
        var readCounts = saved is null
            ? LoadReadCounts([], false, scoped)
            : new ReadCounts(new Dictionary<long, bool>(), null, StartupDiagnostics.Time("Sidebar.LocalCounts", () => _services.Database.LocalFolderCounts(scoped)),
                saved.ArchiveUnread.ToDictionary(folder => (folder.Account, folder.Folder), folder => folder.Unread));
        if (saved is null) _archiveFolderCounts = folderCounts;
        return new(accounts, StartupDiagnostics.Time("Sidebar.AccountCounts", _services.Database.AccountMessageCounts),
            _services.Database.AccountUnreadCounts(),
            StartupDiagnostics.Time("Sidebar.LatestDates", _services.Database.AccountLatestDates),
            archived, StartupDiagnostics.Time("Sidebar.Folders", _services.Database.ListLocalFolders),
            StartupDiagnostics.Time("Sidebar.Outbox", () => _services.Database.ListOutgoing(OutgoingStates.Outbox, 1000).Count),
            readCounts, _services.Database.FolderStorage.Inspect().Folders.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The import's folder counts as last drawn, written down when the window closes.</summary>
    private const string SavedCountsSetting = "startup.archive-counts";

    private sealed record SavedFolderCount(string Account, string Folder, int Total, int Unread);
    private sealed record SavedCounts(SavedFolderCount[] Folders, SavedFolderCount[] ArchiveUnread);

    /// <summary>The import's folders and unread counts as last counted, for writing down at close. Unset until
    /// they have been counted in this run, so a start that only read them back never writes them again.</summary>
    private IReadOnlyList<(string Account, string Folder, int Total, int Unread)>? _archiveFolderCounts;
    private IReadOnlyDictionary<(string Account, string Folder), int>? _archiveUnreadCounts;

    private SavedCounts? ReadSavedCounts()
    {
        try
        {
            return _services.Database.GetSetting(SavedCountsSetting) is { } json
                ? System.Text.Json.JsonSerializer.Deserialize<SavedCounts>(json) : null;
        }
        catch (Exception) { return null; } // counted afresh instead
    }

    private void SaveCounts()
    {
        if (_archiveFolderCounts is not { } folders || _archiveUnreadCounts is not { } unread) return;
        var saved = new SavedCounts(
            folders.Select(folder => new SavedFolderCount(folder.Account, folder.Folder, folder.Total, folder.Unread)).ToArray(),
            unread.Select(pair => new SavedFolderCount(pair.Key.Account, pair.Key.Folder, 0, pair.Value)).ToArray());
        _services.Database.SetSetting(SavedCountsSetting, System.Text.Json.JsonSerializer.Serialize(saved));
    }

    /// <summary>Counts the import afresh once the window is in use, and rebuilds the tree only if its folders
    /// changed; otherwise only the counts that moved are redrawn. Left a while after opening, since the count
    /// holds the store's cache and the first clicks would wait on it.</summary>
    private async Task RecountAfterStartupAsync(SidebarData opened)
    {
        await Task.Delay(TimeSpan.FromSeconds(20));
        if (IsDisposed || Disposing) return;
        SidebarData fresh;
        try { fresh = await Task.Run(() => TimingLog.Time("recount after opening, in the background", LoadSidebarData)); }
        catch (Exception) { return; } // the next rebuild of the tree counts again
        if (IsDisposed || Disposing) return;
        static IEnumerable<(string, string)> Shape(SidebarData data)
            => data.Archived.SelectMany(group => group.Select(folder => (folder.Account, folder.Folder)));
        if (!Shape(opened).SequenceEqual(Shape(fresh))) ReloadAccounts(fresh);
        else
        {
            ++_readCountsGeneration;
            ApplyReadCounts(fresh.ReadCounts);
        }
        try { await Task.Run(SaveCounts); }
        catch (Exception) { } // written again at close
    }

    private int _sidebarReloadGeneration;

    private async Task ReloadAccountsAsync()
    {
        var generation = ++_sidebarReloadGeneration;
        var data = await Task.Run(LoadSidebarData);
        if (IsDisposed || Disposing || generation != _sidebarReloadGeneration) return;
        ReloadAccounts(data);
    }

    // Rendering cannot start a recount: callers must obtain the data off the UI thread.
    private void ReloadAccounts(SidebarData data)
    {
        ++_sidebarReloadGeneration; // Any completed render supersedes older pending sidebar reads.
        var selected = _selectedAccount?.IsConnected == true ? _selectedAccount.Name : null;
        var selectedFolder = _localTree.SelectedNode is { } selectedNode ? FolderKey(selectedNode) : null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var accounts = data.Accounts;
        _loadedAccounts = accounts;
        _accountCounts = data.AccountCounts;
        _accountUnreadCounts = data.AccountUnreadCounts;
        _accountLatest = data.Latest;
        RebuildReceiveMenu(accounts.Where(value => !value.IsConnected).OrderBy(value => value.Address, StringComparer.OrdinalIgnoreCase).ToArray());
        StartupDiagnostics.Time("Sidebar.RenderAccountList", PopulateAccountList);
        _localTree.BeginUpdate();
        _localTree.Nodes.Clear();
        var root = new TreeNode("All Local Mail") { Tag = "local-all", Name = "AllLocalMail", ImageKey = "folder", SelectedImageKey = "folder-open" };
        var folderCounts = data.ReadCounts.Local;
        var localFolders = data.Folders;
        // Drafts carries no count: a draft is never unread, and a number there read as unread mail over a
        // folder holding the imported drafts as well. The Outbox keeps its queued count — that mail is waiting.
        var outgoingCounts = new Dictionary<string, (int Total, int Unread)>(StringComparer.OrdinalIgnoreCase)
        {
            [LocalFolderKinds.Drafts] = (0, 0),
            [LocalFolderKinds.Outbox] = (data.OutboxCount, 0)
        };
        var folderNodes = localFolders.ToDictionary(folder => folder.Key, folder =>
        {
            var count = outgoingCounts.TryGetValue(folder.Key, out var queued) ? queued : folderCounts.GetValueOrDefault(folder.Key);
            var node = new TreeNode(count.Total > 0 ? $"{folder.Name}  ({count.Total})" : folder.Name)
            {
                Tag = folder,
                Name = "LocalFolder:" + folder.Key,
                ImageKey = FolderIcon(folder.Kind),
                SelectedImageKey = FolderIcon(folder.Kind)
            };
            if (count.Unread > 0) node.NodeFont = new Font(_localTree.Font, FontStyle.Bold);
            return node;
        }, StringComparer.OrdinalIgnoreCase);
        foreach (var folder in localFolders)
            if (folder.ParentKey is not null && folderNodes.TryGetValue(folder.ParentKey, out var parent)) parent.Nodes.Add(folderNodes[folder.Key]);
            else root.Nodes.Add(folderNodes[folder.Key]);
        // The folders adopted from the import, by the import path each one stands for.
        var mirrored = localFolders.Where(folder => folder.Origin == ArchiveOrigin && folder.OriginKey is not null)
            .ToDictionary(folder => folder.OriginKey!, folder => (Node: folderNodes[folder.Key], folder.Key), StringComparer.OrdinalIgnoreCase);
        AddArchiveNodes(root, folderNodes, mirrored, data.Archived);
        // Import provenance remains on the folder, but a converted folder uses the ordinary local list.
        foreach (var folder in localFolders.Where(f => data.WritableFolders.Contains(f.Key)))
            folderNodes[folder.Key].Tag = folder;
        SortLocalFolderNodes(root);
        root.Expand();
        // Open what was open: the tree is rebuilt after every sync and on every account change, and a
        // reader filing mail wants the branches they opened to stay opened.
        foreach (var node in Walk(root.Nodes)) if (_expandedFolders.Contains(FolderKey(node))) node.Expand();
        // Counted onto the nodes before they are on screen; see ApplyReadCounts.
        ++_readCountsGeneration;
        ApplyReadCounts(data.ReadCounts, root.Nodes);
        _localTree.Nodes.Add(root);
        _localTree.EndUpdate();
        _selectedAccount = selected is null ? null : accounts.FirstOrDefault(value => value.IsConnected && value.Name == selected);
        if (_selectedAccount?.IsConnected == true)
        {
            // Braces matter here: an unbraced else after this loop's if binds to the INNER if, running the
            // tree-selection branch for every non-matching row — its AfterSelect nulled _selectedAccount
            // mid-loop and the next comparison crashed. That was the delete-from-connected-account bug.
            var selectedName = _selectedAccount.Name;
            _accounts.SelectedItems.Clear();
            foreach (ListViewItem item in _accounts.Items)
            {
                if (((MailAccount)item.Tag!).Name == selectedName) { item.Selected = true; item.Focused = true; break; }
            }
        }
        else
        {
            var nodeName = _selectedLocalFolder == MailContract.ArchiveSource ? "OeArchive"
                : _selectedLocalFolder == "local-all" ? "AllLocalMail" : "LocalFolder:" + _selectedLocalFolder;
            var selectedNodes = _localTree.Nodes.Find(nodeName, true);
            _localTree.SelectedNode = Walk(_localTree.Nodes).FirstOrDefault(node => FolderKey(node) == selectedFolder)
                ?? selectedNodes.FirstOrDefault() ?? root;
        }
        if (_ready) _ = RefreshFindFoldersAsync();
        if (_ready) TimingLog.Mark($"folder tree rebuilt: drawn {clock.ElapsedMilliseconds} ms");
    }

    private static void SortLocalFolderNodes(TreeNode root, bool topLevel = true)
    {
        static string Name(TreeNode node) => node.Tag switch
        {
            LocalFolder folder => folder.Name,
            MailFolder folder => folder.Display,
            _ => node.Text
        };
        static int StandardOrder(TreeNode node) => (node.Tag switch
        {
            LocalFolder folder => folder.Kind,
            MailFolder folder => folder.LocalKey ?? StandardFolderKey(folder.Display),
            _ => null
        }) switch
        {
            LocalFolderKinds.Inbox => 0,
            LocalFolderKinds.Drafts => 1,
            LocalFolderKinds.Outbox => 2,
            LocalFolderKinds.Sent => 3,
            LocalFolderKinds.Deleted => 4,
            "junk" => 5,
            _ => 6
        };
        // Sort after merging: restored/native folders and imported siblings arrive in separate passes.
        var ordered = root.Nodes.Cast<TreeNode>()
            .OrderBy(node => topLevel ? StandardOrder(node) : 0)
            .ThenBy(Name, StringComparer.InvariantCulture).ToArray();
        root.Nodes.Clear();
        root.Nodes.AddRange(ordered);
        foreach (var node in ordered) SortLocalFolderNodes(node, topLevel: false);
    }

    /// <summary>The imported folders are the folder tree, not a compartment inside it. A migrated mailbox
    /// should look like the mailbox it came from, so the standard folders merge into the ones already here —
    /// an Inbox is an Inbox — and everything else keeps the shape and nesting it had.</summary>
    private const string ArchiveOrigin = "archive";

    /// <summary>The imports, grouped by account, with the one that is this mailbox continued first. One import
    /// is this mailbox continued — its Inbox is the Inbox — and its folders merge into the tree. Any other
    /// import was a separate mailbox and keeps its own branch. Picking by name sorted first was wrong the
    /// moment a second archive arrived: importing one called "Alpha" took the mailbox over and scattered
    /// its folders through the standard ones. The mailbox is the one that brought the standard folders with it.</summary>
    private static IGrouping<string, (string Account, string Folder, int Total, int Unread)>[] ArchivedByAccount(
        IEnumerable<(string Account, string Folder, int Total, int Unread)> folderCounts)
    {
        var archived = folderCounts
            .GroupBy(folder => folder.Account, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (archived.Length == 0) return archived;
        var primary = archived
            .OrderByDescending(group => group.Count(folder => StandardFolderKey(folder.Folder.Split('/')[0]) is not null))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .First();
        return archived.Where(group => !ReferenceEquals(group, primary)).Prepend(primary).ToArray();
    }

    /// <summary>Gives every folder of the primary import a folder of the mailbox's own behind it, so that mail
    /// can be filed where the imported mail already sits: a reader who kept a tree of folders in the other
    /// client goes on filing into that tree here. Each is made once, remembered by the import path it stands
    /// for, and merged with the imported folder when the tree is drawn. The standard folders need none; a
    /// folder beneath one of them goes beneath the mailbox's own.</summary>
    private void AdoptPrimaryArchiveFolders(IGrouping<string, (string Account, string Folder, int Total, int Unread)>[] archived)
    {
        if (archived.Length == 0) return;
        var primary = archived[0];
        var existing = _services.Database.ListLocalFolders()
            .Where(folder => folder.Origin == ArchiveOrigin && folder.OriginKey is not null)
            .ToDictionary(folder => folder.OriginKey!, folder => folder.Key, StringComparer.OrdinalIgnoreCase);
        // Adopt in the same culture-aware order used to display the merged folder tree.
        foreach (var path in primary.Select(folder => folder.Folder).OrderBy(folder => folder, StringComparer.InvariantCulture))
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) continue;
            string? parentKey = null;
            var walked = "";
            for (var depth = 0; depth < segments.Length; depth++)
            {
                walked = depth == 0 ? segments[0] : walked + "/" + segments[depth];
                if (depth == 0 && StandardFolderKey(segments[0]) is { } standard)
                {
                    // Junk has no folder of the mailbox's own; nothing can be filed beneath it.
                    if (standard == "junk") break;
                    parentKey = standard;
                    continue;
                }
                var originKey = primary.Key + "|" + walked;
                if (!existing.TryGetValue(originKey, out var key))
                {
                    try { key = _services.Database.CreateLocalFolder(segments[depth], parentKey, ArchiveOrigin, originKey).Key; }
                    catch (InvalidOperationException) { break; } // a native folder of that name is already there
                    existing[originKey] = key;
                }
                parentKey = key;
            }
        }
    }

    private void AddArchiveNodes(TreeNode root, IReadOnlyDictionary<string, TreeNode> standardFolders,
        IReadOnlyDictionary<string, (TreeNode Node, string Key)> mirrored,
        IGrouping<string, (string Account, string Folder, int Total, int Unread)>[] archived)
    {
        if (archived.Length == 0) return;
        var primary = archived[0];
        // Remembered so that dropping one of its folders on "All Local Mail" takes that folder back out to
        // the top of the mailbox: the primary import has no heading of its own to drop on, being the tree.
        _primaryArchive = primary.Key;
        Merge(primary.Key, primary, root, standardFolders, mirrored);
        foreach (var extra in archived.Skip(1))
        {
            // Named so a folder can be dragged back out to the top of the mailbox it came from.
            var node = new TreeNode(extra.Key) { Name = ArchiveRootName(extra.Key), ImageKey = "folder", SelectedImageKey = "folder-open" };
            Merge(extra.Key, extra, node, new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, (TreeNode, string)>(StringComparer.OrdinalIgnoreCase));
            root.Nodes.Add(node);
        }
    }

    private void Merge(string account, IEnumerable<(string Account, string Folder, int Total, int Unread)> folders,
        TreeNode root, IReadOnlyDictionary<string, TreeNode> standardFolders,
        IReadOnlyDictionary<string, (TreeNode Node, string Key)> mirrored)
    {
        var branches = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
        // Custom folders sort alphabetically beneath the standard ones, which keep their own order. The
        // comparison is culture-aware so punctuation leads, putting _Pinned above OE6 Folders as it does in
        // the client this came from; an ordinal sort would put the underscore after every letter.
        foreach (var folder in folders.OrderBy(folder => folder.Folder, StringComparer.InvariantCulture))
        {
            var segments = folder.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) continue;
            var parent = root;
            var walked = "";
            for (var depth = 0; depth < segments.Length; depth++)
            {
                walked = depth == 0 ? segments[0] : walked + "/" + segments[depth];
                var standard = depth == 0 ? StandardFolderKey(segments[0]) : null;
                if (standard is not null && standardFolders.TryGetValue(standard, out var existing))
                {
                    // The mailbox already has this folder: the imported mail joins it rather than sitting
                    // in a second Inbox beside the first.
                    branches[walked] = existing;
                    var display = existing.Text.Split("  (")[0];
                    // The path walked so far, not the whole row: a row for Inbox/Receipts would otherwise
                    // leave the Inbox itself claiming to be its own child.
                    existing.Tag = new MailFolder(standard, account, walked, display);
                    // Drafts and the Outbox keep the count of the mailbox's own queued mail: that is what the
                    // reader is waiting on, not how many drafts the other client once held.
                    if (segments.Length == 1 && standard is not (LocalFolderKinds.Drafts or LocalFolderKinds.Outbox))
                        Label(existing, display, folder.Total, folder.Unread);
                    parent = existing;
                    continue;
                }
                if (mirrored.TryGetValue(account + "|" + walked, out var adopted))
                {
                    // The mailbox has a folder of its own standing for this imported one: the two are one
                    // folder here, the mailbox's mail and the imported mail together, as with the Inbox.
                    branches[walked] = adopted.Node;
                    var name = adopted.Node.Text.Split("  (")[0];
                    adopted.Node.Tag = new MailFolder(adopted.Key, account, walked, name);
                    parent = adopted.Node;
                    continue;
                }
                if (!branches.TryGetValue(walked, out var node))
                {
                    // An imported folder the mailbox has no counterpart for — Junk Email, say — still earns
                    // its own icon when it is one of the standard ones.
                    var icon = depth == 0 ? FolderIcon(StandardFolderKey(segments[0]) ?? "") : "folder";
                    node = new TreeNode(segments[depth]) { ImageKey = icon, SelectedImageKey = icon };
                    // Every node says which folder it is, including the ones only walked through on the way
                    // to somewhere else. A folder that holds nothing itself — OE6 Folders, whose mail all
                    // lives in its children — used to be tagged with nothing at all, so it could not be
                    // clicked and nothing could be dropped into it: it looked like a folder and behaved
                    // like a label.
                    // No local key: this is an imported folder and nothing else. Only a node the mailbox
                    // already owned — matched above — is both, and it is the one that must not be moved.
                    node.Tag = new MailFolder(null, account, walked, segments[depth]);
                    branches[walked] = node;
                    parent.Nodes.Add(node);
                }
                parent = node;
            }
            // Unread in bold with its count, the way every mail client has shown it. Not for Drafts or the
            // Outbox: their count is the mailbox's own queued mail, set when the tree was built.
            if (parent.Tag is not MailFolder { LocalKey: LocalFolderKinds.Drafts or LocalFolderKinds.Outbox })
                Label(parent, segments[^1], folder.Total, folder.Unread);
        }
    }

    /// <summary>OE's folder label: the name in black — bold when unread — and only the unread count shown,
    /// in blue, drawn by DrawFolderNode. A folder with nothing unread is just its name.</summary>
    /// <summary>A name for a folder node that is the same every time the tree is built: the folder it
    /// stands for, not the TreeNode object, which is new each time.</summary>
    private static string FolderKey(TreeNode node) => node.Tag switch
    {
        LocalFolder folder => "local:" + folder.Key,
        MailFolder folder => "archive:" + folder.Account + "/" + folder.ArchiveFolder,
        _ => string.IsNullOrEmpty(node.Name) ? "node:" + node.Text.Split("  (")[0] : "node:" + node.Name
    };

    private static IEnumerable<TreeNode> Walk(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            yield return node;
            foreach (var child in Walk(node.Nodes)) yield return child;
        }
    }

    private void Label(TreeNode node, string name, int total, int unread)
    {
        node.Text = unread > 0 ? $"{name}  ({unread})" : name;
        node.NodeFont = unread > 0 ? new Font(_localTree.Font, FontStyle.Bold) : null;
        node.ForeColor = SystemColors.WindowText;
    }

    /// <summary>Draws a folder as OE does: black name, blue count. Two colours in one node is the one thing
    /// a stock TreeView cannot render.</summary>
    private void DrawFolderNode(object? sender, DrawTreeNodeEventArgs e)
    {
        if (e.Node is null || e.Bounds.Height <= 0 || e.Bounds.Width <= 0) { e.DrawDefault = true; return; }
        var font = e.Node.NodeFont ?? _localTree.Font;
        var text = e.Node.Text;
        var name = text;
        var count = "";
        var split = text.LastIndexOf("  (", StringComparison.Ordinal);
        if (split > 0 && text.EndsWith(')')) { name = text[..split]; count = text[(split + 2)..]; }

        // The bounds handed over are measured with the tree's regular font; a bold name plus the count
        // overruns them and gets clipped mid-parenthesis. Measure what will really be drawn and paint that.
        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        // Measured against OE's own tree, pixel for pixel: the count is in the regular weight even beside a
        // bold name — drawn bold it reads as a deeper blue — and its bracket starts seven pixels after the
        // name's last stroke. Without padding, so the gap is set here rather than by the renderer's margins.
        var countFont = _localTree.Font;
        const TextFormatFlags tight = flags | TextFormatFlags.NoPadding;
        const int countGap = 6;
        var nameWidth = TextRenderer.MeasureText(e.Graphics, name, font, Size.Empty, flags).Width;
        var nameInk = TextRenderer.MeasureText(e.Graphics, name, font, Size.Empty, tight).Width;
        var countWidth = count.Length > 0 ? TextRenderer.MeasureText(e.Graphics, count, countFont, Size.Empty, tight).Width : 0;
        var nameLeft = (nameWidth - nameInk) / 2;
        var area = new Rectangle(e.Bounds.X, e.Bounds.Y,
            countWidth > 0 ? nameLeft + nameInk + countGap + countWidth + 3 : nameWidth + 2, e.Bounds.Height);
        e.Graphics.ResetClip();
        var selected = (e.State & TreeNodeStates.Selected) != 0;
        // A folder being dragged over reads as selected while the drag lasts, without actually selecting
        // (and so opening) it.
        if (ReferenceEquals(e.Node, _dropTarget)) selected = true;
        using (var background = new SolidBrush(selected ? SystemColors.Highlight : _localTree.BackColor))
            e.Graphics.FillRectangle(background, area);
        TextRenderer.DrawText(e.Graphics, name, font,
            new Rectangle(area.X, area.Y, nameWidth + 2, area.Height),
            selected ? SystemColors.HighlightText : SystemColors.WindowText, flags);
        if (count.Length > 0)
            TextRenderer.DrawText(e.Graphics, count, countFont,
                new Rectangle(area.X + nameLeft + nameInk + countGap, area.Y, countWidth + 2, area.Height),
                selected ? SystemColors.HighlightText : ClassicTheme.CountBlue, tight);
        if ((e.State & TreeNodeStates.Focused) != 0)
            ControlPaint.DrawFocusRectangle(e.Graphics, area);
    }

    /// <summary>The standard folders are told apart by shape, not by reading them: everything else is a folder.
    /// </summary>
    private static string FolderIcon(string kind) => kind switch
    {
        LocalFolderKinds.Inbox => "mail",
        LocalFolderKinds.Outbox => "outbox",
        LocalFolderKinds.Sent => "sent",
        LocalFolderKinds.Deleted => "deleted",
        LocalFolderKinds.Drafts => "drafts",
        "junk" => "junk",
        _ => "folder"
    };

    /// <summary>Which of this mailbox's own folders an imported folder is, judged by the names mail clients
    /// have always used for them.</summary>
    private static string? StandardFolderKey(string name) => name.ToLowerInvariant() switch
    {
        "inbox" => LocalFolderKinds.Inbox,
        "outbox" => LocalFolderKinds.Outbox,
        "sent items" or "sent" => LocalFolderKinds.Sent,
        "deleted items" or "deleted" => LocalFolderKinds.Deleted,
        "drafts" => LocalFolderKinds.Drafts,
        // Junk has no counterpart in this mailbox yet, but it is still a standard folder to look at.
        "junk email" or "junk" => "junk",
        _ => null
    };

    /// <summary>A folder the user clicks: this mailbox's own folder, imported mail, or both at once.</summary>
    private sealed record MailFolder(string? LocalKey, string Account, string ArchiveFolder, string Display);

    /// <summary>Which click the list is currently being filled for. Clicking down a tree starts a load per
    /// folder and they need not come back in order; a load that is no longer the current one is dropped rather
    /// than allowed to fill the list with the folder the user has already moved on from.</summary>
    private int _loadGeneration;

    /// <summary>Opening a folder reads the database, and reading a database of this size is not instant on the
    /// first touch of a folder, whatever the indexes say. Done on the calling thread it froze the window —
    /// the tree would not even repaint the folder as selected until the mail arrived. The reading happens off
    /// the thread; only the filling of the list happens on it.</summary>
    private void LoadMessages() => _ = LoadMessagesAsync();

    private Task LoadMessagesAsync() => LoadMailboxAsync(duringStartup: false);

    private async Task LoadMailboxAsync(bool duringStartup)
    {
        if (IsDisposed || Disposing || (!_ready && !duringStartup)) return;
        var generation = ++_loadGeneration;
        // A refresh requested while filing must read the committed destination, not resurrect
        // the source rows that were already removed from the list.
        try { await _filing; } catch (Exception) { }
        if (IsDisposed || Disposing || generation != _loadGeneration) return;
        try
        {
            if (_selectedArchive is { } archived)
            {
                // A merged folder shows this mailbox's own mail and the imported mail together, newest first —
                // which is the whole point of the two being the same folder. Drafts and the Outbox are the
                // exception: this mailbox's own are queued mail rather than messages, so they come first, in
                // the order they were saved, with the imported copies, read-only, beneath them. Before this
                // the import's Drafts folder took the node over and the mailbox's own drafts were nowhere.
                var key = archived.LocalKey;
                var outgoingState = key switch
                {
                    LocalFolderKinds.Drafts => OutgoingStates.Draft,
                    LocalFolderKinds.Outbox => OutgoingStates.Outbox,
                    _ => null
                };
                var (imported, live, queued, here, gone) = await Task.Run(() =>
                {
                    var scoped = LocalScopedAccounts().ToHashSet(StringComparer.OrdinalIgnoreCase);
                    return (
                        _services.Database.ListMessagesForAccounts([archived.Account], archived.ArchiveFolder, WholeFolder,
                            MailContract.ArchiveSource),
                        key is null || outgoingState is not null
                            ? (IReadOnlyList<MessageSummary>)[]
                            : _services.Database.ListMessagesForAccounts(scoped.ToArray(), key, WholeFolder),
                        key is null
                            ? (IReadOnlyList<OutgoingMessage>)[]
                            : outgoingState == OutgoingStates.Outbox ? _services.Database.ListOutgoing(outgoingState, 500)
                            : _services.Database.ListDraftsInFolder(key, 500),
                        // What this mailbox holds of its own, in any folder: an imported copy of any of it is
                        // the same message twice, wherever the mailbox's copy has since been filed.
                        _services.Database.ListHeads(MailContract.LiveSource).Where(head => scoped.Contains(head.Account))
                            .Select(head => (head.Subject, head.Sender, head.DateUtc)).ToHashSet(),
                        // And what it has deleted outright: a deletion made here is not undone by the import.
                        _services.Database.LocalDeletionKeys(MailContract.LiveSource, MailContract.LocalCopyAccount));
                });
                if (IsDisposed || Disposing || generation != _loadGeneration) return;
                _folderBand.Text = archived.Display;
                _identityBand.Text = DefaultIdentityLabel();
                if (outgoingState is not null)
                {
                    _rowIdentities = _services.Identities.Load();
                    Show([.. queued, .. imported]);
                    var isDrafts = outgoingState == OutgoingStates.Draft;
                    var own = queued.Count == 0 ? (isDrafts ? "nothing saved" : "nothing queued")
                        : isDrafts ? $"{queued.Count:N0} saved — double-click to keep writing"
                        : $"{queued.Count:N0} queued — use File > Send Outbox to deliver";
                    _status.Text = $"{archived.Display} — {own}; {imported.Count:N0} imported (read-only)";
                    return;
                }
                // The mailbox fetched the same mail the import did, so an imported copy of a message the mailbox
                // holds of its own is the same message twice. The mailbox's copy is the live one; the import's
                // stands down, and stays down once that copy has been filed, deleted or emptied away.
                imported = imported.Where(message =>
                    !here.Contains((message.Subject, message.Sender, message.Date?.UtcDateTime.ToString("O") ?? ""))
                    && !gone.Contains(MailContract.AdoptedCopyKeyPrefix + message.Id)).ToArray();
                _rowIdentities = _services.Identities.Load();
                Show([.. queued, .. live.Concat(imported).OrderByDescending(message => message.Date)]);
                _status.Text = live.Count > 0
                    ? $"{archived.Display} — {live.Count:N0} here, {imported.Count:N0} imported"
                    : $"{archived.Display} — {imported.Count:N0} imported (read-only)";
                return;
            }
            if (_selectedAccount?.IsConnected == true)
            {
                var account = _selectedAccount;
                var (cached, marks) = await Task.Run(() =>
                {
                    var messages = _services.Database.ListMessages(account.Name, MailContract.LiveSource, WholeFolder);
                    return (messages, ReadMessageMarks(messages));
                });
                if (IsDisposed || Disposing || generation != _loadGeneration) return;
                Show(cached.Cast<object>().ToArray(), marks);
                _folderBand.Text = "Inbox";
                _status.Text = _rows.Count == 0
                    ? $"{account.Address} — no cached messages; use Send/Receive"
                    : $"Connected — {account.Address}";
            }
            else if (_selectedLocalFolder == "local-all")
            {
                Show([]);
                _folderBand.Text = "All Local Mail";
                _status.Text = "Local Mail — select a folder to view its messages";
            }
            else if (_selectedLocalFolder is LocalFolderKinds.Drafts or LocalFolderKinds.Outbox)
            {
                var isDrafts = _selectedLocalFolder == LocalFolderKinds.Drafts;
                var state = isDrafts ? OutgoingStates.Draft : OutgoingStates.Outbox;
                var queued = await Task.Run(() => isDrafts ? _services.Database.ListDraftsInFolder(LocalFolderKinds.Drafts, 500)
                    : _services.Database.ListOutgoing(state, 500));
                if (IsDisposed || Disposing || generation != _loadGeneration) return;
                ReplaceOutgoing(queued);
                _folderBand.Text = isDrafts ? "Drafts" : "Outbox";
                _status.Text = _rows.Count == 0
                    ? (isDrafts ? "Drafts — nothing saved" : "Outbox — nothing queued")
                    : isDrafts ? "Drafts — double-click to keep writing"
                    : "Outbox — use File > Send Outbox to deliver";
            }
            else
            {
                var folder = _selectedLocalFolder;
                var name = _localTree.SelectedNode?.Tag is LocalFolder selectedFolder && selectedFolder.Key == folder
                    ? selectedFolder.Name : folder;
                var (accounts, held, drafts, identities, marks) = await Task.Run(() =>
                {
                    var accounts = LocalScopedAccounts();
                    var messages = _services.Database.ListMessagesForAccounts(accounts, folder, WholeFolder);
                    return (accounts, messages,
                        _services.Database.ListDraftsInFolder(folder, 500),
                        _services.Identities.Load(),
                        ReadMessageMarks(messages));
                });
                if (IsDisposed || Disposing || generation != _loadGeneration) return;
                _rowIdentities = identities;
                Show([.. drafts, .. held], marks);
                _folderBand.Text = name;
                _status.Text = accounts.Length == 0 ? "Local Mail — add a Gmail, Hotmail, or other downloading account"
                    : $"Local Mail — {_folderBand.Text}";
            }
            _identityBand.Text = DefaultIdentityLabel();
        }
        catch (Exception exception)
        {
            if (!IsDisposed && !Disposing && generation == _loadGeneration) ShowError("Could not open the folder", exception);
        }
    }

    /// <summary>
    /// Local Mail folders hold both downloaded mail and anything Mailkeep itself sent, so they are scoped to the
    /// Local Mail accounts plus the Sending Identities rather than to receiving accounts alone.
    /// </summary>
    private string[] LocalScopedAccounts() => _services.Accounts.Load().Where(value => !value.IsConnected).Select(value => value.Name)
        .Concat(_services.Identities.Load().Select(identity => identity.Id))
        .Append(MailContract.LocalCopyAccount)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Builds the one row the list is about to draw.</summary>
    private ListViewItem Row(int index)
    {
        // A virtual list in Details view refuses a row short of a cell per column, and it can ask for an index
        // the rows no longer reach while removed rows are leaving — a bare blank row there threw out of WndProc.
        if (index < 0 || index >= _rows.Count) return BlankRow();
        if (_rows[index] is OutgoingMessage outgoing)
        {
            var identity = _rowIdentities.FirstOrDefault(value => value.Id.Equals(outgoing.SendingIdentityId, StringComparison.OrdinalIgnoreCase));
            // By index, not by name: a row built for a virtual list is not attached to the list yet, so it has
            // no image list to look a name up in and draws with no icon at all.
            var queued = new ListViewItem(string.Join("; ", outgoing.To)) { Tag = outgoing, Name = outgoing.Id, ImageIndex = ReadIcon };
            queued.SubItems.Add(outgoing.Subject.Length == 0 ? "(no subject)" : outgoing.Subject);
            queued.SubItems.Add(identity?.Email ?? outgoing.SendingIdentityId);
            queued.SubItems.Add(outgoing.UpdatedAt.ToLocalTime().ToString("g"));
            queued.SubItems.Add(outgoing.LastError is null ? "" : $"retry {outgoing.AttemptCount}");
            if (outgoing.LastError is not null) queued.ToolTipText = outgoing.LastError;
            return queued;
        }
        if (_rows[index] is not MessageSummary message) return BlankRow();
        var marks = _marks.GetValueOrDefault(message.Id);
        var mark = (marks.Replied ? 1 : 0) + (marks.Forwarded ? 2 : 0);
        var item = new ListViewItem(message.DisplaySender)
        {
            Tag = message, Name = message.SourceKey,
            ImageIndex = (message.IsRead ? ReadIcon : UnreadIcon) + 2 * mark,
            // Said as well as drawn: the arrow is small, and a screen reader cannot see it at all.
            ToolTipText = mark switch { 1 => "Replied", 2 => "Forwarded", 3 => "Replied and forwarded", _ => "" }
        };
        // One bold font for every unread row rather than one each: a font is a handle, and a thousand of them
        // per folder opened is a thousand handles nobody frees.
        if (!message.IsRead) item.Font = _unreadFont ??= new Font(_messages.Font, FontStyle.Bold);
        item.SubItems.Add(message.Subject);
        item.SubItems.Add(message.IsLocalStoredCopy ? "Local copy" : AccountLabel(message.Account));
        item.SubItems.Add(message.Date?.ToLocalTime().ToString("g") ?? "");
        item.SubItems.Add(message.DisplaySize);
        return item;
    }

    private ListViewItem BlankRow()
    {
        var blank = new ListViewItem("");
        while (blank.SubItems.Count < _messages.Columns.Count) blank.SubItems.Add("");
        return blank;
    }

    /// <summary>Account names carry a mode prefix so a Local and a Connected account for the same address can
    /// coexist as separate keys. In the list it says nothing the reader needs, so it goes.</summary>
    private static string AccountLabel(string account)
    {
        foreach (var prefix in new[] { "local:", "connected:" })
            if (account.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return account[prefix.Length..];
        return account;
    }

    /// <summary>Takes rows out of the list where they stand, and moves the selection to the row that now sits
    /// where the first removed one was, the way every mail client carries on after a delete. The list is not
    /// rebuilt and does not pass through empty, so nothing blinks and the scroll position holds.</summary>
    private void RemoveRows(IEnumerable<long> ids)
    {
        var gone = ids.ToHashSet();
        var first = _messages.SelectedIndices.Count > 0 ? _messages.SelectedIndices.Cast<int>().Min() : -1;
        var top = _messages.VirtualListSize > 0 ? At(_messages.TopItem?.Index ?? 0) as MessageSummary : null;
        ++_loadGeneration;
        // Any preview still loading is for a row that may be among those gone.
        _previewGeneration++;
        var remaining = _rows.Where(row => row is not MessageSummary message || !gone.Contains(message.Id)).ToArray();
        _messages.BeginUpdate();
        try
        {
            ListSelection.ClearSelection(_messages);
            // Reset the native owner-data viewport while the old rows still exist. Shrinking a
            // scrolled list in place can leave its native origin outside the remaining rows.
            if (_messages.VirtualListSize > 0) _messages.EnsureVisible(0);
            _messages.VirtualListSize = 0;
            _rows = remaining;
            _messages.VirtualListSize = _rows.Count;
            UpdateMessageCount();
            if (_rows.Count == 0) { ClearPreview(); return; }
            var next = Math.Clamp(first, 0, _rows.Count - 1);
            _messages.SelectedIndices.Add(next);
            _messages.Items[next].Focused = true;
            var topIndex = top is null ? -1 : Array.FindIndex(remaining,
                row => row is MessageSummary message && message.Id == top.Id);
            if (topIndex >= 0) _messages.TopItem = _messages.Items[topIndex];
            _messages.EnsureVisible(next);
        }
        finally
        {
            _messages.EndUpdate();
            RedrawMessageList();
        }
    }

    /// <summary>
    /// Forces the message list to immediately discard any cached renderings and repaint its items.
    /// In a double-buffered virtual ListView, native SysListView32 does not re-query item text/style
    /// on Invalidate() alone.
    /// </summary>
    private void RedrawMessageList(int first = 0, int last = -1)
    {
        _messageHoverTip.Hide(_messages);
        if (_messages is BufferedListView buffered) buffered.RedrawItems(first, last);
        else
        {
            _messages.Invalidate();
            _messages.Update();
        }
    }

    private void ReplaceOutgoing(IReadOnlyList<OutgoingMessage> messages)
    {
        _rowIdentities = _services.Identities.Load();
        Show(messages);
    }

    private const int FromColumn = 0;
    private const int SubjectColumn = 1;
    private const int AccountColumn = 2;
    private const int ReceivedColumn = 3;
    private const int SizeColumn = 4;
    private int _sortColumn = ReceivedColumn;
    private bool _sortDescending = true;

    /// <summary>Hands the list a new set of rows, in the order asked for. Nothing is built here: the list asks
    /// for what it draws.</summary>
    private string? _shownMessageScope;
    private bool _restoringInboxSelection;

    private void Show(IReadOnlyList<object> rows)
        => Show(rows, null);

    private IReadOnlyDictionary<long, MessageMarks> ReadMessageMarks(IEnumerable<MessageSummary> messages)
    {
        var ids = messages.Select(message => message.Id).ToArray();
        if (ids.Length == 0) return new Dictionary<long, MessageMarks>();
        try { return StartupDiagnostics.Time("Mailbox.MessageMarks", () => _services.Database.MessageMarks(ids)); }
        catch (Exception) { return new Dictionary<long, MessageMarks>(); } // Missing marks must not hide the mail.
    }

    private void Show(IReadOnlyList<object> rows, IReadOnlyDictionary<long, MessageMarks>? marks)
    {
        var scope = _selectedAccount?.IsConnected == true ? "connected:" + _selectedAccount.Name
            : _selectedArchive is { } archive ? "archive:" + archive.Account + ":" + archive.ArchiveFolder
            : "local:" + _selectedLocalFolder;
        static string? Key(object? row) => row switch
        {
            MessageSummary message => "message:" + message.Id,
            OutgoingMessage outgoing => "outgoing:" + outgoing.Id,
            _ => null
        };
        var keepPosition = scope == _shownMessageScope;
        var selected = keepPosition ? _messages.SelectedIndices.Cast<int>().Select(index => Key(At(index))).ToHashSet() : [];
        var focused = keepPosition && _messages.FocusedItem is { } item ? Key(At(item.Index)) : null;
        var top = keepPosition && _messages.VirtualListSize > 0 ? Key(At(_messages.TopItem?.Index ?? 0)) : null;
        _shownMessageScope = scope;
        rows = StartupDiagnostics.Time("Mailbox.Sort", () => Sorted(rows));
        // Decide against the rows actually being replaced, after any slow receive/recount.
        // A new first row in newest-first Inbox is an arrival; deleting a row or filling
        // older rows at the bottom must not jump the reader back to the top.
        var firstIsArrival = rows.FirstOrDefault() is MessageSummary firstArrival
            && !_rows.OfType<MessageSummary>().Any(message => message.Id == firstArrival.Id);
        var receivedRowsAdded = false;
        if (_receiveArrivalsPending && keepPosition && ShowingInbox)
        {
            var previousIds = _rows.OfType<MessageSummary>().Select(message => message.Id).ToHashSet();
            receivedRowsAdded = rows.OfType<MessageSummary>().Any(message => !previousIds.Contains(message.Id));
        }
        _receiveArrivalsPending = false;
        var preservePosition = PreserveInboxPosition();
        var scrollToArrival = keepPosition && ShowingInbox
            && _sortColumn == ReceivedColumn && _sortDescending && _rows.Count > 0
            && (firstIsArrival || receivedRowsAdded) && !preservePosition;
        if (_syncRunning)
            TimingLog.Mark($"receive scroll: inbox={ShowingInbox} connected={_selectedAccount?.IsConnected == true} merged={_selectedArchive?.LocalKey == LocalFolderKinds.Inbox} sameScope={keepPosition} newestFirst={_sortColumn == ReceivedColumn && _sortDescending} firstNew={firstIsArrival} preserve={preservePosition} manualUnchanged={_manualReceiveInputVersion == _mailInputVersion} inputAgeMs={Environment.TickCount64 - _lastMailInput} readerFocus={ActiveForm == this && (_body.ContainsFocus || _html.ContainsFocus)} selected={_messages.SelectedIndices.Count} composers={_composes.Count} modal={Application.OpenForms.Cast<Form>().Any(form => form.Modal)} scroll={scrollToArrival}");
        // Restoring the same selected Inbox message must not reload its reading pane,
        // reset its scroll/caret or cancel an in-flight image update.
        var preserveInboxSelection = keepPosition && ShowingInbox
            && !_previewUpdatePending && selected.Count == 1 && _currentContent is { } reading
            && selected.Contains("message:" + reading.Summary.Id)
            && rows.OfType<MessageSummary>().Any(message => message.Id == reading.Summary.Id);
        _rows = rows;
        _marks = marks ?? ReadMessageMarks(rows.OfType<MessageSummary>());
        _restoringInboxSelection = preserveInboxSelection;
        _messages.BeginUpdate();
        try
        {
            // Before the count changes, and whether or not there is anything to show. A virtual list keeps its
            // selection as bare indices, so rows that have gone leave their indices selected — and this used to
            // be skipped entirely for an empty folder, which is what a folder looks like after its mail has been
            // deleted. The ghost selection then followed the next account into a list that never came right.
            ListSelection.ClearSelection(_messages);
            if (!keepPosition)
            {
                // Through zero first: a virtual list keeps its scroll offset across a size change, so a folder opened
                // after a longer one painted its first rows where the old ones had scrolled to — a blank band, then
                // rows drawn half a row out, until something forced a full redraw.
                // Through zero alone was not enough: leaving a scrolled folder for Drafts left the native list with a
                // top index of -8 and nothing drawn at all. Scroll home while the old rows still exist, so the size
                // change starts from row 0.
                if (_messages.VirtualListSize > 0) _messages.EnsureVisible(0);
                _messages.VirtualListSize = 0;
            }
            _messages.VirtualListSize = rows.Count;
            UpdateMessageCount();
            SortArrow.Show(_messages, _sortColumn, _sortDescending);
            var restored = Enumerable.Range(0, rows.Count).Where(index => selected.Contains(Key(rows[index]))).ToArray();
            // A populated folder replaces the page directly. Clearing first started a blank
            // browser navigation and swapped renderers before the selected message arrived.
            if (rows.Count == 0) ClearPreview();
            if (rows.Count > 0)
            {
                foreach (var index in restored.Length > 0 ? restored : [0]) _messages.SelectedIndices.Add(index);
                var focusedIndex = focused is null ? -1
                    : Enumerable.Range(0, rows.Count).FirstOrDefault(index => Key(rows[index]) == focused, -1);
                _messages.Items[focusedIndex >= 0 ? focusedIndex : restored.FirstOrDefault()].Focused = true;
                var topIndex = scrollToArrival ? 0
                    : top is null ? -1 : Enumerable.Range(0, rows.Count).FirstOrDefault(index => Key(rows[index]) == top, -1);
                if (topIndex >= 0) _messages.TopItem = _messages.Items[topIndex];
                else _messages.EnsureVisible(restored.FirstOrDefault());
            }
        }
        finally
        {
            _messages.EndUpdate();
            _restoringInboxSelection = false;
            RedrawMessageList();
        }
    }

    /// <summary>The rows in the order the headings ask for. Queued mail keeps the order it was given: an
    /// Outbox is a queue, and sorting a queue by subject says nothing about when anything will be sent.</summary>
    private IReadOnlyList<object> Sorted(IReadOnlyList<object> rows)
    {
        // Filed drafts share the list with received mail. Their position must not disable sorting,
        // and filtering to MessageSummary would silently drop them when a received row came first.
        if (!rows.Any(row => row is MessageSummary)) return rows;
        string Account(OutgoingMessage draft) => _rowIdentities.FirstOrDefault(identity =>
            identity.Id.Equals(draft.SendingIdentityId, StringComparison.OrdinalIgnoreCase))?.Email ?? draft.SendingIdentityId;
        var ordered = _sortColumn switch
        {
            FromColumn => rows.OrderBy(row => row switch { MessageSummary message => message.DisplaySender, OutgoingMessage draft => string.Join("; ", draft.To), _ => "" }, StringComparer.CurrentCultureIgnoreCase),
            SubjectColumn => rows.OrderBy(row => row switch { MessageSummary message => message.Subject, OutgoingMessage draft => draft.Subject, _ => "" }, StringComparer.CurrentCultureIgnoreCase),
            AccountColumn => rows.OrderBy(row => row switch { MessageSummary message => message.Account, OutgoingMessage draft => Account(draft), _ => "" }, StringComparer.CurrentCultureIgnoreCase),
            SizeColumn => rows.OrderBy(row => row is MessageSummary message ? message.Size : 0),
            _ => rows.OrderBy(row => row switch { MessageSummary message => message.Date, OutgoingMessage draft => draft.UpdatedAt, _ => (DateTimeOffset?)null })
        };
        return (_sortDescending ? ordered.Reverse() : ordered).ToArray();
    }

    private FindMessageForm? _findForm;
    private IReadOnlyList<FindFolder> _findFolders = [];

    /// <summary>The folders Find can look in. Counting the import's folders for them took most of the time Find
    /// took to appear, so they are worked out off the window's thread whenever the tree changes, not on Ctrl+F.</summary>
    private IReadOnlyList<FindFolder> FindFolders() => _services.Database.FolderCounts(MailContract.ArchiveSource)
        .Select(folder => new FindFolder("Imported / " + folder.Folder, folder.Folder))
        .DistinctBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
        .Concat(FindFolder.LocalChoices(_services.Database.ListLocalFolders()))
        .OrderBy(folder => folder.Name, StringComparer.InvariantCulture)
        .ToArray();

    private async Task RefreshFindFoldersAsync()
    {
        try
        {
            var folders = await Task.Run(FindFolders);
            if (IsDisposed) return;
            _findFolders = folders;
            if (_findForm is { IsDisposed: false, Visible: false }) _findForm.SetFolders(folders);
        }
        catch (Exception) { } // Find keeps the folders it had
    }

    /// <summary>OE's Find Message window, made once and kept: built, laid out and given its window ahead of
    /// time, so Ctrl+F only has to show it. Building it on every Ctrl+F, with its folders counted, took over a
    /// tenth of a second each time.</summary>
    private FindMessageForm FindForm()
    {
        if (_findForm is { IsDisposed: false }) return _findForm;
        _findForm = new FindMessageForm(_findFolders,
            (query, folder, filters, cancellation) =>
            {
                TimingLog.Time("find operational sync", () => _services.Catalog.SyncOperational(cancellationToken: cancellation));
                TimingLog.Time("find archive sync", () => _services.Catalog.SyncArchive(cancellationToken: cancellation));
                return TimingLog.Time("find indexed query", () => _services.Catalog.Search(query, "all", 250, folder, null, filters, cancellation));
            },
            // Opening a hit opens the message, in its own window, the way double-clicking one in the list does.
            // It used to replace the whole folder with that single row, which threw away where the user was
            // and never showed them the message they had asked for.
            hit => _ = OpenMessageAsync(hit.Id),
            OpenAttachment, _services.Database) { Owner = this };
        _findForm.SetArchiveSearch((query, sender) => Archives.Search(query, 500, sender));
        _findForm.OpenResults = (hit, order) => _ = OpenMessageWithOrderAsync(hit.Id, order);
        _findForm.CreateControl();
        return _findForm;
    }

    /// <summary>The one way into search: OE's Find Message window, summoned by the hotkey, the toolbar
    /// button or the menu. Modeless, so mail stays readable while it is open.</summary>
    private Task ShowSearchPromptAsync()
    {
        if (!RequireReady()) return Task.CompletedTask;
        var find = FindForm();
        if (!find.Visible) find.Show();
        find.Activate();
        return Task.CompletedTask;
    }

    /// <summary>Changes the summoning key: parsed and registered immediately, so a bad choice fails now
    /// rather than silently after the next restart.</summary>
    private void ShowOptions()
    {
        if (!RequireReady()) return;
        var imagePolicies = _services.Database.GetSetting(RemoteImagePermissions.PoliciesSetting);
        using var dialog = new OptionsForm(_services, _autoSyncItem.Checked, _accountSort.ToString(), owner =>
        {
            using var export = new ExportPicturesForm(_services.Database, _findFolders);
            export.ShowDialog(owner);
        });
        var result = dialog.ShowDialog(this);
        if (imagePolicies != _services.Database.GetSetting(RemoteImagePermissions.PoliciesSetting))
        {
            if (_currentContent is { } content) { _html.Clear(); ShowBody(content); }
            foreach (var reader in OwnedForms.OfType<MessageWindow>()) reader.RefreshImages();
        }
        if (result != DialogResult.OK) return;
        _archiveToolbar.Visible = OptionsForm.ToolbarSettings.ShowArchive(_services.Database);
        
        if (_autoSyncItem.Checked != dialog.AutoSync)
        {
            _autoSyncItem.Checked = dialog.AutoSync;
            SetAutoSync(dialog.AutoSync);
        }
        
        if (Enum.TryParse<AccountSort>(dialog.AccountSort, out var sort))
        {
            SetAccountSort(sort);
        }
    }

    private void ChangeSearchShortcut()
    {
        if (!RequireReady()) return;
        var current = _services.Database.GetSetting("hotkey.search") ?? "Ctrl+Shift+F";
        using var prompt = new SearchPromptForm(current, shortcutMode: true);
        if (prompt.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(prompt.Query)) return;
        var chosen = prompt.Query.Trim();
        if (!TryParseHotkey(chosen, out _, out _))
        {
            MessageBox.Show(this, $"'{chosen}' is not a shortcut. Combine Ctrl, Shift, Alt or Win with a key, e.g. Ctrl+Shift+F.",
                "Search shortcut", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (SearchShortcutConflicts(chosen))
        {
            MessageBox.Show(this, $"'{chosen}' is reserved for a mail or editing command. Use Ctrl+Shift+F or a different shortcut.",
                "Search shortcut", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _services.Database.SetSetting("hotkey.search", chosen);
        RegisterSearchHotkey();
        if (!_status.Text.Contains("taken", StringComparison.OrdinalIgnoreCase))
            _status.Text = $"Search shortcut is now {chosen}, from anywhere on the desktop";
    }

    /// <summary>Summons search from anywhere on the desktop. The key is registered system-wide and read from
    /// settings so it can be changed; a failed registration (the combination is taken) degrades to in-app only.
    /// </summary>
    private void RegisterSearchHotkey()
    {
        const int id = 1;
        UnregisterHotKey(Handle, id);
        var stored = _services.Database.GetSetting("hotkey.search") ?? "Ctrl+Shift+F";
        if (SearchShortcutConflicts(stored))
        {
            _status.Text = $"Search shortcut {stored} conflicts with a mail command — use Ctrl+Shift+F; change it under Tools";
            return;
        }
        if (!TryParseHotkey(stored, out var modifiers, out var key))
        {
            _status.Text = $"Search shortcut '{stored}' is not understood — use e.g. Ctrl+Shift+F";
            return;
        }
        if (!RegisterHotKey(Handle, id, modifiers, (uint)key))
            _status.Text = $"Search shortcut {stored} is taken by another program — change it under Tools";
    }

    internal static bool TryParseHotkey(string text, out uint modifiers, out Keys key)
    {
        modifiers = 0;
        key = Keys.None;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= 2; break;
                case "shift": modifiers |= 4; break;
                case "alt": modifiers |= 1; break;
                case "win": modifiers |= 8; break;
                default:
                    if (key != Keys.None) return false;
                    if (part.Length == 1 && part[0] is >= '0' and <= '9') key = Keys.D0 + (part[0] - '0');
                    else if (!Enum.TryParse(part, true, out key) || key == Keys.None) return false;
                    break;
            }
        }
        return key != Keys.None && modifiers != 0;
    }

    protected override void WndProc(ref Message message)
    {
        const int WM_HOTKEY = 0x0312;
        if (message.Msg == WM_HOTKEY && message.WParam == 1)
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            _ = ShowSearchPromptAsync();
            return;
        }
        base.WndProc(ref message);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_syncRunning)
        {
            e.Cancel = true;
            _status.Text = "Wait for receiving to finish before closing Mailkeep. You can hide the download progress window.";
            return;
        }
        if (_folderChanging || !_filing.IsCompleted)
        {
            e.Cancel = true;
            _status.Text = "Finish saving the current folder change before closing Mailkeep.";
            return;
        }
        if (_archiveTransferInProgress)
        {
            e.Cancel = true;
            _status.Text = "Finish the archive operation before closing Mailkeep.";
            return;
        }
        if (_remoteLocalWrites.Count > 0)
        {
            e.Cancel = true;
            _status.Text = "Saving server changes before closing…";
            _ = CloseAfterRemoteWritesAsync();
            return;
        }
        if (_composes.Count > 0)
        {
            e.Cancel = true;
            _status.Text = "Close or save the open message windows before exiting Mailkeep.";
            _composes.First().Activate();
            return;
        }
        base.OnFormClosing(e);
        if (e.Cancel || !_ready) return;
        _layoutSave.Stop();
        try { SaveLayout(); }
        catch (Exception error) { ShowError("Could not save window layout", error); }
        try { SaveCounts(); }
        catch (Exception) { } // the next start counts afresh
    }

    private void SaveLayout()
    {
        // Closing saves the newest snapshot after any deferred write, so an older
        // background save cannot overwrite the final selection or placement.
        _layoutWrite.GetAwaiter().GetResult();
        SettingsWrites.DrainAsync(_services.Database).GetAwaiter().GetResult();
        if (CaptureLayout() is { } layout) _services.Database.SetSetting(WindowLayout.Setting, layout);
    }

    private string? CaptureLayout()
    {
        // A diagnostic size experiment must not replace the user's saved window placement.
        if (_openWithoutActivation || StartupDiagnostics.Skip("SAVED_WINDOW")) return null;
        return WindowLayout.Capture(this, _lastMaximized,
            _mainSplit, _folderSplit, _previewSplit, _messages, _expandedFolders,
            _selectedAccount?.IsConnected == true ? _selectedAccount.Name : null,
            _localTree.SelectedNode is { } node ? FolderKey(node) : null).ToJson();
    }

    /// <summary>A moved splitter or column is written down a moment after it stops moving, not only when the
    /// window closes: a client that was killed, crashed or replaced by an update came back with the panes
    /// where they had been before, however long ago they were moved.</summary>
    private readonly System.Windows.Forms.Timer _layoutSave = new() { Interval = 1000 };
    private Task _layoutWrite = Task.CompletedTask;

    private void SaveLayoutSoon()
    {
        if (!_ready || WindowState == FormWindowState.Minimized) return;
        _layoutSave.Stop();
        _layoutSave.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _messageMarksRefresh.Stop();
        _messageMarksRefresh.Dispose();
        _messageHoverTip.Dispose();
        _remoteTimer.Stop();
        _remoteTimer.Dispose();
        _remoteStop.Cancel();
        UnregisterHotKey(Handle, 1);
        base.OnFormClosed(e);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr handle, int id);

    /// <summary>The row the pane is reading, which only means anything when one row is selected. Indices, not
    /// items: the list holds no items to ask, it is given the rows it draws.</summary>
    private object? Current()
        => _messages.SelectedIndices.Count == 1 ? At(_messages.SelectedIndices[0]) : null;

    private object? At(int index) => index >= 0 && index < _rows.Count ? _rows[index] : null;

    /// <summary>Every message highlighted, which is what Delete and Move act on. Drafts and queued mail are
    /// not messages of the store and are left out.</summary>
    private MessageSummary[] Selected()
        => _messages.SelectedIndices.Cast<int>().Select(At).OfType<MessageSummary>().ToArray();

    private OutgoingMessage[] SelectedDrafts()
    {
        var rows = _messages.SelectedIndices.Cast<int>().Select(At).ToArray();
        return rows.Length > 0 && rows.All(row => row is OutgoingMessage { Status: OutgoingStates.Draft })
            ? rows.Cast<OutgoingMessage>().ToArray() : [];
    }

    /// <summary>Every row in the folder, which is what Delete and Move then act on. The list is virtual, so
    /// the control is told once rather than asked to mark each row, and the pane is refreshed here because a
    /// selection made this way arrives as a range and not as a change of the current row.</summary>
    private async Task SelectAllMessagesAsync()
    {
        if (_rows.Count == 0) return;
        _messages.Focus();
        ListSelection.SelectAll(_messages);
        await ShowSelectedMessageAsync();
    }

    /// <summary>The letter as it was written: the page when it was written as a page, and text when it
    /// was not. Mail laid out by a designer is unreadable any other way, which is what a text-only pane
    /// had made of every alert, statement and receipt in the account.</summary>
    private async void ShowBody(MessageContent content)
    {
        var generation = ++_imageGeneration;
        var policies = await Task.Run(ReadImagePolicies);
        if (IsDisposed || Disposing || generation != _imageGeneration || _currentContent?.Summary.Id != content.Summary.Id) return;
        RenderBody(content, policies);
    }

    private IReadOnlyDictionary<string, string> ReadImagePolicies()
    {
        try { return _imagePermissions.Policies(); }
        catch (Exception) { return new Dictionary<string, string>(); } // Unavailable policy means Ask, never allow.
    }

    private void RenderBody(MessageContent content, IReadOnlyDictionary<string, string> policies)
    {
        ++_imageGeneration;
        if (_html.IsReady && !_html.Failed && !string.IsNullOrWhiteSpace(content.BodyHtml))
        {
            var origins = RemoteImages.Origins(content.BodyHtml);
            var allowed = policies.Where(pair => pair.Value == RemoteImagePermissions.Allow).Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ask = origins.Any(origin => policies.GetValueOrDefault(origin, RemoteImagePermissions.Ask) == RemoteImagePermissions.Ask);
            var neverCount = origins.Count(origin => policies.GetValueOrDefault(origin) == RemoteImagePermissions.Never);
            _body.Visible = false;
            _html.Visible = true;
            // The bar first, then the page: shown the other way round, the bar appearing shrank the pane
            // under a page already painting, and every message with pictures arrived with a jolt.
            _remoteImages.ShowFor(origins,
                ask ? () => LoadRemoteImages(content, origins.Where(origin => policies.GetValueOrDefault(origin, RemoteImagePermissions.Ask) != RemoteImagePermissions.Never).ToArray(), policies, loadedOnce: true) : null,
                () => RememberRemoteImages(content, origins), neverCount);
            var permitted = origins.Where(allowed.Contains).ToArray();
            // Show the letter immediately. Pictures arrive independently; a slow remote host must never
            // keep the previous message in the reading pane. Only this process fetches remote pictures.
            _html.Show(SafeHtml(content.BodyHtml));
            if (permitted.Length > 0) LoadRemoteImages(content, permitted, policies, showFirst: false);
            return;
        }
        ShowText(content.BodyText);
    }

    private void ShowText(string text)
    {
        ++_imageGeneration;
        _remoteImages.Clear();
        _html.Clear();
        _html.Visible = false;
        _body.Visible = true;
        _body.Text = text;
        ApplyBodyLeading();
    }

    private int _imageGeneration;
    private async void LoadRemoteImages(MessageContent content, IReadOnlyList<string> origins, IReadOnlyDictionary<string, string> policies, bool showFirst = true, bool loadedOnce = false)
    {
        if (_currentContent?.Summary.Id != content.Summary.Id || string.IsNullOrWhiteSpace(content.BodyHtml)) return;
        var previous = _status.Text;
        var generation = ++_imageGeneration;
        if (loadedOnce)
        {
            var sites = RemoteImages.Origins(content.BodyHtml);
            _remoteImages.ShowFor(sites, null, () => RememberRemoteImages(content, sites), sites.Count(site => policies.GetValueOrDefault(site) == RemoteImagePermissions.Never));
        }
        if (showFirst) _status.Text = "Loading pictures…";
        // Fetched by this program and written into the page: the page's runtime may be refused at the
        // firewall where this program is let out, and the letter then shows its pictures either way.
        string inlined;
        var progress = new Progress<string>(pictures =>
        {
            if (!IsDisposed && generation == _imageGeneration && _currentContent?.Summary.Id == content.Summary.Id)
                _html.UpdatePictures(SafeHtml(pictures));
        });
        var allowed = policies.Where(pair => pair.Value == RemoteImagePermissions.Allow).Select(pair => pair.Key);
        try { inlined = await RemoteImageFetcher.InlineAsync(content.BodyHtml, allowed.Concat(origins).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), progress: progress); }
        catch (Exception) { inlined = content.BodyHtml; }
        if (IsDisposed || generation != _imageGeneration || _currentContent?.Summary.Id != content.Summary.Id) return;
        _html.UpdatePictures(SafeHtml(inlined));
        _status.Text = showFirst ? "Pictures loaded" : previous;
    }

    private void RememberRemoteImages(MessageContent content, IReadOnlyList<string> origins)
    {
        if (_currentContent?.Summary.Id != content.Summary.Id) return;
        ImageSitesForm.EditMessage(this, _imagePermissions, content.BodyHtml ?? "");
    }

    private int _previewGeneration;
    private bool _previewUpdatePending;

    private void QueuePreviewUpdate()
    {
        if (_restoringInboxSelection) return;
        if (!_ready) return; // Startup renders the first selection once, before enabling the mailbox.
        // A row change reports deselection before selection. Read the final selection on the next
        // UI turn so the intermediate empty state never clears the message or navigates to blank.
        ++_previewGeneration;
        ++_imageGeneration;
        if (_previewUpdatePending || IsDisposed || !IsHandleCreated) return;
        _previewUpdatePending = true;
        BeginInvoke(async () =>
        {
            _previewUpdatePending = false;
            if (!IsDisposed) await ShowSelectedMessageAsync();
        });
    }

    private Task ShowSelectedMessageAsync() => ShowSelectedMessageCoreAsync(waitForReadMark: true);

    private async Task ShowSelectedMessageCoreAsync(bool waitForReadMark)
    {
        var generation = ++_previewGeneration;
        var selected = _messages.SelectedIndices.Count;
        if (selected == 0) { ClearPreview(); return; }
        // Several at once is a selection to act on, not something to read. Reading each one as the selection
        // passes over it would also mean a message loaded for every press of the arrow key. Nothing is shown
        // in the pane either: a reply aimed at whichever message happened to be read last is not what the
        // twenty highlighted rows are asking for.
        if (selected > 1)
        {
            var several = Selected();
            ClearPreview();
            _subject.Text = $"{selected:N0} messages selected";
            var deletable = several.Any(message => message.Source != MailContract.ArchiveSource);
            if (_delete is not null) _delete.Enabled = deletable;
            if (_move is not null) _move.Enabled = SelectedDrafts().Length > 0 || CanFileLocally(several) || several.Length > 0 && several.All(IsConnectedMessage);
            _status.Text = $"{selected:N0} messages selected";
            return;
        }
        _currentContent = null;
        _currentOutgoing = null;
        if (_print is not null) _print.Enabled = false;
        UpdateComposeState();
        if (Current() is OutgoingMessage outgoing)
        {
            try { await ShowOutgoingAsync(outgoing, generation); }
            catch (Exception error) { if (!IsDisposed && generation == _previewGeneration) ShowError("Could not open draft", error); }
            return;
        }
        if (Current() is not MessageSummary summary) return;
        var previewClock = Stopwatch.StartNew();
        double readMs = 0, policyMs = 0;
        try
        {
            var (content, policies) = await Task.Run(() =>
            {
                var clock = Stopwatch.StartNew();
                var message = StartupDiagnostics.Time("Preview.GetMessage", () => GetAnyMessage(summary.Id, (phase, elapsed) =>
                {
                    if (elapsed >= 250) TimingLog.MarkBackground($"preview: generation={generation} read_phase={phase} elapsed_ms={elapsed:F1}");
                }));
                readMs = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                var permissions = ReadImagePolicies();
                policyMs = clock.Elapsed.TotalMilliseconds;
                return (message, permissions);
            });
            if (content is null || IsDisposed || generation != _previewGeneration
                || Current() is not MessageSummary current || current.Id != summary.Id)
            {
                TimingLog.MarkBackground($"preview: discarded generation={generation} read_ms={readMs:F1} policy_ms={policyMs:F1} elapsed_ms={previewClock.Elapsed.TotalMilliseconds:F1}");
                return;
            }
            _currentContent = content;
            _subject.Text = content.Summary.Subject;
            _subject.ForeColor = ClassicTheme.HeaderNavy;
            SetPreviewLabels(true);
            _from.Text = string.IsNullOrWhiteSpace(content.Summary.SenderName) ? content.Summary.Sender : $"{content.Summary.SenderName} <{content.Summary.Sender}>";
            _to.Text = string.Join(", ", content.Summary.Recipients);
            _date.Text = content.Summary.Date?.ToLocalTime() is { } received ? $"{received:d} {received:T}" : "Date unavailable";
            var renderClock = Stopwatch.StartNew();
            StartupDiagnostics.Time("Preview.ShowBody", () => RenderBody(content, policies));
            var renderMs = renderClock.Elapsed.TotalMilliseconds;
            renderClock.Restart();
            StartupDiagnostics.Time("Preview.Attachments", () => _attachments.SetMessage(content));
            TimingLog.MarkBackground($"preview: submitted generation={generation} read_ms={readMs:F1} policy_ms={policyMs:F1} render_ms={renderMs:F1} attachments_ms={renderClock.Elapsed.TotalMilliseconds:F1} elapsed_ms={previewClock.Elapsed.TotalMilliseconds:F1}");
            _delete.Enabled = content.Summary.Source != MailContract.ArchiveSource;
            _openHtml.Enabled = !string.IsNullOrWhiteSpace(content.BodyHtml);
            if (_print is not null) _print.Enabled = true;
            if (_move is not null) _move.Enabled = true;
            UpdateComposeState();
            // Shown in the pane counts as read, as it does in the client this is modelled on, after a moment
            // long enough to tell reading from the selection passing over on its way somewhere else.
            if (content.Summary.Source == MailContract.LiveSource && !content.Summary.IsRead)
            {
                var mark = MarkReadAfterPreviewAsync(content.Summary, generation);
                if (waitForReadMark) await mark;
            }
        }
        catch (Exception exception)
        {
            if (!IsDisposed && generation == _previewGeneration) ShowError("Could not open message", exception);
        }
    }

    private async Task MarkReadAfterPreviewAsync(MessageSummary summary, int generation)
    {
        await Task.Delay(1500);
        if (!IsDisposed && generation == _previewGeneration) await MarkReadQuietlyAsync(summary);
    }

    /// <summary>Double-click and Enter open the message properly: a draft goes back to the composer, anything
    /// else opens in its own window rather than only ever appearing in the preview pane.</summary>
    private async Task OpenSelectedMessageAsync()
    {
        if (Current() is OutgoingMessage outgoing)
        {
            if (outgoing.Status == OutgoingStates.Draft) await EditDraftAsync(outgoing);
            else if (outgoing.Status == OutgoingStates.Outbox)
            {
                if (MessageBox.Show(this, "Move this message back to Drafts for editing?\r\n\r\nIt will not be sent until you place it in the Outbox again.",
                    "Edit queued message", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                try
                {
                    var draft = _services.Send.ReturnToDraft(outgoing.Id);
                    LoadMessages();
                    await EditDraftAsync(draft);
                }
                catch (Exception error) { ShowError("Could not edit queued message", error); LoadMessages(); }
            }
            return;
        }
        if (Current() is not MessageSummary summary) return;
        await OpenMessageAsync(summary.Id);
    }

    /// <summary>Opens one message in a window of its own, wherever it was asked for — the list or a search.</summary>
    private Task OpenMessageAsync(long id) => OpenMessageWithOrderAsync(id, null);

    private async Task OpenMessageWithOrderAsync(long id, IReadOnlyList<long>? searchOrder)
    {
        try
        {
            // Reload rather than reusing _currentContent: the pane may still be catching up with the selection.
            var content = await Task.Run(() => GetAnyMessage(id));
            if (content is null) { _status.Text = "That message could not be loaded."; return; }
            if (content.Summary.Source == MailContract.LiveSource && !content.Summary.IsRead) await MarkReadQuietlyAsync(content.Summary);
            MessageWindow? reader = null;
            reader = new MessageWindow(content, ReplyToAsync, ForwardContentAsync, PrintContent, OpenHtmlFor,
                Path.Combine(_services.Paths.Root, "reading-pane"), ExtractAnyAttachment,
                _imagePermissions, keys => ReadShortcut(reader?.CurrentContent ?? content, keys, () => reader?.Close()));
            if (searchOrder is not null)
            {
                var order = searchOrder.ToArray();
                var index = Array.IndexOf(order, id);
                if (index >= 0)
                {
                    reader.SetNavigation(async direction =>
                    {
                        var next = index + direction;
                        if (next < 0 || next >= order.Length) return;
                        try
                        {
                            var loaded = await Task.Run(() => GetAnyMessage(order[next]));
                            if (reader.IsDisposed || reader.Disposing) return;
                            if (loaded is null) { _status.Text = "That message could not be loaded."; return; }
                            if (loaded.Summary.Source == MailContract.LiveSource && !loaded.Summary.IsRead)
                                await MarkReadQuietlyAsync(loaded.Summary);
                            if (reader.IsDisposed || reader.Disposing) return;
                            await reader.SetContentAsync(loaded);
                            index = next;
                            reader.UpdateNavigation(index > 0, index < order.Length - 1);
                        }
                        catch (Exception error) { if (!reader.IsDisposed) ShowError("Could not open message", error); }
                    }, index > 0, index < order.Length - 1);
                }
            }
            reader.Show(ArchiveStore.Owns(id) && _archiveWindow is { IsDisposed: false, Visible: true } archive ? archive : this);
        }
        catch (Exception exception) { ShowError("Could not open message", exception); }
    }

    private async void OpenAttachment(long occurrenceId)
    {
        try
        {
            var path = await Task.Run(() => _services.Catalog.OpenAttachment(occurrenceId));
            if (!IsDisposed) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) { if (!IsDisposed) ShowError("Could not open attachment", exception); }
    }

    private async Task ShowOutgoingAsync(OutgoingMessage message, int generation)
    {
        var (identity, attachments) = await Task.Run(() => (_services.Identities.Find(message.SendingIdentityId),
            _services.Database.GetOutgoingAttachments(message.Id)));
        if (IsDisposed || Disposing || generation != _previewGeneration || Current() is not OutgoingMessage current || current.Id != message.Id) return;
        _currentOutgoing = message;
        _subject.Text = message.Subject.Length == 0 ? "(no subject)" : message.Subject;
        _subject.ForeColor = ClassicTheme.HeaderNavy;
        SetPreviewLabels(true);
        _from.Text = identity is null ? message.SendingIdentityId : $"{identity.DisplayName} <{identity.Email}>";
        _to.Text = string.Join("; ", message.To.Concat(message.Cc));
        _date.Text = message.Status == OutgoingStates.Draft
            ? "Draft saved " + message.UpdatedAt.ToLocalTime().ToString("f")
            : $"Queued {message.CreatedAt.ToLocalTime():f}" + (message.LastError is null ? "" : $" — attempt {message.AttemptCount} failed: {message.LastError}");
        ShowText(message.BodyText);
        _attachments.SetAttachments(attachments,
            index => _services.Database.ExtractOutgoingAttachment(message.Id, index));
        _delete.Enabled = message.Status == OutgoingStates.Draft;
        _openHtml.Enabled = false;
        if (_print is not null) _print.Enabled = true;
        if (_move is not null) _move.Enabled = message.Status == OutgoingStates.Draft;
        UpdateComposeState();
    }

    private async Task SyncOneAsync(MailAccount account)
    {
        if (!RequireReady()) return;
        if (_syncRunning) { _status.Text = "A refresh is already running…"; return; }
        _syncRunning = true;
        _manualReceiveInputVersion = account.IsConnected ? null : _mailInputVersion;
        _syncProgress.Style = ProgressBarStyle.Marquee;
        _syncProgress.Visible = true;
        var downloads = account.IsConnected ? null : BeginDownloads([account]);
        var receiveProgress = DownloadReporter(downloads);
        var receiveToken = _receiveCancellation?.Token ?? default;
        try
        {
            await RunBusyAsync(null, $"Checking {account.Address}…", async () =>
            {
                var result = await Task.Run(() => _services.Sync.SyncAsync(account.Name, receiveProgress: receiveProgress, cancellationToken: receiveToken));
                await Task.Yield();
                _receiveArrivalsPending |= result.NewCount > 0;
                downloads?.Complete([result]);
                await RefreshAfterReceiveAsync(reloadMessages: true);
                if (!result.Ok)
                {
                    _status.Text = $"{account.Address}: receive failed — see Download progress";
                    if (downloads is null) throw new InvalidOperationException(result.Error);
                    return;
                }
                _status.Text = $"{account.Address}: {result.NewCount} new";
            }, showWaitCursor: false);
        }
        finally { _manualReceiveInputVersion = null; downloads?.Complete([], "Receive stopped before completion"); EndDownloads(); _syncRunning = false; _syncProgress.Visible = false; _syncProgress.Style = ProgressBarStyle.Blocks; }
    }

    /// <summary>OE Classic's Send/Receive drop-down: All, then each Local Mail account by address.
    /// Rebuilt whenever accounts change so the list is never stale.</summary>
    private void RebuildReceiveMenu(IReadOnlyList<MailAccount> localAccounts)
    {
        _receive.DropDownItems.Clear();
        _receive.DropDownItems.Add(Item("Send and Receive All Local", async () => await SyncAllAsync(), "Send and Receive all Local accounts"));
        if (localAccounts.Count > 0) _receive.DropDownItems.Add(new ToolStripSeparator());
        foreach (var account in localAccounts)
        {
            var target = account;
            _receive.DropDownItems.Add(Item(target.Address, async () => await SyncOneAsync(target), "Send and Receive " + target.Address));
        }
        _receive.DropDownItems.Add(new ToolStripSeparator());
        _receive.DropDownItems.Add(Item("Send Outbox", async () => await SendOutboxAsync(true), "Send Outbox toolbar item"));
    }

    private Task SyncAllAsync() => SyncAccountsAsync(false);

    private async Task SyncAccountsAsync(bool connected)
    {
        if (!RequireReady()) return;
        if (_syncRunning) { _status.Text = "A refresh is already running…"; return; }
        _syncRunning = true;
        _manualReceiveInputVersion = connected ? null : _mailInputVersion;
        var failed = false;
        DownloadProgressForm? downloads = null;
        try
        {
            if (!connected)
            {
                var accounts = await Task.Run(() => _services.Accounts.Load().Where(account => !account.IsConnected).ToArray());
                downloads = BeginDownloads(accounts);
            }
            var receiveProgress = DownloadReporter(downloads);
            var receiveToken = _receiveCancellation?.Token ?? default;
            if (!connected) await SendOutboxAsync(false);
            await RunBusyAsync(null, connected ? "Refreshing connected accounts…" : "Checking Local accounts…", async () =>
            {
                var progress = SyncProgressReporter();
                var results = await Task.Run(() => _services.Sync.SyncAllAsync(false, progress: progress, connectedAccounts: connected,
                    receiveProgress: receiveProgress, cancellationToken: receiveToken));
                await Task.Yield();   // progress reports are posted; let the stragglers land before the summary
                _receiveArrivalsPending |= results.Any(result => result.NewCount > 0);
                downloads?.Complete(results);
                var failures = results.Where(value => !value.Ok).ToArray();
                await RefreshAfterReceiveAsync(reloadMessages: true);
                if (failures.Length > 0)
                {
                    failed = true;
                    if (downloads is not null)
                    {
                        _status.Text = $"Receive complete: {results.Sum(value => value.NewCount)} new, {failures.Length} account(s) failed — see Download progress";
                        return;
                    }
                    throw new InvalidOperationException(string.Join(Environment.NewLine, failures.Select(value => $"{value.Account}: {value.Error}")));
                }
                _status.Text = $"{(connected ? "Connected refresh" : "Send / Receive")} complete: {results.Sum(value => value.NewCount)} new";
            }, showWaitCursor: false);
        }
        finally
        {
            _manualReceiveInputVersion = null;
            downloads?.Complete([], "Receive stopped before completion");
            EndDownloads();
            _syncRunning = false;
            _syncProgress.Visible = false;
            // A failed pass leaves the taskbar button red — "the last refresh had errors" — until the next one runs.
            if (failed) TaskbarProgress.SetError(Handle); else TaskbarProgress.Clear(Handle);
        }
    }

    /// <summary>Live progress for a multi-account pass: the status bar counts completions and always
    /// names the accounts still being checked (so a slow mailbox is never an anonymous wait), while the
    /// strip's progress bar and the Windows taskbar button fill — the visibility the original HTML
    /// app's refresh page had.</summary>
    private readonly List<string> _syncInFlight = [];

    private IProgress<SyncProgress> SyncProgressReporter()
    {
        _syncInFlight.Clear();
        return new Progress<SyncProgress>(tick =>
        {
            if (!_syncRunning) return;   // a report arriving after the pass ended must not resurrect the bar
            if (tick.Started) _syncInFlight.Add(tick.Address); else _syncInFlight.Remove(tick.Address);
            _syncProgress.Style = ProgressBarStyle.Blocks;
            _syncProgress.Maximum = Math.Max(1, tick.Total);
            _syncProgress.Value = Math.Min(tick.Done, _syncProgress.Maximum);
            _syncProgress.Visible = true;
            var checking = _syncInFlight.Count == 0 ? ""
                : " — checking " + string.Join(", ", _syncInFlight.Take(3)) + (_syncInFlight.Count > 3 ? ", …" : "");
            _status.Text = $"Send / Receive: {tick.Done} of {tick.Total} done{checking}";
            TaskbarProgress.SetValue(Handle, tick.Done, Math.Max(1, tick.Total));
        });
    }

    /// <summary>Live progress for a deletion. Hundreds of messages is a long enough wait that a spinner says
    /// nothing worth knowing: it cannot tell half way from barely started, and the one thing wanted at that
    /// point is whether to keep waiting.</summary>
    private IProgress<DeleteProgress> DeleteProgressReporter()
        => new Progress<DeleteProgress>(tick =>
        {
            _syncProgress.Style = ProgressBarStyle.Blocks;
            _syncProgress.Maximum = Math.Max(1, tick.Total);
            _syncProgress.Value = Math.Min(tick.Done, _syncProgress.Maximum);
            _syncProgress.Visible = true;
            _status.Text = $"Deleting: {tick.Done:N0} of {tick.Total:N0} — {tick.Account}";
            TaskbarProgress.SetValue(Handle, tick.Done, Math.Max(1, tick.Total));
        });

    private bool _suppressAutoSyncEvents;

    private void StartAutoSync()
    {
        _suppressAutoSyncEvents = true;
        _autoSyncItem.Checked = _services.Database.GetSetting("sync.auto") is not "off";
        _suppressAutoSyncEvents = false;
        _autoSync.Tick += async (_, _) => await AutoSyncTickAsync();
        if (_autoSyncItem.Checked) _autoSync.Start();
    }

    private void SetAutoSync(bool enabled)
    {
        if (_suppressAutoSyncEvents) return;
        _ = SettingsWrites.Save(_services.Database, "sync.auto", enabled ? "on" : "off");
        if (enabled) _autoSync.Start(); else _autoSync.Stop();
        _status.Text = enabled
            ? "Automatic refresh on — Local Mail and connected accounts follow their refresh intervals"
            : "Automatic refresh off — Send/Receive is manual only";
    }

    /// <summary>The background pass checks accounts whose interval has lapsed, without a wait
    /// cursor or error dialog. Local receive details remain in Download progress; all failures go to the fetch log.
    /// Reload arrivals and the current Connected Inbox after a successful server check.</summary>
    private async Task AutoSyncTickAsync()
    {
        // The short first interval lets a stale fleet catch up soon after the app opens.
        _autoSync.Interval = 15 * 60 * 1000;
        if (_syncRunning || !_ready) return;
        _syncRunning = true;
        var failures = 0;
        DownloadProgressForm? downloads = null;
        try
        {
            var local = await Task.Run(() => _services.Accounts.Load().Where(account => !account.IsConnected
                && _services.Sync.IsDue(account, DateTimeOffset.UtcNow)).ToArray());
            downloads = BeginDownloads(local);
            var progress = SyncProgressReporter();
            var receiveProgress = DownloadReporter(downloads);
            var receiveToken = _receiveCancellation?.Token ?? default;
            var results = await Task.Run(() => _services.Sync.SyncAllAsync(true, progress: progress, receiveProgress: receiveProgress, cancellationToken: receiveToken));
            await Task.Yield();
            downloads?.Complete(results);
            if (results.Count == 0) return;
            failures = results.Count(value => !value.Ok);
            var arrived = results.Sum(value => value.NewCount) > 0;
            _receiveArrivalsPending |= arrived;
            var connectedChecked = _selectedAccount is { IsConnected: true } selected
                && results.Any(result => result.Ok && result.Account.Equals(selected.Name, StringComparison.OrdinalIgnoreCase));
            await RefreshAfterReceiveAsync(reloadMessages: arrived || connectedChecked);
            _status.Text = failures > 0
                ? $"Background refresh: {results.Count} account(s), {failures} failed — see View > Fetch log"
                : $"Background refresh: {results.Count} account(s), {results.Sum(value => value.NewCount)} new";
        }
        catch (OperationCanceledException) when (_receiveCancellation?.IsCancellationRequested == true)
        {
            await Task.Yield();
            downloads?.Complete([], "Receive stopped");
            await RefreshAfterReceiveAsync(reloadMessages: true);
            _status.Text = "Receive stopped. Downloaded messages have been kept.";
        }
        catch (Exception exception) { failures++; _status.Text = "Background refresh failed: " + exception.Message; }
        finally
        {
            downloads?.Complete([], "Receive stopped before completion");
            EndDownloads();
            _syncRunning = false;
            _syncProgress.Visible = false;
            if (failures > 0) TaskbarProgress.SetError(Handle); else TaskbarProgress.Clear(Handle);
        }
    }

    private Task DeleteAsync() => DeleteMessagesAsync(null);

    /// <summary>Receiving may change every folder badge. Read those counts off the window thread,
    /// keeping the current list painted while the database is busy.</summary>
    private async Task RefreshAfterReceiveAsync(bool reloadMessages)
    {
        var generation = ++_readCountsGeneration;
        var sidebar = await Task.Run(LoadSidebarData);
        if (IsDisposed || Disposing) return;
        if (generation == _readCountsGeneration) ReloadAccounts(sidebar);
        if (reloadMessages) await LoadMessagesAsync();
    }

    private async Task DeleteMessagesAsync(MessageSummary[]? explicitMessages, bool permanently = false)
    {
        if (explicitMessages is null && _currentOutgoing is { Status: OutgoingStates.Draft } draft)
        {
            if (MessageBox.Show(this, "Discard this draft?", "Delete draft", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            await Task.Run(() => _services.Send.DiscardDraft(draft.Id));
            await ReloadAccountsAsync();
            LoadMessages();
            return;
        }
        // Whatever is highlighted, not whatever was last read: deleting one of twenty selected messages
        // because that was the one in the preview pane is not what was asked for.
        var accounts = _services.Accounts.Load();
        var selection = explicitMessages ?? Selected();
        var chosen = selection
            .Where(message => message.Source != MailContract.ArchiveSource)
            .Where(message => message.IsLocalSentCopy || message.IsLocalStoredCopy || accounts.Any(account => account.Name.Equals(message.Account, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (chosen.Length == 0)
        {
            // Say so: the Delete key reaches here with the button greyed, and silence reads as a fault.
            if (selection.Length > 0) _status.Text = "Imported mail is read-only here — it can only be deleted in the client it came from.";
            return;
        }
        var connected = chosen.Any(message => !message.IsLocalSentCopy && !message.IsLocalStoredCopy && accounts.First(account =>
            account.Name.Equals(message.Account, StringComparison.OrdinalIgnoreCase)).IsConnected);
        var subject = chosen.Length == 1 ? "this message" : $"these {chosen.Length:N0} messages";
        // Moving local mail to Deleted Items is reversible, so it just happens, as it does in OE. Only the
        // irreversible cases ask: emptying from Deleted Items, or moving on the provider.
        string? prompt = connected ? $"Move {subject} to Trash on the mail provider?" : null;
        if (permanently)
            prompt = connected
                ? $"Permanently delete {subject}? Connected messages will be deleted on the mail provider; Local Mail copies will be deleted only here. This cannot be undone."
                : $"Permanently delete {subject} from Local Mail without moving to Deleted Items? This cannot be undone. The provider copy will remain untouched.";
        else if (!connected)
        {
            var permanent = chosen.Count(message => message.Folder == LocalFolderKinds.Deleted);
            if (permanent == chosen.Length)
                prompt = $"Permanently delete {subject} from Local Mail Deleted Items? The provider copy will remain untouched.";
            else if (permanent > 0)
                prompt = $"Permanently delete {permanent:N0} messages already in Deleted Items and move {chosen.Length - permanent:N0} others there? The provider copies will remain untouched.";
        }
        if (prompt is not null && MessageBox.Show(this, prompt, "Delete message", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        if (!connected)
        {
            // Local mail: the rows go now and the store follows. No wait cursor, no rebuilding of the folder
            // tree, no reloading of the list — rebuilding the tree re-selected the folder and loaded the list
            // twice over, which is what made a delete take seconds and the window blink. A message was there
            // and now it is not, which is all deleting ever looked like in the client this is modelled on.
            RemoveRows(chosen.Select(message => message.Id));
            // Into Deleted Items is a move like any filing, so Ctrl+Z puts it back. Gone for good is not.
            var binned = permanently ? new Dictionary<long, string>()
                : chosen.Where(message => message.Folder != LocalFolderKinds.Deleted).ToDictionary(message => message.Id, message => message.Folder);
            var undo = binned.Count > 0 ? new UndoableMove("Deleted Items",
                () => binned.Count(pair => _services.Database.RestoreDeleted(pair.Key, pair.Value))) : null;
            if (undo is not null) PushUndo(undo);
            var deleting = Task.Run(() => _services.Sync.DeleteManyAsync(chosen, permanently: permanently));
            _filing = deleting;
            try { await deleting; }
            catch (Exception error)
            {
                if (undo is not null && _undoMoves.TryPeek(out var top) && ReferenceEquals(top, undo)) _undoMoves.Pop();
                ShowError("Could not delete", error); LoadMessages(); return;
            }
            if (IsDisposed) return;
            _status.Text = (chosen.Length == 1 ? "Message deleted" : $"{chosen.Length:N0} messages deleted") + (undo is null ? "" : " — Ctrl+Z to undo");
            await RefreshReadStatesAsync([]);
            return;
        }
        try
        {
            AdjustConnectedRemovalCounts(chosen, -1);
            RemoveRows(chosen.Select(message => message.Id));
            _status.Text = "Saving deletion…";
            await PersistRemoteChangeAsync(() => _services.Sync.DeleteManyAsync(chosen, permanently: permanently));
            if (IsDisposed) return;
            ++_readCountsGeneration;
            _status.Text = $"Deleted {chosen.Length:N0} messages here — server changes queued";
            _ = RemoteOperationsTickAsync();
        }
        catch (Exception error)
        {
            if (!IsDisposed)
            {
                AdjustConnectedRemovalCounts(chosen, 1);
                _ = RefreshConnectedCountsAsync();
                LoadMessages(); _status.Text = "Could not queue deletion: " + error.Message;
            }
        }
    }

    private async Task AddOAuthAccountAsync(string provider, string mailboxMode)
    {
        if (!RequireReady()) return;
        var local = mailboxMode == MailboxModes.LocalKeep;
        using var dialog = new OAuthAccountForm(provider, local);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (_services.Accounts.Load().Any(value => value.Address.Equals(dialog.Address, StringComparison.OrdinalIgnoreCase)
            && value.EffectiveMailboxMode == mailboxMode))
        {
            MessageBox.Show(this, $"{dialog.Address} already has a {(local ? "Local Mail" : "Connected")} account.", dialog.Text);
            return;
        }
        await RunBusyAsync(null, "Waiting for browser authorization…", async () =>
        {
            var protocol = local ? MailProtocols.Pop3 : MailProtocols.Imap;
            // Local Mail accounts send as well as receive, so one sign-in grants both scopes.
            var credential = await _services.OAuth.AuthorizeAsync(provider, dialog.Address, protocol, includeSmtp: local);
            var name = local && dialog.AccountName.Equals(dialog.Address, StringComparison.OrdinalIgnoreCase)
                ? "local:" + dialog.Address.ToLowerInvariant()
                : dialog.AccountName;
            var account = new MailAccount(name, provider, dialog.Address, "INBOX",
                local ? LocalFolderKinds.Inbox : dialog.FolderLabel,
                null, credential, null, new KeepAliveSettings(), mailboxMode,
                new IncomingServerSettings(protocol, OAuthClient.DefaultHost(provider, protocol), local ? 995 : 993));
            if (local)
            {
                var inspection = await _services.Sync.InspectPopAsync(account);
                MessageBox.Show(this, $"POP connection succeeded. {inspection.NewMessageCount:N0} of {inspection.MessageCount:N0} messages "
                    + $"({DisplayBytes(inspection.TotalBytes)}) are ready to download. Nothing has been downloaded yet.",
                    "Local Mail account tested", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            _services.Accounts.Upsert(account);
            if (local)
            {
                // One sign-in serves both directions, including an address already imported from OE Classic.
                var identity = IdentityFor(account) ?? NewIdentityFor(account);
                _services.Identities.Upsert(SendingIdentityStore.ResetTestIfConnectionChanged(identity,
                    identity with { OAuth = credential, Password = null }));
            }
            await ReloadAccountsAsync(); LoadMessages();
        });
    }

    private async Task AddOtherAccountAsync(string mailboxMode, string? preset = null)
    {
        if (!RequireReady()) return;
        using var dialog = new ServerAccountForm(mailboxMode, preset);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var account = dialog.Account;
        if (_services.Accounts.Load().Any(value => value.Name.Equals(account.Name, StringComparison.OrdinalIgnoreCase)
            || (value.Address.Equals(account.Address, StringComparison.OrdinalIgnoreCase) && value.EffectiveMailboxMode == mailboxMode)))
        {
            MessageBox.Show(this, "This account already exists. Use Properties to change its servers.", dialog.Text);
            return;
        }
        if (dialog.BetaProvider is { } provider &&
            MessageBox.Show(this, $"Thank you for testing {provider} in Mailkeep. This provider has not yet been tested end to end. If you run into a problem, please report it on our GitHub issue tracker.\r\n\r\nOpen the issue tracker now?",
                $"{provider} beta support", MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            OpenLink("https://github.com/GanizaniSitara/mailkeep/issues/new");
        await RunBusyAsync(null, "Testing account servers…", async () =>
        {
            var receiving = account.IsConnected ? null : await InspectLocalIncomingAsync(account);
            _services.Accounts.Upsert(account);
            SendingIdentityTest? sending = null;
            if (dialog.Identity is { } identity)
            {
                if (IdentityFor(account) is { } existing)
                    identity = identity with { Id = existing.Id, OeAccountId = existing.OeAccountId, IsDefault = existing.IsDefault };
                _services.Identities.Upsert(identity);
                sending = await _services.Send.TestAsync(identity.Id);
            }
            await ReloadAccountsAsync(); LoadMessages();
            if (receiving is not null || sending is not null)
            {
                var outgoing = sending is null ? "Sending: not configured; you can add an outgoing server in Properties."
                    : sending.Ok ? "Sending: authenticated. Nothing sent."
                    : "Sending could not be verified: " + sending.Error + "\r\nYour settings are saved; correct them in Properties and test again.";
                MessageBox.Show(this, receiving + "\r\n\r\n" + outgoing, "Local Mail account tested", MessageBoxButtons.OK,
                    sending?.Ok == false ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
        });
    }

    private async Task ShowAccountsAsync()
    {
        if (!RequireReady()) return;
        while (true)
        {
            var data = await Task.Run(() => (_services.Accounts.Load(), _services.Identities.Load(), _services.Database.RecentFetches(500)));
            if (IsDisposed || Disposing) return;
            using var dialog = new AccountsForm(data.Item1, data.Item2, data.Item3);
            if (dialog.ShowDialog(this) != DialogResult.OK) break;
            if (dialog.RequestedAction == AccountsAction.Add) { await AddAccountAsync(dialog.MailboxMode); continue; }
            var account = _services.Accounts.Load().FirstOrDefault(value => value.Name.Equals(dialog.SelectedAccountName, StringComparison.OrdinalIgnoreCase));
            if (account is null) continue;
            switch (dialog.RequestedAction)
            {
                case AccountsAction.Properties: await EditAccountAsync(account); break;
                case AccountsAction.Authorize: await AuthorizeAccountAsync(account); break;
                case AccountsAction.SetDefaultSender: SetDefaultSender(account); break;
                case AccountsAction.Remove: await RemoveAccountAsync(account); break;
            }
            UpdateComposeState();
        }
        UpdateComposeState();
        LoadMessages();
    }

    private async Task AddAccountAsync(string mailboxMode)
    {
        using var chooser = new ProviderChoiceForm(mailboxMode);
        if (chooser.ShowDialog(this) != DialogResult.OK) return;
        if (chooser.Choice is "gmail" or "outlook") await AddOAuthAccountAsync(chooser.Choice, mailboxMode);
        else await AddOtherAccountAsync(mailboxMode, chooser.Choice == "other" ? null : chooser.Choice);
    }

    private SendingIdentity? IdentityFor(MailAccount account)
        => _services.Identities.Load().FirstOrDefault(value => value.Email.Equals(account.Address, StringComparison.OrdinalIgnoreCase));

    private static SendingIdentity NewIdentityFor(MailAccount account) => new(account.Name, null, account.Address, account.Address,
        null, false, account.Provider,
        new OutgoingServerSettings(AccountPropertiesForm.DefaultSmtpHost(account.Provider), AccountPropertiesForm.DefaultSmtpPort(account.Provider),
            account.Provider.Equals("gmail", StringComparison.OrdinalIgnoreCase) ? MailSecurityModes.SslOnConnect : MailSecurityModes.StartTls),
        null, null, null);

    private async Task EditAccountAsync(MailAccount account)
    {
        using var properties = new AccountPropertiesForm(account, IdentityFor(account));
        if (properties.ShowDialog(this) != DialogResult.OK) return;
        _services.Accounts.Upsert(properties.Account);
        // Sending belongs to Local Mail accounts only; a Connected account is a receiving mailbox.
        if (!account.IsConnected && properties.Identity is { } identity)
        {
            _services.Identities.Upsert(identity);
            if (identity.IsDefault) _services.Identities.SetDefault(identity.Id);
            // A credential typed into Properties is verified the same way a browser sign-in is.
            if (identity.HasCredential && !identity.IsReady) await VerifyAccountAsync(properties.Account);
        }
        await ReloadAccountsAsync(); LoadMessages();
    }

    /// <summary>One button does the whole thing: browser sign-in, then immediate proof — waiting mail counted
    /// over POP (nothing downloaded) and SMTP authenticated (nothing sent). Success unlocks sending.</summary>
    private async Task AuthorizeAccountAsync(MailAccount account)
    {
        if (account.Provider is not ("gmail" or "outlook" or "microsoft"))
        {
            MessageBox.Show(this, "Custom accounts use the passwords entered in Properties; saving Properties verifies them.",
                "Authorize account");
            return;
        }
        var trace = new AuthorizationTrace(_services.Paths, account.Name);
        trace.Mark("authorize_selected");
        try
        {
            var authorized = account;
            var saved = false;
            await RunBusyAsync(null, "Waiting for browser authorization…", async () =>
            {
                // One sign-in covers both directions for Local Mail, so receiving and sending never drift apart.
                var credential = await _services.OAuth.AuthorizeAsync(account.Provider, account.Address,
                    account.EffectiveProtocol, includeSmtp: !account.IsConnected, trace: trace.Mark);
                authorized = account with { OAuth = credential, AppPassword = null };
                trace.Mark("receiving_save_started");
                _services.Accounts.Upsert(authorized);
                trace.Mark("receiving_saved");
                var reloaded = _services.Accounts.Load().FirstOrDefault(value => value.Name == account.Name);
                trace.Mark(reloaded?.OAuth?.RefreshToken == credential.RefreshToken
                    ? "receiving_reload_matches" : "receiving_reload_mismatch");
                if (account.IsConnected) { saved = true; return; }
                var identity = IdentityFor(account) ?? NewIdentityFor(account);
                _services.Identities.Upsert(SendingIdentityStore.ResetTestIfConnectionChanged(identity,
                    identity with { OAuth = credential, Password = null }));
                trace.Mark("sending_saved");
                saved = true;
            }, trace);
            if (!saved)
            {
                trace.Mark("authorization_not_saved");
                return;
            }
            if (account.IsConnected)
            {
                // Proof is a real fetch, which also records the result the Accounts window shows next to the account.
                if (!ReferenceEquals(authorized, account)) await SyncOneAsync(authorized);
                trace.Mark("connected_sync_completed");
                return;
            }
            await VerifyAccountAsync(authorized, trace);
        }
        catch (Exception error) { trace.Fail(error); throw; }
    }

    private async Task VerifyAccountAsync(MailAccount account, AuthorizationTrace? trace = null)
    {
        if (account.IsConnected) return;
        string? receiving = null;
        SendingIdentityTest? sending = null;
        var identity = IdentityFor(account);
        await RunBusyAsync(null, $"Verifying {account.Address}…", async () =>
        {
            trace?.Mark("receiving_test_started");
            receiving = await InspectLocalIncomingAsync(account);
            trace?.Mark("receiving_test_passed");
            if (identity is not null)
            {
                trace?.Mark("sending_test_started");
                sending = await _services.Send.TestAsync(identity.Id);
                trace?.Mark(sending.Ok ? "sending_test_passed" : "sending_test_failed");
            }
        }, trace);
        var outgoing = identity is null ? "Sending: no outgoing server configured yet — use Properties."
            : sending is null ? "Sending could not be checked."
            : sending.Ok ? "Sending: authenticated. This address can now send." : "Sending: " + sending.Error;
        MessageBox.Show(this, (receiving ?? "Receiving could not be checked.") + "\r\n\r\n" + outgoing, $"{account.Address} verified", MessageBoxButtons.OK,
            sending?.Ok == false ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    private async Task<string> InspectLocalIncomingAsync(MailAccount account)
    {
        if (account.EffectiveProtocol == MailProtocols.Pop3)
        {
            var inspection = await _services.Sync.InspectPopAsync(account);
            return $"Receiving: {inspection.NewMessageCount:N0} new of {inspection.MessageCount:N0} messages "
                + $"({DisplayBytes(inspection.TotalBytes)}) waiting. Nothing downloaded.";
        }
        var count = await _services.Sync.InspectImapAsync(account);
        return $"Receiving: IMAP connected; {count:N0} messages in Inbox. Nothing downloaded.";
    }

    private void SetDefaultSender(MailAccount account)
    {
        if (IdentityFor(account) is not { } identity)
        {
            MessageBox.Show(this, $"{account.Address} has no outgoing server yet. Set one in Properties first.", "Default From address");
            return;
        }
        _services.Identities.SetDefault(identity.Id);
        _status.Text = $"{identity.Email} is now the default From address";
    }

    private async Task RemoveAccountAsync(MailAccount account)
    {
        var prompt = account.IsConnected
            ? $"Remove the Connected account {account.Address}? Mail already downloaded is retained and the server mailbox is untouched."
            : $"Remove the Local Mail account {account.Address}? Downloaded mail is retained.";
        if (MessageBox.Show(this, prompt, "Remove account", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _services.Accounts.Remove(account.Name);
        if (!account.IsConnected && IdentityFor(account) is { } identity
            && MessageBox.Show(this, $"Also remove {identity.Email} as a From address?", "Remove account",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            _services.Identities.Remove(identity.Id);
        await ReloadAccountsAsync(); LoadMessages();
    }

    private async void ShowKeepAlive()
    {
        if (!RequireReady() || _selectedAccount is null) return;
        using var dialog = new KeepAliveForm(_selectedAccount);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var name = _selectedAccount.Name;
        await RunBusyAsync(null, "Saving account settings…", async () =>
        {
            await Task.Run(() => _services.Accounts.SetKeepAlive(name, dialog.KeepAliveEnabled, dialog.IntervalHours));
            await ReloadAccountsAsync();
        });
    }

    private void ShowAddressBook()
    {
        if (!RequireReady()) return;
        using var book = new AddressBookForm(_services.Database);
        book.ShowDialog(this);
    }

    private async void ShowFetchLog()
    {
        if (!RequireReady()) return;
        await RunBusyAsync(null, "Loading fetch log…", async () =>
        {
            var rows = await Task.Run(() => _services.Database.RecentFetches(250));
            if (IsDisposed || Disposing) return;
            using var dialog = new FetchLogForm(rows);
            dialog.ShowDialog(this);
        });
    }

    private IReadOnlyList<SendingIdentity> ReadyIdentities()
        => _services.Identities.Load().Where(identity => identity.IsReady)
            .OrderByDescending(identity => identity.IsDefault).ThenBy(identity => identity.Email, StringComparer.OrdinalIgnoreCase).ToArray();

    private void UpdateComposeState()
    {
        var canCompose = _ready && ReadyIdentities().Count > 0;
        foreach (var item in _composeItems) item.Enabled = canCompose;
        foreach (var item in _replyItems) item.Enabled = canCompose && _currentContent is not null;
    }

    private Task ComposeNewAsync() => ComposeAsync(new ComposeSeed("New Message"));
    private Task ReplyAsync(bool all) => _currentContent is null ? Task.CompletedTask : ReplyToAsync(_currentContent, all);
    private Task ForwardAsync() => _currentContent is null ? Task.CompletedTask : ForwardContentAsync(_currentContent);

    // Taking the message explicitly is what lets an opened message window answer its own message rather than
    // whatever happens to be selected in the list behind it.
    private Task ReplyToAsync(MessageContent content, bool all)
        => ComposeAsync(CreateReplySeed(content, all));

    internal ComposeSeed CreateReplySeed(MessageContent content, bool all)
    {
        var identities = ReadyIdentities();
        var ownAddress = OwnAddress(content);
        var identity = identities.FirstOrDefault(value => value.Id.Equals(content.Summary.Account, StringComparison.OrdinalIgnoreCase))
            ?? identities.FirstOrDefault(value => value.Email.Equals(ownAddress, StringComparison.OrdinalIgnoreCase));
        var ownAddresses = _services.Accounts.Load().Select(value => value.Address)
            .Concat(_services.Identities.Load().Select(value => value.Email)).ToArray();
        return ComposeForm.Reply(content, all, ownAddresses, identity?.Id)
            with { OriginMessageId = content.Summary.Id, OriginKind = MessageMarkKinds.Replied };
    }
    private async Task ForwardContentAsync(MessageContent content)
    {
        try
        {
            var attachments = await Task.Run(() => content.Attachments.Select((file, index) => (file, index))
                .Where(item => !item.file.Inline)
                .Select(item => ExtractAnyAttachment(content.Summary.Id, item.index)).ToArray());
            await ComposeAsync(ComposeForm.Forward(content) with
            {
                AttachmentPaths = attachments, OriginMessageId = content.Summary.Id, OriginKind = MessageMarkKinds.Forwarded
            });
        }
        catch (Exception error) { ShowError("Could not forward message", error); }
    }

    private string OwnAddress(MessageContent message)
        => _services.Accounts.Load().FirstOrDefault(account => account.Name.Equals(message.Summary.Account, StringComparison.OrdinalIgnoreCase))?.Address
           ?? _services.Identities.Find(message.Summary.Account)?.Email
           ?? "";

    private Task ComposeAsync(ComposeSeed seed)
    {
        if (!RequireReady()) return Task.CompletedTask;
        if (seed.DraftId is not null && _composes.FirstOrDefault(window => window.DraftId == seed.DraftId) is { } existing)
        {
            if (existing.WindowState == FormWindowState.Minimized) existing.WindowState = FormWindowState.Normal;
            existing.Activate();
            return Task.CompletedTask;
        }
        var identities = ReadyIdentities();
        if (identities.Count == 0)
        {
            MessageBox.Show(this, "Authorize a Local Mail account before composing mail.\r\n\r\nTools > Accounts… > Local Mail",
                "New Message", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return Task.CompletedTask;
        }
        var compose = new ComposeForm(identities, seed, Path.Combine(_services.Paths.Root, "reading-pane"), _services.Database);
        compose.FindMessage = () => { _ = ShowSearchPromptAsync(); };
        var draftId = compose.DraftId;
        string? sendAfterClose = null;
        var refreshAfterClose = false;
        compose.CommitMessage = async () =>
        {
            var identityId = compose.IdentityId;
            var to = compose.To;
            var cc = compose.Cc;
            var bcc = compose.Bcc;
            var subject = compose.Subject;
            var body = compose.Body;
            var bodyHtml = compose.BodyHtml;
            var attachmentPaths = compose.AttachmentPaths;
            var completed = false;
            await RunBusyAsync(null, compose.ShouldQueue ? "Placing the message in the Outbox…" : "Saving draft…", async () =>
            {
                var draft = await Task.Run(() => _services.Send.SaveDraft(identityId, to, subject, body,
                    cc, bcc, draftId, attachmentPaths, bodyHtml));
                draftId = draft.Id;
                // Which message this answers or passes on. It marks that message only once it is delivered.
                if (seed is { OriginMessageId: { } origin, OriginKind: { } kind })
                    _services.Database.SetOutgoingOrigin(draft.Id, origin, kind);
                var sendNow = false;
                if (compose.ShouldQueue)
                {
                    await _services.Send.QueueAsync(draft.Id);
                    sendNow = OptionsForm.SendSettings.Immediately(_services.Database);
                    _status.Text = sendNow ? "Sending…" : "Message placed in the Outbox — it goes with the next Send/Receive";
                }
                else _status.Text = "Draft saved";
                // Queued is as good as done for the editor: a failed send leaves the message in the Outbox to
                // retry, so the window must not reopen for it.
                completed = true;
                if (sendNow)
                    sendAfterClose = draft.Id;
                refreshAfterClose = true;
            });
            // Keep the same editor and the saved draft ID after an error. Retrying must neither lose
            // the user's text/attachments nor create another draft if saving succeeded but queueing failed.
            return completed;
        };
        _composes.Add(compose);
        compose.FormClosed += (_, _) =>
        {
            _composes.Remove(compose);
            if (sendAfterClose is { } outgoingId)
                BeginInvoke(() => { _ = SendComposeInBackgroundAsync(outgoingId); });
            else if (refreshAfterClose)
                BeginInvoke(() => { _ = RefreshAfterComposeAsync(); });
        };
        // No owner: minimizing the main window must not hide an owned compose window.
        compose.StartPosition = FormStartPosition.Manual;
        var area = Screen.FromControl(this).WorkingArea;
        compose.Location = new Point(Math.Clamp(Left + (Width - compose.Width) / 2, area.Left, Math.Max(area.Left, area.Right - compose.Width)),
            Math.Clamp(Top + (Height - compose.Height) / 2, area.Top, Math.Max(area.Top, area.Bottom - compose.Height)));
        compose.Show();
        return Task.CompletedTask;
    }

    private async Task SendComposeInBackgroundAsync(string outgoingId)
    {
        try
        {
            var result = (await Task.Run(() => _services.Send.SendQueuedAsync([outgoingId]))).Single();
            _ = Task.Run(() => _services.Catalog.SyncOperational());
            if (IsDisposed || Disposing) return;
            _ = RefreshAfterComposeAsync();
            if (!result.Ok)
            {
                await ShowSendFailureAsync([result]);
                return;
            }
            _status.Text = "Message sent";
        }
        catch (Exception error)
        {
            if (!IsDisposed && !Disposing) ShowError("Could not send the queued message", error);
        }
    }

    private async Task RefreshAfterComposeAsync()
    {
        var generation = ++_readCountsGeneration;
        try
        {
            var sidebar = await Task.Run(LoadSidebarData);
            if (IsDisposed || Disposing) return;
            if (generation == _readCountsGeneration) ReloadAccounts(sidebar);
            await LoadMessagesAsync();
        }
        catch (Exception error)
        {
            if (!IsDisposed && !Disposing)
                _status.Text = "Message saved; view refresh failed: " + FirstLine(error.Message);
        }
    }

    private async Task EditDraftAsync(OutgoingMessage draft)
    {
        if (draft.Status != OutgoingStates.Draft) return;
        try
        {
            var attachments = await Task.Run(() => _services.Database.GetOutgoingAttachments(draft.Id)
                .Select((attachment, index) => (attachment, index)).Where(item => !item.attachment.Inline)
                .Select(item => _services.Database.ExtractOutgoingAttachment(draft.Id, item.index)).ToArray());
            await ComposeAsync(ComposeForm.Edit(draft) with { AttachmentPaths = attachments });
        }
        catch (Exception error) { ShowError("Could not open draft", error); }
    }

    /// <summary>Delivery happens only on the reader's own Send/Receive or Send Outbox; nothing here runs on a timer or on startup.</summary>
    private async Task SendOutboxAsync(bool announceEmpty)
    {
        if (!RequireReady()) return;
        var queued = await Task.Run(() => _services.Database.ListOutgoing(OutgoingStates.Outbox, 500));
        if (queued.Count == 0)
        {
            if (announceEmpty) MessageBox.Show(this, "The Outbox is empty.", "Send Outbox", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // No second question, as in OE: the reader already pressed Send once. This runs only from the reader's own
        // Send/Receive or Send Outbox; agents send through the CLI, which keeps its --execute guard.
        await RunBusyAsync(null, "Sending queued messages…", async () =>
        {
            var results = await _services.Send.SendQueuedAsync(queued.Select(message => message.Id).ToArray());
            var failures = results.Where(result => !result.Ok).ToArray();
            await ReloadAccountsAsync();
            LoadMessages();
            _ = Task.Run(() => _services.Catalog.SyncOperational());
            if (failures.Length > 0)
            {
                await ShowSendFailureAsync(failures);
                return;
            }
            _status.Text = $"Sent {results.Count} message(s)";
        }, showWaitCursor: announceEmpty);
    }

    /// <summary>Prints the message as it reads on screen — headers then body — through the system dialog.</summary>
    private void PrintMessage()
    {
        if (_currentOutgoing is { } outgoing)
        {
            using var document = CreateOutgoingPrintDocument(outgoing, _services.Identities.Find(outgoing.SendingIdentityId));
            PrintDocument(document);
        }
        else if (_currentContent is { } content) PrintContent(content);
    }

    private void PrintContent(MessageContent content)
    {
        if (!string.IsNullOrWhiteSpace(content.BodyHtml))
        {
            using var preview = new HtmlPrintForm(content, Path.Combine(_services.Paths.Root, "reading-pane"));
            if (preview.ShowDialog(this) != DialogResult.Abort) return;
        }
        using var document = CreatePrintDocument(content);
        PrintDocument(document);
    }

    private void PrintDocument(System.Drawing.Printing.PrintDocument document)
    {
        using var dialog = new PrintDialog { Document = document, UseEXDialog = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { document.Print(); }
        catch (Exception error) { ShowError("Could not print", error); }
    }

    internal static System.Drawing.Printing.PrintDocument CreatePrintDocument(MessageContent content)
    {
        // Built from the message rather than the pane's labels, so printing from an opened window prints
        // that window's message.
        var summary = content.Summary;
        var sender = string.IsNullOrWhiteSpace(summary.SenderName) ? summary.Sender : $"{summary.SenderName} <{summary.Sender}>";
        var when = summary.Date?.ToLocalTime().ToString("f") ?? "Date unavailable";
        var header = $"From: {sender}\r\nTo: {string.Join(", ", summary.Recipients)}\r\n{when}\r\nSubject: {summary.Subject}\r\n\r\n";
        var text = header + (content.BodyText.Length > 0 ? content.BodyText : StripTags(content.BodyHtml ?? ""));
        return CreateTextPrintDocument(summary.Subject, text);
    }

    internal static System.Drawing.Printing.PrintDocument CreateOutgoingPrintDocument(OutgoingMessage message, SendingIdentity? identity)
    {
        var sender = identity is null ? message.SendingIdentityId : $"{identity.DisplayName} <{identity.Email}>";
        var when = message.Status == OutgoingStates.Draft
            ? $"Draft saved {message.UpdatedAt.ToLocalTime():f}"
            : $"Queued {message.CreatedAt.ToLocalTime():f}";
        var header = $"From: {sender}\r\nTo: {string.Join(", ", message.To)}\r\n";
        if (message.Cc.Count > 0) header += $"Cc: {string.Join(", ", message.Cc)}\r\n";
        if (message.Bcc.Count > 0) header += $"Bcc: {string.Join(", ", message.Bcc)}\r\n";
        header += $"{when}\r\nSubject: {message.Subject}\r\n\r\n";
        return CreateTextPrintDocument(message.Subject, header + message.BodyText);
    }

    private static System.Drawing.Printing.PrintDocument CreateTextPrintDocument(string subject, string text)
    {
        var remaining = text.Replace("\r\n", "\n");
        var document = new System.Drawing.Printing.PrintDocument { DocumentName = subject };
        var font = new Font("Tahoma", 9f);
        document.Disposed += (_, _) => font.Dispose();
        document.BeginPrint += (_, _) => remaining = text.Replace("\r\n", "\n");
        document.PrintPage += (_, e) =>
        {
            var area = e.MarginBounds;
            using var format = new StringFormat { Trimming = StringTrimming.Word, FormatFlags = StringFormatFlags.LineLimit };
            e.Graphics!.MeasureString(remaining, font, area.Size, format, out var printed, out int _unused);
            if (printed == 0 && remaining.Length > 0)
                throw new InvalidOperationException("The print margins leave no room for text. Choose smaller margins or a larger paper size.");
            e.Graphics.DrawString(remaining, font, Brushes.Black, area, format);
            remaining = remaining[printed..];
            e.HasMorePages = remaining.Length > 0;
        };
        return document;
    }

    private static string StripTags(string html)
        => System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ").Replace("&nbsp;", " ");

    /// <summary>Files a message into another Local Mail folder. Imported mail is left alone: the other client
    /// still owns where those messages live, and moving one here would only be undone by the next sync.</summary>
    private void MoveMessage()
    {
        if (SelectedDrafts() is { Length: > 0 } drafts)
        {
            var folders = _services.Database.ListLocalFolders().Where(folder => folder.Kind != LocalFolderKinds.Outbox).ToArray();
            using var chooser = new FolderChoiceForm(folders, _services.Database.ListLocalFolders());
            if (chooser.ShowDialog(this) == DialogResult.OK && chooser.Chosen is { } target)
                _ = FileDraftsAsync(drafts, target.Key, target.Name);
            return;
        }
        MoveMessages(Selected());
    }

    private async Task FileDraftsAsync(OutgoingMessage[] drafts, string folder, string name)
    {
        try
        {
            var from = new Dictionary<string, string>();
            var undo = new UndoableMove(name, () =>
            {
                var count = 0;
                foreach (var (id, earlier) in from)
                    if (_services.Database.DraftFolder(id) == folder) count += _services.Database.MoveDrafts([id], earlier);
                return count;
            });
            PushUndo(undo);
            var writing = Task.Run(() =>
            {
                foreach (var draft in drafts) from[draft.Id] = _services.Database.DraftFolder(draft.Id);
                return _services.Database.MoveDrafts(drafts.Select(draft => draft.Id).ToArray(), folder);
            });
            _filing = writing;
            int moved;
            try { moved = await writing; }
            catch
            {
                if (_undoMoves.TryPeek(out var top) && ReferenceEquals(top, undo)) _undoMoves.Pop();
                throw;
            }
            if (IsDisposed) return;
            await LoadMessagesAsync();
            _status.Text = $"{moved:N0} drafts moved to {name} — still editable and unsent — Ctrl+Z to undo";
        }
        catch (Exception error) { if (!IsDisposed) { ShowError("Could not move the drafts", error); LoadMessages(); } }
    }

    private void MoveMessages(MessageSummary[] chosen)
    {
        if (chosen.Length == 0) return;
        if (!CanFileLocally(chosen))
        {
            if (!chosen.All(IsConnectedMessage))
            {
                MessageBox.Show(this, "Local, Connected and imported messages cannot be filed together.",
                    "Move message", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this,
                    $"Copy {chosen.Length:N0} Connected message(s) into Local Mail?\r\n\r\nThe originals will remain in the server mailbox.",
                    "Copy to Local Mail", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            CopyMessagesToLocal(chosen);
            return;
        }
        // Only a folder none of them is already in: filing four messages into the folder holding one of them
        // is a fair thing to ask, so the folder is excluded only when it holds all of them.
        var folders = _services.Database.ListLocalFolders()
            .Where(folder => folder.Kind is not (LocalFolderKinds.Drafts or LocalFolderKinds.Outbox))
            .Where(folder => chosen.Any(message => folder.Key != message.Folder))
            .ToArray();
        if (folders.Length == 0) return;
        using var chooser = new FolderChoiceForm(folders, _services.Database.ListLocalFolders());
        if (chooser.ShowDialog(this) != DialogResult.OK || chooser.Chosen is not { } target) return;
        _ = FileMessagesAsync(chosen, target.Key, target.Name);
    }

    /// <summary>Mail that can be filed: this mailbox's own, and imported mail, which filing adopts. Only
    /// Connected mail cannot — the server owns where it lives.</summary>
    private bool CanFileLocally(IReadOnlyList<MessageSummary> messages) => messages.Count > 0 && messages.All(message =>
        message.Source == MailContract.ArchiveSource
        || message.Source == MailContract.LiveSource && (message.IsLocalSentCopy || message.IsLocalStoredCopy || _loadedAccounts.Any(account =>
            !account.IsConnected && account.Name.Equals(message.Account, StringComparison.OrdinalIgnoreCase))));

    private bool IsConnectedMessage(MessageSummary message) => message.Source == MailContract.LiveSource
        && _loadedAccounts.Any(account => account.IsConnected && account.Name.Equals(message.Account, StringComparison.OrdinalIgnoreCase));

    private async void CopyMessagesToLocal(IReadOnlyList<MessageSummary> messages)
    {
        var folders = _services.Database.ListLocalFolders()
            .Where(folder => folder.Kind is not (LocalFolderKinds.Drafts or LocalFolderKinds.Outbox)).ToArray();
        using var chooser = new FolderChoiceForm(folders, _services.Database.ListLocalFolders(), copying: true);
        if (chooser.ShowDialog(this) != DialogResult.OK || chooser.Chosen is not { } target) return;
        try
        {
            var ids = messages.Select(message => message.Id).ToArray();
            var copying = Task.Run(() => _services.Database.CopyMessagesToLocal(ids, target.Key));
            _filing = copying;
            var copied = await copying;
            await ReloadAccountsAsync();
            LoadMessages();
            _status.Text = $"{copied:N0} messages copied to {target.Name}; originals remain Connected";
        }
        catch (Exception error) { ShowError("Could not copy the messages", error); }
    }

    /// <summary>Files messages the way Delete removes them: the rows leave the list at once and the store follows
    /// off the window's thread, then only the counts are brought up to date. It used to move, rebuild the whole
    /// folder tree, recount the archive and reload the list, all on the window's thread and, for a drag, inside
    /// the drop itself — the window stood frozen with the messages still showing in the folder they had left.</summary>
    private async Task FileMessagesAsync(IReadOnlyList<MessageSummary> messages, string folder, string name)
    {
        if (!CanFileLocally(messages)) return;
        var ids = messages.Select(message => message.Id).ToArray();
        // Imported mail is adopted rather than moved: Ctrl+Z puts a fresh copy away again, so the imported
        // message shows where it was, and sends one adopted before back to the folder it came from.
        var adopting = messages.Where(message => message.Source == MailContract.ArchiveSource).Select(message => message.Id).ToArray();
        var own = ids.Except(adopting).ToArray();
        var from = messages.Where(message => message.Folder != folder && message.Source != MailContract.ArchiveSource)
            .ToDictionary(message => message.Id, message => message.Folder);
        RemoveRows(ids);
        _status.Text = messages.Count == 1 ? $"Moving to {name}…" : $"Moving {messages.Count:N0} messages to {name}…";
        IReadOnlyDictionary<long, (long Id, string Folder)> before = new Dictionary<long, (long, string)>();
        IReadOnlyDictionary<long, (long Id, string Folder)> after = before;
        // Recorded now, in the order the filings were asked for, not when each finishes: a Ctrl+Z pressed while
        // this one is still being written must take back this one, not the one before it.
        var undo = new UndoableMove(name, () =>
        {
            var database = _services.Database;
            var now = database.MessageFolders(from.Keys.Concat(after.Values.Select(copy => copy.Id)).ToArray());
            var count = 0;
            foreach (var group in from.Where(pair => now.GetValueOrDefault(pair.Key) == folder).GroupBy(pair => pair.Value))
                count += database.MoveMessages(group.Select(pair => pair.Key).ToArray(), group.Key);
            foreach (var (archiveId, copy) in after)
            {
                if (now.GetValueOrDefault(copy.Id) != folder) continue;
                if (before.TryGetValue(archiveId, out var earlier)) count += database.MoveMessages([copy.Id], earlier.Folder);
                else if (database.RemoveMessage(copy.Id)) count++;
            }
            return count;
        });
        PushUndo(undo);
        int moved;
        var writing = Task.Run(() =>
        {
            var count = own.Length > 0 ? _services.Database.MoveMessages(own, folder) : 0;
            if (adopting.Length == 0) return count;
            before = _services.Database.AdoptedCopies(adopting);
            count += _services.Database.AdoptMessages(adopting, folder);
            after = _services.Database.AdoptedCopies(adopting);
            return count;
        });
        _filing = writing;
        try { moved = await writing; }
        catch (Exception error)
        {
            if (undo is not null && _undoMoves.TryPeek(out var top) && ReferenceEquals(top, undo)) _undoMoves.Pop();
            if (IsDisposed) return;
            ShowError(error is FolderStorageRecoveryException ? "Folder recovery needed; check current folders" : "Could not move the messages", error);
            await LoadMessagesAsync();
            return;
        }
        if (IsDisposed) return;
        _status.Text = $"{moved:N0} messages moved to {name} — Ctrl+Z to undo";
        await RefreshReadStatesAsync([]);
    }

    /// <summary>A move that can be taken back — a filing, a delete to Deleted Items, a draft filed — and how:
    /// run off the window's thread, it puts back what is still where the move left it and counts what it did.</summary>
    private sealed record UndoableMove(string Name, Func<int> Undo);
    private Stack<UndoableMove> _undoMoves = new();

    private void PushUndo(UndoableMove undo)
    {
        _undoMoves.Push(undo);
        while (_undoMoves.Count > 20) _undoMoves = new Stack<UndoableMove>(_undoMoves.Take(20).Reverse());
    }
    /// <summary>The filing still being written, which an undo waits for before it looks at anything.</summary>
    private Task _filing = Task.CompletedTask;

    /// <summary>Ctrl+Z: the last filing, put back. Each message returns to the folder it was filed from — a
    /// selection gathered from All Local Mail goes back to several. A message moved again since, or deleted,
    /// is left where it now is.</summary>
    private async Task UndoMoveAsync()
    {
        if (!RequireReady()) return;
        if (!_undoMoves.TryPop(out var last)) { _status.Text = "Nothing to undo"; return; }
        try { await _filing; } catch (Exception) { } // a failed filing has already taken itself off the list
        try
        {
            var returned = await Task.Run(last.Undo);
            if (IsDisposed) return;
            await LoadMessagesAsync();
            if (IsDisposed) return;
            _status.Text = returned == 0 ? $"Nothing to undo — those messages are no longer in {last.Name}"
                : returned == 1 ? $"Moved 1 message back out of {last.Name}" : $"Moved {returned:N0} messages back out of {last.Name}";
            await RefreshReadStatesAsync([]);
        }
        catch (Exception error) { if (!IsDisposed) { ShowError("Could not undo the move", error); LoadMessages(); } }
    }

    /// <summary>Alt+1 to Alt+0: the selected mail filed straight into the folder set for that key.</summary>
    private async Task FileByShortcutAsync(int digit)
    {
        if (!RequireReady()) return;
        var key = FilingShortcuts.Folder(_services.Database, digit);
        var folder = key is null ? null : _services.Database.ListLocalFolders().FirstOrDefault(value => value.Key == key);
        if (folder is null)
        {
            _status.Text = key is null ? $"Alt+{digit} has no folder yet — choose one under Tools > Options > Filing shortcuts"
                : $"The folder for Alt+{digit} no longer exists — choose another under Tools > Options";
            return;
        }
        var chosen = Selected();
        if (chosen.Length == 0) { _status.Text = "Select the messages to file first"; return; }
        if (!CanFileLocally(chosen))
        {
            _status.Text = "Connected mail cannot be filed with a shortcut — use Move to copy it into Local Mail";
            return;
        }
        if (chosen.All(message => message.Folder == folder.Key)) { _status.Text = $"Already in {folder.Name}"; return; }
        var path = FilingShortcuts.Destinations(_services.Database.ListLocalFolders()).FirstOrDefault(value => value.Key == folder.Key)?.Name ?? folder.Name;
        await FileMessagesAsync(chosen, folder.Key, path);
    }

    /// <summary>The filing keys, taken before the menus see them: Alt and a digit is no menu's, and Ctrl+Z is
    /// left to a text box or a folder being renamed, where it undoes typing as everywhere else.</summary>
    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (keys is Keys.Delete or (Keys.Control | Keys.D) or (Keys.Shift | Keys.Delete))
            TimingLog.Mark($"delete key: treeFocus={_localTree.ContainsFocus} listFocus={_messages.ContainsFocus} editing={EditingText()} deletableFolder={SelectedFolder().CanDelete} ready={_ready}");
        if (MainShortcut(keys) is { } action) { action(); return true; }
        if ((keys & Keys.Modifiers) == Keys.Alt && (keys & Keys.KeyCode) is >= Keys.D0 and <= Keys.D9)
        {
            _ = FileByShortcutAsync((keys & Keys.KeyCode) - Keys.D0);
            return true;
        }
        if (keys == (Keys.Control | Keys.Z) && !EditingText())
        {
            _ = UndoMoveAsync();
            return true;
        }
        return base.ProcessCmdKey(ref message, keys);
    }

    private bool EditingText()
    {
        var focus = GetFocus();
        if (focus == IntPtr.Zero) return false;
        // A text box of ours says whether it can be typed in; any other edit control — the label editor a
        // folder rename opens — can.
        if (FromHandle(focus) is TextBoxBase box) return !box.ReadOnly;
        var name = new System.Text.StringBuilder(64);
        GetClassName(focus, name, name.Capacity);
        return name.ToString().Contains("EDIT", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int capacity);

    private void OpenHtml() { if (_currentContent is { } content) OpenHtmlFor(content); }

    private void OpenHtmlFor(MessageContent content)
    {
        if (string.IsNullOrWhiteSpace(content.BodyHtml)) return;
        var directory = Path.Combine(Path.GetTempPath(), "Mailkeep", "html-preview");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"message-{content.Summary.Id}.html");
        File.WriteAllText(path, SafeHtml(content.BodyHtml));
        const string brave = @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe";
        if (!File.Exists(brave)) { MessageBox.Show(this, "Brave Browser was not found.", "Open HTML"); return; }
        Process.Start(new ProcessStartInfo(brave, path) { UseShellExecute = false });
    }

    /// <summary>DetectUrls underlines bare www. links without a scheme, and ShellExecute treats those as
    /// file paths, so the scheme is restored before anything is launched. Non-web links stay inert.</summary>
    internal static void OpenLink(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) return;
        var url = link.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + link : link;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        const string brave = @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe";
        if (File.Exists(brave)) Process.Start(new ProcessStartInfo(brave, url) { UseShellExecute = false });
        else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>The letter with everything that could run or fetch a page taken out: scripts, frames, forms,
    /// embedded objects and a refresh.</summary>
    internal static string StripActiveContent(string html)
    {
        var body = Regex.Replace(html, @"<(script|iframe|object|embed|form|base)\b[^>]*>.*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = Regex.Replace(body, @"<(script|iframe|object|embed|form|base)\b[^>]*/?>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return Regex.Replace(body, "<meta\\b[^>]*http-equiv\\s*=\\s*['\\\"]?refresh[^>]*>", "", RegexOptions.IgnoreCase);
    }

    internal static string SafeHtml(string html, IEnumerable<string>? imageOrigins = null)
    {
        var body = StripActiveContent(html);
        var allowed = (imageOrigins ?? []).Select(value => RemoteImages.TryOrigin(value, out var origin) ? origin : null)
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
        var imagePolicy = string.Join(" ", allowed);
        var csp = "default-src 'none'; img-src data:" + (imagePolicy.Length == 0 ? "" : " " + imagePolicy)
            + "; style-src 'unsafe-inline'";
        return "<!doctype html><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\""
            + WebUtility.HtmlEncode(csp) + "\">" + body;
    }

    private void ResizeAccountColumns()
    {
        if (_accounts.Columns.Count < 2) return;
        _accounts.Columns[1].Width = 58;
        _accounts.Columns[0].Width = Math.Max(90,
            _accounts.ClientSize.Width - _accounts.Columns[1].Width - SystemInformation.VerticalScrollBarWidth - 2);
    }

    private void DrawAccountSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        var item = e.Item;
        if (item?.Tag is not MailAccount account) { e.DrawDefault = true; return; }
        var count = _accountCounts.GetValueOrDefault(account.Name);
        var selected = item.Selected;
        var background = selected ? (_accounts.Focused ? SystemColors.Highlight : SystemColors.ControlLight) : _accounts.BackColor;
        var foreground = selected && _accounts.Focused ? SystemColors.HighlightText : _accounts.ForeColor;
        using var backgroundBrush = new SolidBrush(background);
        e.Graphics.FillRectangle(backgroundBrush, e.Bounds);

        if (e.ColumnIndex == 0)
        {
            var icon = _accounts.SmallImageList?.Images[item.ImageKey];
            var left = e.Bounds.Left + 3;
            if (icon is not null)
            {
                e.Graphics.DrawImageUnscaled(icon, left, e.Bounds.Top + Math.Max(0, (e.Bounds.Height - icon.Height) / 2));
                left += icon.Width + 4;
            }
            using var bold = count.New > 0 ? new Font(_accounts.Font, FontStyle.Bold) : null;
            TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? "", bold ?? _accounts.Font,
                new Rectangle(left, e.Bounds.Top, Math.Max(0, e.Bounds.Right - left - 2), e.Bounds.Height), foreground,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            return;
        }

        var badgeText = count.Total.ToString("N0");
        using var badgeBold = count.New > 0 ? new Font(_accounts.Font, FontStyle.Bold) : null;
        var badgeFont = badgeBold ?? _accounts.Font;
        var badgeWidth = _accountBadgeWidth;
        var badgeHeight = Math.Min(16, e.Bounds.Height - 2);
        var badge = new Rectangle(e.Bounds.Right - badgeWidth - 5, e.Bounds.Top + (e.Bounds.Height - badgeHeight) / 2, badgeWidth, badgeHeight);
        using var path = PillPath(badge);
        using var badgeBrush = new SolidBrush(count.New > 0 ? Color.FromArgb(38, 112, 190) : Color.FromArgb(232, 232, 232));
        using var badgePen = new Pen(count.New > 0 ? Color.FromArgb(20, 72, 135) : Color.FromArgb(165, 165, 165));
        var oldSmoothing = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        e.Graphics.FillPath(badgeBrush, path);
        e.Graphics.DrawPath(badgePen, path);
        e.Graphics.SmoothingMode = oldSmoothing;
        // Centre by measurement rather than flags: DrawText's own centring biases left inside the
        // rounded rectangle because GDI adds asymmetric padding to the cell.
        var text = TextRenderer.MeasureText(e.Graphics, badgeText, badgeFont, Size.Empty, TextFormatFlags.NoPadding);
        var textOrigin = new Point(badge.Left + (badge.Width - text.Width + 1) / 2, badge.Top + (badge.Height - text.Height + 1) / 2);
        TextRenderer.DrawText(e.Graphics, badgeText, badgeFont, textOrigin,
            count.New > 0 ? Color.White : Color.FromArgb(65, 65, 65),
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        if (selected && _accounts.Focused) ControlPaint.DrawFocusRectangle(e.Graphics, item.Bounds, foreground, background);
    }

    private static System.Drawing.Drawing2D.GraphicsPath PillPath(Rectangle bounds)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = Math.Max(2, bounds.Height - 1);
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 90, 180);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 180);
        path.CloseFigure();
        return path;
    }

    private void SelectConnectedAccount(MailAccount account)
    {
        if (_repopulatingAccounts) return;
        if (_selectedAccount is not null && _selectedAccount.Name.Equals(account.Name, StringComparison.OrdinalIgnoreCase)) return;

        // Clear local tree selection so we don't have two active panes
        _localTree.SelectedNode = null;

        _selectedAccount = account; _selectedArchive = null;
        SaveLayoutSoon();
        _ = ShowConnectedAccountAsync(account);
    }

    /// <summary>What is here first, then the check — in that order, so the list's own status line does not land on
    /// top of "Checking…" and leave a twenty-second server check looking like nothing is happening.</summary>
    private async Task ShowConnectedAccountAsync(MailAccount account)
    {
        await LoadMessagesAsync();
        await CheckConnectedAccountAsync(account);
    }

    private static string FirstLine(string? text) => (text ?? "").Split(Environment.NewLine.ToCharArray(), StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    /// <summary>When each connected account was last checked from here, so clicking back and forth does not
    /// ask the server again every time.</summary>
    private readonly Dictionary<string, DateTimeOffset> _accountChecked = new(StringComparer.OrdinalIgnoreCase);

    private void RefreshCurrent()
    {
        if (_selectedAccount is { IsConnected: true } account) _ = SyncOneAsync(account);
        else LoadMessages();
    }

    /// <summary>Show the cached Inbox immediately, then reconcile it with the server without blocking the window.</summary>
    private async Task CheckConnectedAccountAsync(MailAccount account)
    {
        if (!_ready || _syncRunning) return;
        if (_accountChecked.TryGetValue(account.Name, out var checkedAt) && DateTimeOffset.UtcNow - checkedAt < TimeSpan.FromMinutes(2)) return;
        bool Showing() => !IsDisposed && _selectedAccount?.Name.Equals(account.Name, StringComparison.OrdinalIgnoreCase) == true;
        _syncRunning = true;
        if (Showing()) _status.Text = $"Checking {account.Address} for new mail…";
        try
        {
            var result = await Task.Run(() => _services.Sync.SyncAsync(account.Name));
            if (IsDisposed) return;
            if (!result.Ok)
            {
                if (Showing()) _status.Text = $"{account.Address}: could not check for new mail — {FirstLine(result.Error)}";
                return;
            }
            _accountChecked[account.Name] = DateTimeOffset.UtcNow;
            var counts = await Task.Run(() => (_services.Database.AccountMessageCounts(),
                _services.Database.AccountUnreadCounts(), _services.Database.AccountLatestDates()));
            if (IsDisposed || Disposing) return;
            (_accountCounts, _accountUnreadCounts, _accountLatest) = counts;
            PopulateAccountList();
            if (Showing()) await LoadMessagesAsync();
            if (Showing()) _status.Text = result.NewCount == 0 ? $"Connected — {account.Address}: no new mail"
                : $"Connected — {account.Address}: {result.NewCount} new";
        }
        catch (Exception error)
        {
            if (Showing()) _status.Text = $"{account.Address}: could not check for new mail — {FirstLine(error.Message)}";
        }
        finally { _syncRunning = false; }
    }

    private void SelectTreeNode(TreeNode? node)
    {
        if (node?.Tag is not null) SaveLayoutSoon();
        // Drop the ghost selection from the connected accounts list
        if (node?.Tag != null)
        {
            _accounts.SelectedItems.Clear();
        }

        if (node?.Tag is LocalFolder folder)
        {
            _selectedAccount = null; _selectedArchive = null; _selectedLocalFolder = folder.Key; LoadMessages(); return;
        }
        if (Equals(node?.Tag, "local-all"))
        {
            _selectedAccount = null; _selectedArchive = null; _selectedLocalFolder = "local-all"; LoadMessages(); return;
        }
        if (node?.Tag is MailFolder archived)
        {
            _selectedAccount = null; _selectedArchive = archived; LoadMessages();
        }
    }

    private async Task RunBusyAsync(ToolStripItem? source, string message, Func<Task> action, AuthorizationTrace? authorizationTrace = null,
        bool showWaitCursor = true)
    {
        var previous = source?.Enabled ?? false;
        var previousCursor = UseWaitCursor;
        if (source is not null) source.Enabled = false;
        if (showWaitCursor) UseWaitCursor = true;
        _status.Text = message;
        try { await action(); }
        catch (OperationCanceledException) when (_receiveCancellation?.IsCancellationRequested == true)
        {
            await Task.Yield();
            _downloadProgress?.Complete([], "Receive stopped");
            await RefreshAfterReceiveAsync(reloadMessages: true);
            _status.Text = "Receive stopped. Downloaded messages have been kept.";
        }
        catch (Exception exception) { authorizationTrace?.Fail(exception); ShowError(message, exception); }
        finally { if (showWaitCursor) UseWaitCursor = previousCursor; if (source is not null) source.Enabled = previous; }
    }

    private bool RequireReady() { if (_ready) return true; _status.Text = "Mailkeep is still loading…"; return false; }
    private string DefaultIdentityLabel()
    {
        var identities = _services.Identities.Load();
        var identity = identities.FirstOrDefault(value => value.IsDefault) ?? identities.FirstOrDefault();
        return identity is null ? "Mailkeep" : string.IsNullOrWhiteSpace(identity.DisplayName) ? identity.Email : identity.DisplayName;
    }
    private static string DisplayBytes(long bytes)
        => bytes < 1024 ? $"{bytes:N0} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:N1} KB" : $"{bytes / 1024d / 1024d:N1} MB";
    private void SetReadyControls(bool enabled)
    {
        foreach (var item in _readyItems) item.Enabled = enabled;
        _localTree.Enabled = _accounts.Enabled = _messages.Enabled = enabled;
    }
    private async Task ShowSendFailureAsync(IReadOnlyList<SendResult> failures)
    {
        var notice = await SendFailureNotice.CreateAsync(_services.Paths.Root, failures);
        if (IsDisposed || Disposing) return;
        _status.Text = failures.Count == 1 ? "Message not sent; kept in Outbox" : $"{failures.Count} messages not sent; kept in Outbox";
        MessageBox.Show(this, notice.Message, "Could not send mail", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void ShowError(string title, Exception exception, IWin32Window? owner = null)
    {
        // The full exception (stack and all) goes to errors.log — "Object reference not set" in a dialog
        // is undebuggable without it.
        try
        {
            File.AppendAllText(Path.Combine(_services.Paths.Root, "errors.log"),
                $"{DateTimeOffset.Now:O} {title}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
        // The status label gets one line only — a multi-line message (28 failed accounts) would grow the
        // whole status strip to fit it. The dialog still carries the full text.
        var lines = exception.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _status.Text = lines.Length > 1 ? $"{lines[0]} (+{lines.Length - 1} more — see the error dialog or fetch log)" : exception.Message;
        MessageBox.Show(owner ?? this, exception.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void ClearPreview()
    {
        ++_previewGeneration;
        // The page view keeps its place: hiding it and showing the text pane here, only for the next message
        // to swap them straight back, flashed the pane white on every change of folder.
        _currentContent = null; _currentOutgoing = null; _subject.Text = "Select a message to read it"; _subject.ForeColor = SystemColors.GrayText; _from.Text = ""; _to.Text = ""; _date.Text = ""; _remoteImages.Clear(); _html.Clear(); _html.Visible = false; _body.Visible = true; _body.Clear(); _attachments.SetMessage(null);
        SetPreviewLabels(false);
        if (_delete is not null) _delete.Enabled = false;
        if (_openHtml is not null) _openHtml.Enabled = false;
        if (_print is not null) _print.Enabled = false;
        if (_move is not null) _move.Enabled = false;
        if (_ready) UpdateComposeState();
    }

    private ToolStripMenuItem Item(string text, Action action, string accessibleName)
    {
        var item = new ToolStripMenuItem(text) { AccessibleName = accessibleName };
        item.Click += (_, _) => action();
        return item;
    }

    private ToolStripMenuItem ComposeItem(string text, Func<Task> action, string accessibleName, bool needsMessage)
    {
        var item = Item(text, action, accessibleName);
        item.ShortcutKeyDisplayString = accessibleName switch
        {
            "New Mail menu item" => "Ctrl+N", "Reply menu item" => "Ctrl+R",
            "Reply All menu item" => "Ctrl+Shift+R", "Forward menu item" => "Ctrl+F", _ => ""
        };
        item.Enabled = false;
        _composeItems.Add(item);
        if (needsMessage) _replyItems.Add(item);
        return item;
    }

    private ToolStripButton CommandItem(string text, Image image, EventHandler click, bool enabled = true)
    {
        var button = new ToolStripButton(text, image)
        {
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            TextImageRelation = TextImageRelation.ImageAboveText,
            AccessibleName = text,
            ToolTipText = text,
            Enabled = enabled,
            AutoSize = true,
            // Sized from OE, not by feel: its label-to-label gap within a toolbar group measures 42-45px.
            Padding = new Padding(16, 0, 16, 0),
            Margin = new Padding(2, 0, 2, 0)
        };
        button.Click += click;
        if (enabled) _readyItems.Add(button);
        return button;
    }
    private static ToolStripMenuItem Item(string text, Func<Task> action, string accessibleName,
        string? shortcutText = null)
    {
        var item = new ToolStripMenuItem(text) { AccessibleName = accessibleName };
        // Shown, not registered: a registered shortcut would answer wherever the focus was, and Ctrl+A in
        // the reading pane means the text there.
        if (shortcutText is not null) { item.ShortcutKeyDisplayString = shortcutText; item.ShowShortcutKeys = true; }
        item.Click += async (_, _) => await action();
        return item;
    }
    private static ClassicSectionHeader SectionLabel(string text) => new() { Text = text, Dock = DockStyle.Fill, Margin = Padding.Empty, Font = new Font("Tahoma", 8.25f) };
    private static ClassicBand BandLabel(string text, ContentAlignment alignment) => new() { Text = text, Dock = DockStyle.Fill, Margin = Padding.Empty, Font = new Font("Tahoma", 9f, FontStyle.Bold | FontStyle.Italic), TextAlign = alignment, ShowEnvelope = alignment == ContentAlignment.MiddleLeft };
    private static Label ValueLabel() => new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Tahoma", 8.25f), Margin = new Padding(0, 0, 3, 0) };
    private static Label HeaderCaption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Tahoma", 8.25f, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 1, 3, 0)
    };

    /// <summary>OE's leading: about a fifth more space between lines than the control's default, applied to
    /// whatever text the body currently holds.</summary>
    private void ApplyBodyLeading() => ApplyReadingLeading(_body);

    /// <summary>The reading pane's line spacing, applied to any message body — the pane and a message opened
    /// in its own window have to read identically.</summary>
    internal static void ApplyReadingLeading(RichTextBox body)
    {
        const int EM_SETPARAFORMAT = 0x0447;
        const uint WM_SETREDRAW = 0x000B;
        var format = new PARAFORMAT2
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<PARAFORMAT2>(),
            dwMask = 0x00000100, // PFM_LINESPACING
            bLineSpacingRule = 5, // spacing in dyLineSpacing / 20 lines
            dyLineSpacing = 24    // 1.2 lines
        };
        SendMessage(body.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try
        {
            var start = body.SelectionStart;
            body.SelectAll();
            SendMessage(body.Handle, EM_SETPARAFORMAT, IntPtr.Zero, ref format);
            body.Select(start, 0);
        }
        finally
        {
            SendMessage(body.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
            body.Invalidate();
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PARAFORMAT2
    {
        public int cbSize;
        public uint dwMask;
        public short wNumbering;
        public short wReserved;
        public int dxStartIndent;
        public int dxRightIndent;
        public int dxOffset;
        public short wAlignment;
        public short cTabCount;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 32)]
        public int[] rgxTabs;
        public int dySpaceBefore;
        public int dySpaceAfter;
        public int dyLineSpacing;
        public short sStyle;
        public byte bLineSpacingRule;
        public byte bOutlineLevel;
        public short wShadingWeight;
        public short wShadingStyle;
        public short wNumberingStart;
        public short wNumberingStyle;
        public short wNumberingTab;
        public short wBorderSpace;
        public short wBorderWidth;
        public short wBorders;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, ref PARAFORMAT2 lParam);

    // An empty preview shows nothing at all: bare "From:" captions with no message imply a message that failed to load.
    private void SetPreviewLabels(bool visible) { foreach (var label in _previewLabels) label.Visible = visible; }

    private static ImageList NavigationImages()
    {
        var images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
        images.Images.Add("folder", SmallImage(SmallGlyph.Folder));
        images.Images.Add("folder-open", SmallImage(SmallGlyph.FolderOpen));
        images.Images.Add("mail", SmallImage(SmallGlyph.Mail));
        images.Images.Add("account", SmallImage(SmallGlyph.Account));
        images.Images.Add("outbox", SmallImage(SmallGlyph.Outbox));
        images.Images.Add("sent", SmallImage(SmallGlyph.Sent));
        images.Images.Add("deleted", SmallImage(SmallGlyph.Deleted));
        images.Images.Add("drafts", SmallImage(SmallGlyph.Drafts));
        images.Images.Add("junk", SmallImage(SmallGlyph.Junk));
        return images;
    }

    /// <summary>Where the two message icons sit in the list built below, since a virtual row must ask by
    /// position rather than by name.</summary>
    private const int ReadIcon = 0;
    private const int UnreadIcon = 1;

    private static ImageList MessageImages()
    {
        // Row height in a ListView follows its image list, so the extra three pixels here are the breathing
        // room OE's message rows have.
        var images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 19) };
        images.Images.Add("read", Taller(SmallImage(SmallGlyph.Read)));
        images.Images.Add("unread", Taller(SmallImage(SmallGlyph.Unread)));
        // Then each again with the arrow OE puts on a message answered or passed on, in the order Row indexes
        // them: replied, forwarded, both — each read and unread.
        foreach (var mark in new[] { 1, 2, 3 })
            foreach (var glyph in new[] { SmallGlyph.Read, SmallGlyph.Unread })
            {
                var oe = OeIcons.At16(mark switch { 1 => 76, 2 => 78, _ => 80 } + (glyph == SmallGlyph.Read ? 1 : 0));
                if (oe is not null)
                {
                    // Keep OE's envelope and badge placement; the user wants the reply arrow blue.
                    if ((mark & 1) != 0)
                        for (var y = 8; y < 16; y++)
                            for (var x = 5; x < 16; x++)
                            {
                                var color = oe.GetPixel(x, y);
                                if (color.A > 0 && color.GetHue() < 40 && color.GetSaturation() > 0.45f)
                                    oe.SetPixel(x, y, Color.FromArgb(color.A, 0, 70, 200));
                            }
                    images.Images.Add(Taller(oe));
                }
                else images.Images.Add(Marked(Taller(SmallImage(glyph)), mark));
            }
        return images;
    }

    /// <summary>The envelope with a small blue arrow at its foot: pointing back for replied, pointing on
    /// for forwarded, both when it has been both.</summary>
    private static Bitmap Marked(Bitmap envelope, int mark)
    {
        using var graphics = Graphics.FromImage(envelope);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        if ((mark & 1) != 0)
        {
            using var reply = new SolidBrush(Color.FromArgb(0, 70, 200));
            graphics.FillPolygon(reply, [new Point(0, 15), new Point(4, 11), new Point(4, 19)]);
            graphics.FillRectangle(reply, 4, 14, 4, 3);
        }
        if ((mark & 2) != 0)
        {
            using var forward = new SolidBrush(Color.FromArgb(0, 70, 200));
            graphics.FillPolygon(forward, [new Point(16, 15), new Point(12, 11), new Point(12, 19)]);
            graphics.FillRectangle(forward, 8, 14, 4, 3);
        }
        return envelope;
    }

    private static Bitmap Taller(Bitmap glyph)
    {
        var image = new Bitmap(16, 19);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(Color.Transparent);
        graphics.DrawImageUnscaled(glyph, 0, 1);
        glyph.Dispose();
        return image;
    }

    /// <summary>The folder and message icons, from the other client's sheet where it is installed. Its Inbox,
    /// its Sent Items, its bin: a mailbox that came from there should still look like itself.</summary>
    private static Bitmap SmallImage(SmallGlyph glyph) => (OeIcons.At16(glyph switch
    {
        SmallGlyph.Folder => 111,
        SmallGlyph.FolderOpen => 122,
        SmallGlyph.Mail => 114,        // the inbox: an envelope arriving
        SmallGlyph.Account => 94,
        SmallGlyph.Read => 75,         // an opened envelope
        SmallGlyph.Unread => 74,       // a sealed one
        SmallGlyph.Outbox => 78,
        SmallGlyph.Sent => 79,
        SmallGlyph.Deleted => 117,     // the bin
        SmallGlyph.Drafts => 118,      // an envelope with a pencil
        SmallGlyph.Junk => 107,
        _ => -1
    })) ?? DrawnSmallImage(glyph);

    private static Bitmap DrawnSmallImage(SmallGlyph glyph)
    {
        var image = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(image);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        graphics.Clear(Color.Transparent);
        using var outline = new Pen(Color.FromArgb(100, 100, 90));
        using var blue = new SolidBrush(Color.FromArgb(78, 139, 210));
        using var yellow = new SolidBrush(Color.FromArgb(250, 205, 80));
        using var pale = new SolidBrush(Color.FromArgb(255, 238, 175));
        using var white = new SolidBrush(Color.White);
        switch (glyph)
        {
            case SmallGlyph.Folder:
            case SmallGlyph.FolderOpen:
                graphics.FillRectangle(yellow, 1, 5, 14, 9);
                graphics.FillRectangle(pale, 2, glyph == SmallGlyph.FolderOpen ? 7 : 3, 7, 4);
                graphics.DrawRectangle(outline, 1, 5, 14, 9);
                break;
            case SmallGlyph.Mail:
            case SmallGlyph.Read:
            case SmallGlyph.Unread:
                graphics.FillRectangle(glyph == SmallGlyph.Read ? white : pale, 1, 3, 14, 10);
                graphics.DrawRectangle(outline, 1, 3, 14, 10);
                graphics.DrawLine(outline, 2, 4, 8, 9);
                graphics.DrawLine(outline, 14, 4, 8, 9);
                break;
            case SmallGlyph.Account:
                graphics.FillEllipse(blue, 5, 1, 6, 6);
                graphics.FillRectangle(blue, 3, 8, 10, 7);
                break;
            // The standard folders read at a glance by shape, the way a mail client's have always done:
            // an envelope leaving, an envelope sent, a bin, a page being written, a warning.
            case SmallGlyph.Outbox:
                graphics.FillRectangle(pale, 1, 6, 14, 8);
                graphics.DrawRectangle(outline, 1, 6, 14, 8);
                graphics.FillPolygon(blue, [new Point(8, 0), new Point(12, 5), new Point(4, 5)]);
                break;
            case SmallGlyph.Sent:
                graphics.FillRectangle(white, 1, 5, 14, 9);
                graphics.DrawRectangle(outline, 1, 5, 14, 9);
                using (var tick = new Pen(Color.FromArgb(35, 135, 75), 2))
                {
                    graphics.DrawLine(tick, 3, 9, 6, 12);
                    graphics.DrawLine(tick, 6, 12, 13, 4);
                }
                break;
            case SmallGlyph.Deleted:
                graphics.FillRectangle(new SolidBrush(Color.FromArgb(190, 195, 200)), 4, 4, 8, 11);
                graphics.DrawRectangle(outline, 4, 4, 8, 11);
                graphics.FillRectangle(new SolidBrush(Color.FromArgb(120, 125, 130)), 3, 2, 10, 2);
                graphics.DrawLine(outline, 7, 6, 7, 13);
                graphics.DrawLine(outline, 9, 6, 9, 13);
                break;
            case SmallGlyph.Drafts:
                graphics.FillRectangle(white, 2, 1, 10, 13);
                graphics.DrawRectangle(outline, 2, 1, 10, 13);
                graphics.DrawLine(outline, 4, 4, 10, 4);
                graphics.DrawLine(outline, 4, 7, 10, 7);
                using (var pencil = new Pen(Color.FromArgb(230, 150, 40), 2))
                    graphics.DrawLine(pencil, 9, 14, 15, 8);
                break;
            case SmallGlyph.Junk:
                graphics.FillPolygon(new SolidBrush(Color.FromArgb(225, 170, 60)),
                    [new Point(8, 1), new Point(15, 14), new Point(1, 14)]);
                graphics.DrawLine(new Pen(Color.FromArgb(80, 60, 20), 2), 8, 6, 8, 10);
                graphics.FillRectangle(new SolidBrush(Color.FromArgb(80, 60, 20)), 7, 11, 2, 2);
                break;
        }
        return image;
    }

    private enum SmallGlyph { Folder, FolderOpen, Mail, Account, Read, Unread, Outbox, Sent, Deleted, Drafts, Junk }
    private enum AccountSort { Alphabetical, Volume, New }

    /// <summary>Where each command sits in the other client's icon sheet, so the toolbar carries the icons the
    /// user already knows rather than an approximation of them. Falls back to the drawn ones — see OeIcons.</summary>
    private static Bitmap ToolbarImage(ToolbarGlyph glyph) => (OeIcons.At24(glyph switch
    {
        ToolbarGlyph.Mail => 0,        // an envelope being written
        ToolbarGlyph.Reply => 1,
        ToolbarGlyph.ReplyAll => 2,
        ToolbarGlyph.Forward => 3,
        ToolbarGlyph.Delete => 4,
        ToolbarGlyph.Sync => 5,        // the globe and envelope OE uses for send and receive
        ToolbarGlyph.Print => 6,
        ToolbarGlyph.Find => 7,
        ToolbarGlyph.Move => 8,
        ToolbarGlyph.Clock => 10,      // the refresh arrow
        ToolbarGlyph.Log => 45,
        ToolbarGlyph.Html => 68,       // a page in a browser window
        ToolbarGlyph.Account => 22,
        _ => -1
    })) ?? DrawnToolbarImage(glyph);

    private static Bitmap DrawnToolbarImage(ToolbarGlyph glyph)
    {
        var image = new Bitmap(24, 24);
        using var graphics = Graphics.FromImage(image);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.None;
        graphics.Clear(Color.Transparent);
        using var dark = new Pen(Color.FromArgb(40, 55, 70), 1);
        using var blue = new Pen(Color.FromArgb(32, 93, 170), 2);
        using var green = new Pen(Color.FromArgb(35, 135, 75), 2);
        using var red = new Pen(Color.FromArgb(190, 45, 45), 2);
        using var paper = new SolidBrush(Color.White);
        using var blueFill = new SolidBrush(Color.FromArgb(86, 146, 214));
        using var goldFill = new SolidBrush(Color.FromArgb(248, 198, 74));
        using var grayFill = new SolidBrush(Color.FromArgb(190, 195, 200));

        switch (glyph)
        {
            case ToolbarGlyph.Mail:
                graphics.FillRectangle(paper, 3, 6, 18, 13);
                graphics.DrawRectangle(blue, 3, 6, 18, 13);
                graphics.DrawLine(blue, 4, 7, 12, 14);
                graphics.DrawLine(blue, 20, 7, 12, 14);
                break;
            case ToolbarGlyph.Reply:
            case ToolbarGlyph.ReplyAll:
            case ToolbarGlyph.Forward:
                graphics.FillRectangle(paper, 3, 9, 18, 11);
                graphics.DrawRectangle(dark, 3, 9, 18, 11);
                graphics.DrawLine(dark, 4, 10, 12, 16);
                graphics.DrawLine(dark, 20, 10, 12, 16);
                if (glyph is ToolbarGlyph.Reply or ToolbarGlyph.ReplyAll)
                {
                    graphics.DrawLine(green, 16, 5, 7, 5);
                    graphics.DrawLine(green, 7, 5, 11, 1);
                    graphics.DrawLine(green, 7, 5, 11, 9);
                    // Reply All is the same arrow twice: one recipient, then everyone.
                    if (glyph == ToolbarGlyph.ReplyAll)
                    {
                        graphics.DrawLine(green, 16, 9, 11, 9);
                        graphics.DrawLine(green, 11, 9, 14, 6);
                        graphics.DrawLine(green, 11, 9, 14, 12);
                    }
                }
                else
                {
                    graphics.DrawLine(blue, 8, 5, 17, 5);
                    graphics.DrawLine(blue, 17, 5, 13, 1);
                    graphics.DrawLine(blue, 17, 5, 13, 9);
                }
                break;
            case ToolbarGlyph.Print:
                // A printer: paper going in at the top, the machine, and the page coming out.
                graphics.FillRectangle(paper, 6, 2, 12, 6);
                graphics.DrawRectangle(dark, 6, 2, 12, 6);
                graphics.FillRectangle(blueFill, 3, 8, 18, 8);
                graphics.DrawRectangle(dark, 3, 8, 18, 8);
                graphics.FillRectangle(paper, 6, 14, 12, 7);
                graphics.DrawRectangle(dark, 6, 14, 12, 7);
                graphics.DrawLine(dark, 8, 17, 16, 17);
                graphics.DrawLine(dark, 8, 19, 16, 19);
                break;
            case ToolbarGlyph.Move:
                // A folder with mail arriving in it.
                graphics.FillRectangle(new SolidBrush(Color.FromArgb(250, 205, 80)), 2, 8, 14, 12);
                graphics.DrawRectangle(dark, 2, 8, 14, 12);
                graphics.FillRectangle(new SolidBrush(Color.FromArgb(255, 238, 175)), 3, 5, 7, 3);
                graphics.DrawLine(green, 22, 4, 13, 4);
                graphics.DrawLine(green, 13, 4, 17, 1);
                graphics.DrawLine(green, 13, 4, 17, 7);
                break;
            case ToolbarGlyph.Sync:
                graphics.DrawLine(blue, 7, 3, 7, 18);
                graphics.DrawLine(blue, 7, 18, 3, 13);
                graphics.DrawLine(blue, 7, 18, 11, 13);
                graphics.DrawLine(green, 17, 21, 17, 6);
                graphics.DrawLine(green, 17, 6, 13, 11);
                graphics.DrawLine(green, 17, 6, 21, 11);
                break;
            case ToolbarGlyph.Account:
                graphics.FillEllipse(goldFill, 4, 2, 8, 8);
                graphics.DrawEllipse(dark, 4, 2, 8, 8);
                graphics.FillRectangle(blueFill, 3, 12, 11, 9);
                graphics.DrawRectangle(dark, 3, 12, 11, 9);
                graphics.DrawLine(green, 18, 12, 18, 22);
                graphics.DrawLine(green, 13, 17, 23, 17);
                break;
            case ToolbarGlyph.Log:
                graphics.FillRectangle(paper, 5, 2, 14, 20);
                graphics.DrawRectangle(dark, 5, 2, 14, 20);
                graphics.DrawLine(blue, 8, 7, 16, 7);
                graphics.DrawLine(blue, 8, 11, 16, 11);
                graphics.DrawLine(blue, 8, 15, 16, 15);
                break;
            case ToolbarGlyph.Clock:
                graphics.FillEllipse(goldFill, 3, 3, 18, 18);
                graphics.DrawEllipse(dark, 3, 3, 18, 18);
                graphics.DrawLine(dark, 12, 6, 12, 13);
                graphics.DrawLine(dark, 12, 13, 17, 16);
                break;
            case ToolbarGlyph.Html:
                graphics.FillRectangle(paper, 3, 3, 18, 18);
                graphics.DrawRectangle(blue, 3, 3, 18, 18);
                graphics.DrawLine(red, 10, 8, 6, 12);
                graphics.DrawLine(red, 6, 12, 10, 16);
                graphics.DrawLine(red, 14, 8, 18, 12);
                graphics.DrawLine(red, 18, 12, 14, 16);
                break;
            case ToolbarGlyph.Delete:
                graphics.FillRectangle(grayFill, 6, 7, 12, 14);
                graphics.DrawRectangle(dark, 6, 7, 12, 14);
                graphics.DrawLine(dark, 4, 6, 20, 6);
                graphics.DrawLine(dark, 9, 3, 15, 3);
                graphics.DrawLine(red, 9, 10, 15, 18);
                graphics.DrawLine(red, 15, 10, 9, 18);
                break;
            case ToolbarGlyph.Find:
                graphics.FillEllipse(paper, 3, 3, 13, 13);
                graphics.DrawEllipse(blue, 3, 3, 13, 13);
                graphics.DrawLine(dark, 14, 14, 21, 21);
                break;
        }
        return image;
    }

    private enum ToolbarGlyph { Mail, Reply, ReplyAll, Forward, Sync, Account, Log, Clock, Html, Delete, Find, Print, Move }

    /// <summary>The toolbar icons, so a message opened in its own window carries the same ones.</summary>
    internal enum Glyph { Reply, ReplyAll, Forward, Print, Html }

    internal static Image ToolbarGlyphImage(Glyph glyph) => ToolbarImage(glyph switch
    {
        Glyph.Reply => ToolbarGlyph.Reply,
        Glyph.ReplyAll => ToolbarGlyph.ReplyAll,
        Glyph.Forward => ToolbarGlyph.Forward,
        Glyph.Print => ToolbarGlyph.Print,
        _ => ToolbarGlyph.Html
    });

    private static void ApplyNativeFont(Control control, byte quality = 0)
    {
        var nativeFont = IntPtr.Zero;
        control.HandleCreated += (_, _) =>
        {
            var description = new NativeLogFont();
            control.Font.ToLogFont(description);
            description.Quality = quality;
            nativeFont = CreateFontIndirect(description);
            SendMessage(control.Handle, 0x0030, nativeFont, new IntPtr(1));
        };
        control.HandleDestroyed += (_, _) =>
        {
            if (nativeFont != IntPtr.Zero) DeleteObject(nativeFont);
            nativeFont = IntPtr.Zero;
        };
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr word, IntPtr value);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontIndirect([In] NativeLogFont description);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);

    [StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private sealed class NativeLogFont
    {
        public int Height, Width, Escapement, Orientation, Weight;
        public byte Italic, Underline, StrikeOut, CharSet, OutPrecision, ClipPrecision, Quality, PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FaceName = "";
    }
}

/// <summary>The palette, pixel-picked from OE Classic running on this machine rather than remembered.
/// One place to nudge when a colour reads wrong.</summary>
internal static class ClassicTheme
{
    public static readonly Color BandTop = Color.FromArgb(97, 128, 156);
    public static readonly Color BandBottom = Color.FromArgb(39, 61, 86);
    public static readonly Color BandEdge = Color.FromArgb(0, 28, 55);
    public static readonly Color HeaderGrey = Color.FromArgb(233, 233, 233);
    public static readonly Color CountBlue = Color.FromArgb(0, 0, 255);
    public static readonly Color SeparatorGrey = Color.FromArgb(210, 210, 210);
    /// <summary>The reading pane's subject and the names in its From and To lines.</summary>
    public static readonly Color HeaderNavy = Color.FromArgb(0, 0, 136);
    /// <summary>The bracketed address after a name in the reading pane.</summary>
    public static readonly Color AddressGrey = Color.FromArgb(85, 85, 85);
}

/// <summary>A From or To line as OE paints it: each name in navy, its address after it in grey brackets.
/// Text keeps the "Name &lt;address&gt;" form the rest of the client uses; only the drawing changes.</summary>
internal sealed class AddressLabel : Label
{
    private static readonly System.Text.RegularExpressions.Regex Address = new(@"\s*<([^>]*)>", System.Text.RegularExpressions.RegexOptions.Compiled);

    public AddressLabel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var backColor = BackColor == Color.Transparent ? (Parent?.BackColor ?? SystemColors.Control) : BackColor;
        using (var brush = new SolidBrush(backColor))
        {
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }

        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        var x = 0;
        var last = 0;
        foreach (System.Text.RegularExpressions.Match match in Address.Matches(Text))
        {
            x += Draw(Text[last..match.Index], ClassicTheme.HeaderNavy, x);
            x += Draw($" ({match.Groups[1].Value})", ClassicTheme.AddressGrey, x);
            last = match.Index + match.Length;
        }
        Draw(Text[last..], ClassicTheme.HeaderNavy, x);

        int Draw(string part, Color colour, int at)
        {
            if (part.Length == 0) return 0;
            var width = TextRenderer.MeasureText(e.Graphics, part, Font, new Size(int.MaxValue, Height), flags).Width;
            TextRenderer.DrawText(e.Graphics, part, Font, new Rectangle(at, 0, Math.Max(0, Width - at), Height), colour, flags);
            return width;
        }
    }
}

internal sealed class ClassicBand : Control
{
    [System.ComponentModel.DefaultValue(ContentAlignment.MiddleLeft)]
    public ContentAlignment TextAlign { get; set; } = ContentAlignment.MiddleLeft;
    [System.ComponentModel.DefaultValue(false)]
    public bool ShowEnvelope { get; set; }

    public ClassicBand()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var background = new System.Drawing.Drawing2D.LinearGradientBrush(ClientRectangle,
            ClassicTheme.BandTop, ClassicTheme.BandBottom, 90f);
        e.Graphics.FillRectangle(background, ClientRectangle);
        e.Graphics.DrawLine(Pens.White, 0, 0, Width, 0);
        using var lowerEdge = new Pen(ClassicTheme.BandEdge);
        e.Graphics.DrawLine(lowerEdge, 0, Height - 1, Width, Height - 1);

        var textBounds = new Rectangle(ShowEnvelope ? 29 : 8, 0, Width - (ShowEnvelope ? 37 : 16), Height);
        if (ShowEnvelope)
        {
            using var fill = new SolidBrush(Color.FromArgb(235, 248, 235));
            using var outline = new Pen(Color.FromArgb(30, 105, 55));
            e.Graphics.FillRectangle(fill, 9, 9, 14, 10);
            e.Graphics.DrawRectangle(outline, 9, 9, 14, 10);
            e.Graphics.DrawLine(outline, 10, 10, 16, 15);
            e.Graphics.DrawLine(outline, 22, 10, 16, 15);
        }

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        flags |= TextAlign == ContentAlignment.MiddleRight ? TextFormatFlags.Right : TextFormatFlags.Left;
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, Color.White, flags);
    }
}

internal sealed class ClassicSectionHeader : Control
{
    public ClassicSectionHeader()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var background = new System.Drawing.Drawing2D.LinearGradientBrush(ClientRectangle,
            Color.White, Color.FromArgb(210, 210, 210), 90f);
        e.Graphics.FillRectangle(background, ClientRectangle);
        e.Graphics.DrawLine(SystemPens.ControlDark, 0, Height - 1, Width, Height - 1);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(7, 0, Width - 14, Height), SystemColors.ControlText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
