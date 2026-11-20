using Mailkeep.Core;

namespace Mailkeep.WinForms;

/// <summary>A receive pass stays inspectable after completion; hiding it never stops collection.</summary>
internal sealed class DownloadProgressForm : Form
{
    private readonly ListView _accounts = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
        MultiSelect = false, AccessibleName = "Account download progress"
    };
    private readonly Label _summary = new() { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(8, 4, 8, 0), AutoEllipsis = true };
    private readonly Label _headline = new() { Dock = DockStyle.Fill, Text = "Receiving Local Mail", AutoEllipsis = true };
    private readonly Label _activity = new() { Dock = DockStyle.Fill, Text = "Waiting to check accounts…", AutoEllipsis = true };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, AccessibleName = "Accounts received progress" };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, AccessibleName = "Receive details" };
    private readonly ListBox _errors = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true, AccessibleName = "Receive errors" };
    private readonly Button _toggleDetails = new() { Text = "<< Details", Width = 85, AccessibleName = "Toggle receive details" };
    private int _detailsHeight = 410;
    private readonly TextBox _details = new()
    {
        Dock = DockStyle.Bottom, Height = 62, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, AccessibleName = "Download account details"
    };
    private readonly Button _close = new() { Text = "Hide", AutoSize = true, AccessibleName = "Hide download progress" };
    private readonly Button _stop = new() { Text = "Stop", Width = 85, AccessibleName = "Stop receiving mail" };
    internal event Action? StopRequested;
    private readonly Dictionary<string, ListViewItem> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _finished = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _baselined = new(StringComparer.OrdinalIgnoreCase);
    internal bool Finished { get; private set; }
    protected override bool ShowWithoutActivation => true;

    internal DownloadProgressForm(IReadOnlyList<MailAccount> accounts)
    {
        Text = "Receive Local Mail";
        Font = new Font("Tahoma", 8.25f);
        Size = new Size(760, 410); MinimumSize = new Size(600, 180);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false; MinimizeBox = false;
        _accounts.Columns.Add("Account", 220);
        _accounts.Columns.Add("Status", 125);
        _accounts.Columns.Add("Messages", 90);
        _accounts.Columns.Add("Details", 280);
        foreach (var account in accounts)
        {
            var row = new ListViewItem([account.Address, "Waiting", "—", "Waiting to check this account"]);
            row.Name = account.Name;
            _rows.Add(account.Name, row); _accounts.Items.Add(row);
        }
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 98, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(6, 0, 6, 0) };
        _close.Width = 85;
        buttons.Controls.Add(_close);
        buttons.Controls.Add(_stop);
        buttons.Controls.Add(_toggleDetails);
        var header = new Panel { Dock = DockStyle.Top, Height = 105, Padding = new Padding(10, 8, 6, 8) };
        var operation = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(0, 0, 8, 0) };
        operation.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        operation.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        operation.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        _headline.Font = new Font(Font, FontStyle.Bold);
        operation.Controls.Add(_headline, 0, 0); operation.Controls.Add(_activity, 0, 1); operation.Controls.Add(_progress, 0, 2);
        header.Controls.Add(operation); header.Controls.Add(buttons);
        var tasks = new TabPage("Tasks"); tasks.Controls.Add(_accounts);
        var errors = new TabPage("Errors"); errors.Controls.Add(_errors);
        _tabs.TabPages.AddRange([tasks, errors]);
        _close.Click += (_, _) => { if (Finished) Close(); else Hide(); };
        _stop.Click += (_, _) =>
        {
            _stop.Enabled = false; _stop.Text = "Stopping…";
            StopRequested?.Invoke();
        };
        _toggleDetails.Click += (_, _) =>
        {
            var expanded = _tabs.Visible;
            if (expanded) _detailsHeight = Height;
            _tabs.Visible = _details.Visible = !expanded;
            _toggleDetails.Text = expanded ? "Details >>" : "<< Details";
            Height = expanded ? MinimumSize.Height : _detailsHeight;
        };
        _accounts.SelectedIndexChanged += (_, _) => ShowDetails();
        _errors.SelectedIndexChanged += (_, _) => ShowDetails();
        _tabs.SelectedIndexChanged += (_, _) => ShowDetails();
        Controls.Add(_tabs); Controls.Add(_details); Controls.Add(_summary); Controls.Add(header);
        FormClosing += (_, e) =>
        {
            if (!Finished && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
        UpdateSummary();
    }

    internal void ShowCentered(Form owner)
    {
        if (Finished) Text = _failed.Count > 0 ? "Local Mail receive errors" : "Last Local Mail receive results";
        var area = Screen.FromControl(owner).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        if (!Visible) Show(owner);
    }

    internal void Report(ReceiveProgress tick)
    {
        if (IsDisposed || Finished || !_rows.TryGetValue(tick.Account, out var row) || _finished.Contains(tick.Account)) return;
        row.SubItems[1].Text = tick.Stage;
        if (tick.Stage.StartsWith("Recording existing", StringComparison.Ordinal)) _baselined.Add(tick.Account);
        if (tick.Total is { } total) row.SubItems[2].Text = $"{tick.Downloaded} of {total}";
        row.SubItems[3].Text = tick.Stage == "Downloading"
            ? $"{tick.Downloaded} message(s) safely stored" : tick.Stage;
        if (tick.Result is { } result)
        {
            _finished.Add(tick.Account);
            if (result.Ok) row.SubItems[2].Text = $"{result.NewCount} new";
            row.SubItems[3].Text = result.Error ?? (_baselined.Contains(tick.Account)
                ? "Existing messages recorded as already collected — none downloaded" : $"Complete — {result.NewCount} new message(s)");
            if (!result.Ok)
            {
                _failed.Add(tick.Account); row.ForeColor = Color.DarkRed;
                _errors.Items.Add(row.Text + ": " + result.Error);
                if (_errors.SelectedIndex < 0) _errors.SelectedIndex = 0;
                _tabs.TabPages[1].Text = $"Errors ({_failed.Count})";
                if (_accounts.SelectedItems.Count == 0) row.Selected = true;
            }
        }
        UpdateSummary(); ShowDetails();
    }

    internal void Complete(IReadOnlyList<SyncResult> results, string? error = null)
    {
        if (IsDisposed || Finished) return;
        foreach (var result in results)
            Report(new(result.Account, "", result.Ok ? "Complete" : "Failed", Result: result));
        foreach (var pair in _rows.Where(pair => !_finished.Contains(pair.Key)).ToArray())
            Report(new(pair.Key, "", "Failed", Result: new SyncResult(pair.Key, 0, 0, 0, error ?? "Account did not complete")));
        Finished = true; _stop.Enabled = false; _stop.Text = "Stop"; _close.Text = "Close"; _close.AccessibleName = "Close download progress";
        UpdateSummary();
        if (_failed.Count == 0) Hide();
        else
        {
            // Switching a visible native tab can activate an otherwise inactive form.
            // Configure it while hidden, then use the existing no-activation show path.
            if (!ContainsFocus) Hide();
            _tabs.SelectedIndex = 1;
            _tabs.Visible = _details.Visible = true;
            _toggleDetails.Text = "<< Details";
            if (Height < _detailsHeight) Height = _detailsHeight;
            if (Owner is Form owner) ShowCentered(owner);
        }
    }

    private void UpdateSummary()
    {
        _progress.Maximum = Math.Max(1, _rows.Count); _progress.Value = _finished.Count;
        _summary.Text = $"{_finished.Count} of {_rows.Count} accounts finished — {_failed.Count} failed"
            + (Finished ? ". Last receive results." : ". Closes automatically if there are no errors.");
        if (Finished)
        {
            _headline.Text = _failed.Count > 0 ? "Receive finished with errors" : "Receive complete";
            _activity.Text = _failed.Count > 0 ? "Select an error below for details." : "Results are also available in the Fetch Log.";
            return;
        }
        var active = _rows.Values.FirstOrDefault(row => !_finished.Contains(row.Name) && row.SubItems[1].Text != "Waiting");
        if (active is not null)
            _activity.Text = active.SubItems[1].Text + " — " + active.Text
                + (active.SubItems[2].Text == "—" ? "" : " (" + active.SubItems[2].Text + " messages)");
    }
    private void ShowDetails() => _details.Text = _tabs.SelectedIndex == 1
        ? _errors.SelectedItem?.ToString() ?? "No error selected."
        : _accounts.SelectedItems.Count == 0 ? "Select an account to read its details."
        : _accounts.SelectedItems[0].SubItems[0].Text + Environment.NewLine + _accounts.SelectedItems[0].SubItems[3].Text;
}
