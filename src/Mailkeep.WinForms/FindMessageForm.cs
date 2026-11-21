using Mailkeep.Core;
using Mailkeep.Infrastructure;
using System.Text.Json;
using System.Diagnostics;

namespace Mailkeep.WinForms;

/// <summary>OE Classic's Find Message window, faithfully: criteria at the top — Look in with subfolders,
/// From, To, Subject, Message, received dates and an attachment switch — Find Now / Stop / New Search on the
/// right, and the results growing into the same window. Modeless, so mail can be read while it stays open.
/// </summary>
internal sealed class FindMessageForm : Form
{
    private readonly ComboBox _lookIn = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Look in" };
    private readonly CheckBox _subfolders = new() { Text = "Include subfolders", Checked = true, AutoSize = true, AccessibleName = "Include subfolders" };
    private readonly TextBox _from = new() { Dock = DockStyle.Fill, AccessibleName = "From criteria" };
    private readonly TextBox _to = new() { Dock = DockStyle.Fill, AccessibleName = "To criteria" };
    private readonly TextBox _subject = new() { Dock = DockStyle.Fill, AccessibleName = "Subject criteria" };
    private readonly TextBox _message = new() { Dock = DockStyle.Fill, AccessibleName = "Message criteria" };
    private readonly CheckBox _beforeOn = new() { Text = "Received before:", AutoSize = true, AccessibleName = "Received before switch" };
    private readonly CheckBox _afterOn = new() { Text = "Received after:", AutoSize = true, AccessibleName = "Received after switch" };
    private readonly DateTimePicker _before = new() { Format = DateTimePickerFormat.Short, Enabled = false, Width = 110, AccessibleName = "Received before date" };
    private readonly DateTimePicker _after = new() { Format = DateTimePickerFormat.Short, Enabled = false, Width = 110, AccessibleName = "Received after date" };
    private readonly CheckBox _attachments = new() { Text = "Message has attachment(s)", AutoSize = true, AccessibleName = "Has attachments" };
    private readonly CheckBox _matchMessage = new() { Text = "Message", Checked = true, AutoSize = true, AccessibleName = "Search message text" };
    private readonly CheckBox _matchNames = new() { Text = "Attachment filenames", Checked = true, AutoSize = true, AccessibleName = "Search attachment filenames" };
    private readonly CheckBox _matchContents = new() { Text = "Attachment contents", Checked = true, AutoSize = true, AccessibleName = "Search attachment contents" };
    private readonly CheckBox _includeArchives = new() { Text = "Search archives too", AutoSize = true, Visible = false, AccessibleName = "Search archives too" };
    private readonly Button _findNow = new() { Text = "Find Now", Width = 88, AccessibleName = "Find Now" };
    private readonly Button _stop = new() { Text = "Stop", Width = 88, Enabled = false, AccessibleName = "Stop find" };
    private readonly Button _newSearch = new() { Text = "New Search", Width = 88, AccessibleName = "New Search" };
    private readonly BufferedListView _results = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        ShowItemToolTips = true,
        Font = new Font("Tahoma", 8.25f),
        AccessibleName = "Find results",
        Visible = false
    };
    private readonly Font _unreadFont;
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 20, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0), AccessibleName = "Find status" };

    private readonly Func<string, string?, SearchFilters, CancellationToken, SearchEnvelope> _search;
    /// <summary>The archive, searched only when asked ("Search archives too"); archived mail is otherwise out of
    /// sight. Until one is given, the option is hidden.</summary>
    private Func<string, string?, ArchiveStore.SearchResult>? _archiveSearch;

    public void SetArchiveSearch(Func<string, ArchiveStore.SearchResult> search)
        => SetArchiveSearch((query, _) => search(query));

    public void SetArchiveSearch(Func<string, string?, ArchiveStore.SearchResult> search)
    {
        _archiveSearch = search;
        _includeArchives.Visible = true;
    }
    private CancellationTokenSource? _searchCancellation;
    private readonly Action<MessageSummary> _open;
    internal Action<MessageSummary, IReadOnlyList<long>>? OpenResults;
    private readonly Action<long> _openAttachment;
    private Dictionary<string, string> _folderNames;

    /// <summary>The hits as found, kept so that re-sorting costs nothing but the sort.</summary>
    private IReadOnlyList<MessageSummary> _hits = [];
    private int _sortColumn = ReceivedColumn;
    private bool _descending = true;
    private readonly MailDatabase? _layoutDatabase;
    private bool _columnsReady;
    private bool _columnsCustomized;
    private readonly System.Windows.Forms.Timer _columnSave = new() { Interval = 300 };
    private sealed record ColumnLayout(int[] Widths, int[] Order);
    private const string ColumnsSetting = "find.columns";

    private const int AttachmentColumn = 0;
    private const int FromColumn = 1;
    private const int SubjectColumn = 2;
    private const int ReceivedColumn = 3;
    private const int FolderColumn = 4;

    public FindMessageForm(IReadOnlyList<FindFolder> folders,
        Func<string, string?, SearchFilters, CancellationToken, SearchEnvelope> search, Action<MessageSummary> open,
        Action<long> openAttachment, MailDatabase? layoutDatabase = null)
    {
        _search = search;
        _open = open;
        _openAttachment = openAttachment;
        _layoutDatabase = layoutDatabase;
        _folderNames = folders.Where(folder => folder.LocalKeys is not null && folder.Key is not null)
            .ToDictionary(folder => folder.Key!, folder => folder.Name, StringComparer.OrdinalIgnoreCase);
        Text = "Find Message";
        AccessibleName = Text;
        ClientSize = new Size(660, 330);
        MinimumSize = new Size(600, 368);
        _unreadFont = new Font(_results.Font, FontStyle.Bold);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Tahoma", 8.25f);
        Icon = Dialog.WindowIcon();
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

        _lookIn.DisplayMember = nameof(FindFolder.Name);
        SetFolders(folders);

        var criteria = new BufferedTableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true, Padding = new Padding(8, 8, 8, 0) };
        criteria.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        criteria.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        criteria.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));

        var lookInRow = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty, AutoSize = true };
        lookInRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        lookInRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        lookInRow.Controls.Add(_lookIn, 0, 0);
        _subfolders.Margin = new Padding(8, 4, 0, 0);
        lookInRow.Controls.Add(_subfolders, 1, 0);

        AddRow(criteria, 0, "Look in:", lookInRow, _findNow);
        AddRow(criteria, 1, "From:", _from, _stop);
        AddRow(criteria, 2, "To:", _to, _newSearch);
        AddRow(criteria, 3, "Subject:", _subject, null);
        AddRow(criteria, 4, "Message:", _message, null);

        var extras = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(66, 2, 8, 6), AccessibleName = "Find filters" };
        extras.Controls.Add(_afterOn);
        extras.Controls.Add(_after);
        extras.Controls.Add(_beforeOn);
        extras.Controls.Add(_before);
        extras.Controls.Add(_attachments);
        extras.Controls.Add(_matchMessage);
        extras.Controls.Add(_matchNames);
        extras.Controls.Add(_matchContents);
        extras.Controls.Add(_includeArchives);
        _includeArchives.Margin = new Padding(14, 4, 0, 0);
        _beforeOn.Margin = new Padding(14, 4, 4, 0);
        _afterOn.Margin = new Padding(0, 4, 4, 0);
        _attachments.Margin = new Padding(14, 4, 0, 0);
        _matchMessage.Margin = new Padding(14, 4, 0, 0);
        _matchNames.Margin = new Padding(8, 4, 0, 0);
        _matchContents.Margin = new Padding(8, 4, 0, 0);

        // OE's results lead with a paperclip column (the flag column it also has is deliberately skipped).
        _results.SmallImageList = ResultImages();
        _results.Columns.Add(new ColumnHeader { Text = "", Width = 36, ImageKey = "clip" });
        _results.Columns.Add("From", 160);
        _results.Columns.Add("Subject", 240);
        _results.Columns.Add("Received", 128);
        _results.Columns.Add("Folder", 140);
        _results.AllowColumnReorder = true;
        _results.Resize += (_, _) => SizeResultColumns();
        _results.ColumnWidthChanged += (_, _) =>
        {
            if (!_columnsReady) return;
            _columnsCustomized = true;
            _columnSave.Stop();
            _columnSave.Start();
        };
        _results.ColumnReordered += (_, _) => { if (_columnsReady) { _columnSave.Stop(); _columnSave.Start(); } };
        _columnSave.Tick += (_, _) => { _columnSave.Stop(); SaveColumns(); };
        Load += (_, _) =>
        {
            try
            {
                var saved = JsonSerializer.Deserialize<ColumnLayout>(_layoutDatabase?.GetSetting(ColumnsSetting) ?? "null");
                if (saved is { Widths.Length: 5, Order.Length: 5 }
                    && saved.Widths.All(width => width >= 16 && width <= 4000)
                    && saved.Order.Order().SequenceEqual(Enumerable.Range(0, 5)))
                {
                    _columnsCustomized = true;
                    for (var i = 0; i < 5; i++)
                    {
                        _results.Columns[i].Width = (int)Math.Round(saved.Widths[i] * DeviceDpi / 96d);
                        _results.Columns[i].DisplayIndex = saved.Order[i];
                    }
                }
            }
            catch (JsonException) { }
            SizeResultColumns();
            _columnsReady = true;
        };
        // Clicking a heading sorts by it, and clicking the same one again turns the order round. A column
        // sorted for the first time picks the direction that column is usually read in: newest mail first,
        // names from A.
        _results.ColumnClick += (_, e) =>
        {
            if (e.Column == _sortColumn) _descending = !_descending;
            else { _sortColumn = e.Column; _descending = e.Column is ReceivedColumn or AttachmentColumn; }
            ShowHits();
        };

        Controls.Add(_results);
        Controls.Add(_status);
        Controls.Add(extras);
        Controls.Add(criteria);
        Controls.Add(BuildMenu());
        _results.BringToFront();

        // Wrapped filters need their full preferred height at the narrowest supported width.
        void FitCriteria()
        {
            var filtersHeight = extras.GetPreferredSize(new Size(ClientSize.Width, 0)).Height;
            var required = criteria.Height + filtersHeight + MainMenuStrip!.Height + _status.Height + 24;
            var minimum = SizeFromClientSize(new Size(584, required));
            if (MinimumSize != minimum) MinimumSize = minimum;
        }
        Shown += (_, _) => FitCriteria();
        ClientSizeChanged += (_, _) => FitCriteria();

        AcceptButton = _findNow;
        _afterOn.CheckedChanged += (_, _) => _after.Enabled = _afterOn.Checked;
        _beforeOn.CheckedChanged += (_, _) => _before.Enabled = _beforeOn.Checked;
        _findNow.Click += async (_, _) => await FindNowAsync();
        _stop.Click += (_, _) =>
        {
            _searchCancellation?.Cancel();
            _stop.Enabled = false;
            _status.Text = "Stopping search…";
        };
        _newSearch.Click += (_, _) => Reset();
        _results.DoubleClick += (_, _) => OpenSelected();
        _results.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            OpenSelected();
        };
        Shown += (_, _) => _from.Focus();
        VisibleChanged += (_, _) => { if (Visible) _from.Focus(); };
        // Closed by the reader, it is put away rather than thrown away, so the next Find is there at once with
        // what was being looked for still in it, as OE's is. Closed with the main window, it goes.
        FormClosing += (_, e) =>
        {
            SaveColumns();
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            _searchCancellation?.Cancel();
            Hide();
        };
        FormClosed += (_, _) => _searchCancellation?.Cancel();
        Disposed += (_, _) => _columnSave.Dispose();
    }

    private MenuStrip BuildMenu()
    {
        var open = new ToolStripMenuItem("&Open", null, (_, _) => OpenSelected()) { ShortcutKeys = Keys.Control | Keys.O };
        var openAttachment = new ToolStripMenuItem("Open Matching &Attachment", null, (_, _) => OpenSelectedAttachment())
        {
            ShortcutKeys = Keys.Control | Keys.Shift | Keys.O
        };
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(open);
        file.DropDownItems.Add(openAttachment);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("&Close", null, (_, _) => Close()));
        var edit = new ToolStripMenuItem("&Edit");
        edit.DropDownItems.Add(new ToolStripMenuItem("Find &Now", null, async (_, _) => await FindNowAsync()) { ShortcutKeys = Keys.Control | Keys.Enter });
        edit.DropDownItems.Add(new ToolStripMenuItem("New &Search", null, (_, _) => Reset()));
        var menu = new MenuStrip { AccessibleName = "Find menu" };
        menu.Items.Add(file);
        menu.Items.Add(edit);
        MainMenuStrip = menu;
        return menu;
    }

    private void OpenSelected()
    {
        if (_results.SelectedItems.Count > 0 && _results.SelectedItems[0].Tag is MessageSummary hit)
        {
            if (OpenResults is { } openResults)
                openResults(hit, _results.Items.Cast<ListViewItem>().Select(item => ((MessageSummary)item.Tag!).Id).ToArray());
            else _open(hit);
        }
    }

    private void OpenSelectedAttachment()
    {
        if (_results.SelectedItems.Count > 0
            && _results.SelectedItems[0].Tag is MessageSummary { AttachmentMatch: { } attachment })
            _openAttachment(attachment.OccurrenceId);
    }

    private void SizeResultColumns()
    {
        if (_columnsCustomized) return;
        // Subject takes whatever the fixed columns leave behind, so the list never shows dead space.
        // Budgeting from the control width (not the client) keeps the sum stable whether or not the
        // vertical scrollbar has appeared yet, so no horizontal one ever does.
        if (_results.Columns.Count < 5) return;
        var taken = _results.Columns[0].Width + _results.Columns[1].Width + _results.Columns[3].Width + _results.Columns[4].Width;
        var ready = _columnsReady;
        _columnsReady = false;
        try { _results.Columns[2].Width = Math.Max(160, _results.Width - taken - SystemInformation.VerticalScrollBarWidth - 8); }
        finally { _columnsReady = ready; }
    }

    private void SaveColumns()
    {
        if (!_columnsReady || _layoutDatabase is null) return;
        _columnsReady = false;
        try
        {
            _results.Columns[0].Width = Math.Max(_results.Columns[0].Width, (int)Math.Ceiling(32 * DeviceDpi / 96d));
            _ = SettingsWrites.Save(_layoutDatabase, ColumnsSetting, JsonSerializer.Serialize(new ColumnLayout(
                _results.Columns.Cast<ColumnHeader>().Select(column => (int)Math.Round(column.Width * 96d / DeviceDpi)).ToArray(),
                _results.Columns.Cast<ColumnHeader>().Select(column => column.DisplayIndex).ToArray())));
        }
        finally { _columnsReady = true; }
    }

    private static ImageList ResultImages()
    {
        var images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 19) };
        images.Images.Add("blank", new Bitmap(16, 19));
        var clip = new Bitmap(16, 19);
        using (var graphics = Graphics.FromImage(clip))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var wire = new Pen(Color.FromArgb(95, 105, 120), 1.6f);
            graphics.DrawArc(wire, 5f, 3f, 6f, 6f, 180f, 180f);
            graphics.DrawLine(wire, 5f, 6f, 5f, 13f);
            graphics.DrawArc(wire, 5f, 10f, 6f, 6f, 0f, 180f);
            graphics.DrawLine(wire, 11f, 6f, 11f, 13f);
            graphics.DrawArc(wire, 7f, 10f, 4f, 4f, 0f, 180f);
            graphics.DrawLine(wire, 7f, 12f, 7f, 7f);
        }
        images.Images.Add("clip", clip);
        return images;
    }

    private static void AddRow(TableLayoutPanel layout, int row, string label, Control value, Control? button)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        layout.Controls.Add(value, 1, row);
        if (button is not null)
        {
            button.Margin = new Padding(8, 1, 0, 1);
            layout.Controls.Add(button, 2, row);
        }
    }

    private async Task FindNowAsync()
    {
        if (_searchCancellation is not null) return;
        var from = _from.Text.Trim();
        var to = _to.Text.Trim();
        var subject = _subject.Text.Trim();
        var body = _message.Text.Trim();
        if (SearchCatalog.SenderFilterTooShort(from))
        {
            _status.Text = "Enter at least 3 characters in From.";
            return;
        }
        if (from.Length + to.Length + subject.Length + body.Length == 0
            && _lookIn.SelectedIndex <= 0 && !_afterOn.Checked && !_beforeOn.Checked && !_attachments.Checked)
        {
            _status.Text = "Enter text or choose a folder, date, or attachment filter.";
            return;
        }
        var matchScope = (_matchMessage.Checked ? SearchMatchScope.Message : 0)
            | (_matchNames.Checked ? SearchMatchScope.AttachmentName : 0)
            | (_matchContents.Checked ? SearchMatchScope.AttachmentContent : 0);
        if (matchScope == 0)
        {
            _status.Text = "Choose Message, Attachment filenames, or Attachment contents.";
            return;
        }
        var chosenFolder = _lookIn.SelectedItem as FindFolder;
        var folder = chosenFolder?.LocalKeys is null ? chosenFolder?.Key : null;
        var filters = new SearchFilters(
            _subfolders.Checked,
            _afterOn.Checked ? new DateTimeOffset(_after.Value.Date) : null,
            _beforeOn.Checked ? new DateTimeOffset(_before.Value.Date.AddDays(1)) : null,
            _attachments.Checked, matchScope, from, to, subject,
            chosenFolder?.LocalKeys is { } keys ? (_subfolders.Checked ? keys : [chosenFolder.Key!]) : null);

        using var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        _findNow.Enabled = false;
        _newSearch.Enabled = false;
        _stop.Enabled = true;
        _status.Text = "Searching…";
        var clock = Stopwatch.StartNew();
        try
        {
            var query = body.Length > 0 ? $"body:\"{body.Replace("\"", "")}\"" : "";
            var envelope = await Task.Run(() => _search(query, folder, filters, cancellation.Token), cancellation.Token);
            TimingLog.MarkBackground($"find catalog elapsedMs={clock.ElapsedMilliseconds} rows={envelope.Results.Count}");
            cancellation.Token.ThrowIfCancellationRequested();
            if (IsDisposed) return;
            var archivesStarted = clock.ElapsedMilliseconds;
            var archived = _includeArchives.Checked && _archiveSearch is { } searchArchives
                ? await Task.Run(() => ArchiveHits(searchArchives, from, to, subject, body, filters), cancellation.Token)
                : null;
            TimingLog.MarkBackground($"find attached archives elapsedMs={clock.ElapsedMilliseconds - archivesStarted} enabled={archived is not null}");
            if (IsDisposed) return;
            _hits = archived is null ? envelope.Results : [.. envelope.Results, .. archived.Hits];
            var renderStarted = clock.ElapsedMilliseconds;
            ShowHits();
            if (!_results.Visible)
            {
                // OE's dialog grows when results arrive; before that it is just the criteria.
                _results.Visible = true;
                Height += 220;
            }
            SizeResultColumns();
            _status.Text = (envelope.Count == 1 ? "1 message found"
                : $"{envelope.Count:N0} messages found" + (envelope.Truncated ? $", showing {envelope.Results.Count:N0}" : ""))
                + (archived is null ? ""
                    : $"; {archived.Hits.Count:N0} in archives"
                      + (archived.NotSearched.Count > 0 ? " (not searched: " + string.Join(", ", archived.NotSearched) + ")" : ""));
            TimingLog.MarkBackground($"find render elapsedMs={clock.ElapsedMilliseconds - renderStarted} totalMs={clock.ElapsedMilliseconds} rows={_hits.Count}");
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        {
            if (!IsDisposed) _status.Text = "Search stopped. Previous results retained.";
        }
        catch (Exception error)
        {
            if (!IsDisposed) _status.Text = "Search failed: " + error.Message;
        }
        finally
        {
            _searchCancellation = null;
            if (!IsDisposed)
            {
                _findNow.Enabled = true;
                _newSearch.Enabled = true;
                _stop.Enabled = false;
            }
        }
    }

    /// <summary>Draws the hits in the order asked for. Newest first to begin with: the index hands them back
    /// in order of how well each matches, which is an order nobody reading mail thinks in.</summary>
    private void ShowHits()
    {
        var ordered = _sortColumn switch
        {
            AttachmentColumn => _hits.OrderBy(hit => hit.AttachmentCount).ThenByDescending(hit => hit.Date),
            FromColumn => _hits.OrderBy(hit => hit.DisplaySender, StringComparer.CurrentCultureIgnoreCase),
            SubjectColumn => _hits.OrderBy(hit => hit.Subject, StringComparer.CurrentCultureIgnoreCase),
            FolderColumn => _hits.OrderBy(hit => DisplayFolder(hit.Folder), StringComparer.CurrentCultureIgnoreCase),
            _ => _hits.OrderBy(hit => hit.Date)
        };
        var rows = (_descending ? ordered.Reverse() : ordered).ToArray();

        _results.BeginUpdate();
        _results.Items.Clear();
        foreach (var hit in rows)
        {
            var item = new ListViewItem("") { Tag = hit, ImageKey = hit.AttachmentCount > 0 ? "clip" : "blank" };
            item.SubItems.Add(hit.DisplaySender);
            item.SubItems.Add(hit.AttachmentMatch is { } attachment ? $"{hit.Subject} — {AttachmentSafety.DisplayName(attachment.FileName)}" : hit.Subject);
            item.ToolTipText = hit.Snippet;
            item.SubItems.Add(hit.Date?.ToLocalTime().ToString("g") ?? "");
            item.SubItems.Add(IsArchived(hit) ? $"Archive: {hit.Account} / {hit.Folder}" : DisplayFolder(hit.Folder));
            if (!hit.IsRead) item.Font = _unreadFont;
            if (IsArchived(hit)) item.ForeColor = SystemColors.GrayText;
            _results.Items.Add(item);
        }
        _results.EndUpdate();
        SortArrow.Show(_results, _sortColumn, _descending);
    }

    /// <summary>Escape puts the window away, as it does a dialog — unless a drop-down is open, when it closes that.</summary>
    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (keys == Keys.Escape && !_lookIn.DroppedDown)
        {
            Close();
            return true;
        }
        return base.ProcessCmdKey(ref message, keys);
    }

    /// <summary>The folders offered under Look in, refreshed whenever the folder tree changes. The choice made
    /// stays chosen if that folder is still there.</summary>
    public void SetFolders(IReadOnlyList<FindFolder> folders)
    {
        var chosen = (_lookIn.SelectedItem as FindFolder)?.Name;
        _folderNames = folders.Where(folder => folder.LocalKeys is not null && folder.Key is not null)
            .ToDictionary(folder => folder.Key!, folder => folder.Name, StringComparer.OrdinalIgnoreCase);
        _lookIn.BeginUpdate();
        _lookIn.Items.Clear();
        _lookIn.Items.Add(new FindFolder("All mail", null));
        foreach (var folder in folders) _lookIn.Items.Add(folder);
        var again = chosen is null ? -1 : _lookIn.Items.Cast<FindFolder>().ToList().FindIndex(folder => folder.Name == chosen);
        _lookIn.SelectedIndex = Math.Max(0, again);
        _lookIn.EndUpdate();
    }

    private string DisplayFolder(string key) => _folderNames.GetValueOrDefault(key, key);

    private static bool IsArchived(MessageSummary hit) => hit.Source == ArchiveStore.Source;

    /// <summary>The same criteria put to the archive: the text fields as column searches, then the date and
    /// attachment filters applied to what comes back. Folder choice does not apply; the archive has its own.</summary>
    private static ArchiveStore.SearchResult ArchiveHits(Func<string, string?, ArchiveStore.SearchResult> search,
        string from, string to, string subject, string body, SearchFilters filters)
    {
        static string Column(string column, string value) => value.Length == 0 ? "" : $"{column}:\"{value.Replace("\"", "")}\" ";
        var query = (Column("recipient", to) + Column("subject", subject) + Column("body", body)).Trim();
        if (query.Length == 0 && from.Length == 0) return new([], ["archives (enter From, To, Subject or Message text to search them)"]);
        var result = search(query, from);
        var hits = result.Hits.Where(hit =>
            (filters.After is not { } after || hit.Date >= after)
            && (filters.Before is not { } before || hit.Date < before)
            && (!filters.RequireAttachment || hit.AttachmentCount > 0)).ToArray();
        return result with { Hits = hits };
    }

    private void Reset()
    {
        if (_searchCancellation is not null) return;
        _from.Clear(); _to.Clear(); _subject.Clear(); _message.Clear();
        _lookIn.SelectedIndex = 0;
        _afterOn.Checked = false; _beforeOn.Checked = false; _attachments.Checked = false;
        _matchMessage.Checked = true; _matchNames.Checked = true; _matchContents.Checked = true;
        _results.Items.Clear();
        _hits = [];
        _status.Text = "";
        _from.Focus();
    }
}
