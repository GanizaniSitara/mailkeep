using Mailkeep.Infrastructure;

namespace Mailkeep.WinForms;

/// <summary>Copies the pictures out of the mail into a folder of the reader's choosing: from all mail or one folder,
/// leaving out the logos and signatures written into message bodies unless asked for. Runs in the background with
/// its progress shown, and can be stopped; what was copied by then stays copied.</summary>
internal sealed class ExportPicturesForm : Form
{
    private readonly MailDatabase _database;
    private readonly ComboBox _lookIn = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Export from folder" };
    private readonly CheckBox _subfolders = new() { Text = "Include subfolders", Checked = true, AutoSize = true, AccessibleName = "Include subfolders" };
    private readonly TextBox _destination = new() { Dock = DockStyle.Fill, AccessibleName = "Export destination folder" };
    private readonly CheckBox _inline = new() { Text = "Also pictures inside message bodies (logos, signatures)", AutoSize = true, AccessibleName = "Include inline pictures" };
    private readonly CheckBox _skipSmall = new() { Text = "Skip pictures under 10 KB", Checked = true, AutoSize = true, AccessibleName = "Skip small pictures" };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, AccessibleName = "Export progress" };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, AccessibleName = "Export status", Text = "Choose where the pictures should go." };
    private readonly Button _export = new() { Text = "Export", Width = 90, AccessibleName = "Export pictures" };
    private readonly Button _close = new() { Text = "Close", Width = 90, AccessibleName = "Close", DialogResult = DialogResult.Cancel };
    private readonly Button _open = new() { Text = "Open folder", Width = 90, AccessibleName = "Open export folder", Enabled = false };
    private CancellationTokenSource? _running;
    private const string DestinationSetting = "export.pictures.destination";
    private const string DefaultDestination = @"C:\images\mail";

    public ExportPicturesForm(MailDatabase database, IReadOnlyList<FindFolder> folders)
    {
        _database = database;
        Text = "Export pictures from mail attachments";
        AccessibleName = Text;
        ClientSize = new Size(580, 250);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Tahoma", 8.25f);
        Icon = Dialog.WindowIcon();

        _lookIn.DisplayMember = nameof(FindFolder.Name);
        _lookIn.Items.Add(new FindFolder("All mail", null));
        foreach (var folder in folders) _lookIn.Items.Add(folder);
        _lookIn.SelectedIndex = 0;
        // The folder used last time, so the export can simply be run again to collect new mail's pictures.
        _destination.Text = _database.GetSetting(DestinationSetting) is { Length: > 0 } last ? last : DefaultDestination;

        var browse = new Button { Text = "Browse…", Width = 80, AccessibleName = "Browse for destination" };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Where should the pictures go?", UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(_destination.Text) ? _destination.Text : "" };
            if (dialog.ShowDialog(this) == DialogResult.OK) _destination.Text = dialog.SelectedPath;
        };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(10, 10, 10, 0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        void Row(string caption, Control control, Control? extra = null, int height = 28)
        {
            var row = layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.Controls.Add(new Label { Text = caption, AutoSize = true, Margin = new Padding(0, 6, 0, 0) }, 0, row);
            layout.Controls.Add(control, 1, row);
            if (extra is not null) layout.Controls.Add(extra, 2, row);
        }
        Row("Look in:", _lookIn, _subfolders);
        Row("Save to:", _destination, browse);
        Row("", _inline, height: 24);
        Row("", _skipSmall, height: 24);
        Row("", _progress, height: 22);
        Row("", _status, height: 34);
        layout.SetColumnSpan(_inline, 2);
        layout.SetColumnSpan(_skipSmall, 2);
        layout.SetColumnSpan(_status, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        buttons.Controls.AddRange([_close, _export, _open]);
        Controls.Add(layout);
        Controls.Add(buttons);
        AcceptButton = _export;
        CancelButton = _close;

        _export.Click += async (_, _) => await ExportAsync();
        _open.Click += (_, _) =>
        {
            if (Directory.Exists(_destination.Text))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{_destination.Text}\"") { UseShellExecute = false });
        };
        // Closing while it runs stops it; what was copied stays.
        FormClosing += (_, _) => _running?.Cancel();
    }

    private async Task ExportAsync()
    {
        if (_running is not null) { _running.Cancel(); return; }
        var destination = _destination.Text.Trim();
        if (destination.Length == 0 || !Path.IsPathFullyQualified(destination))
        {
            MessageBox.Show(this, "Choose a folder to save the pictures in.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var scope = (FindFolder)_lookIn.SelectedItem!;
        var subfolders = _subfolders.Checked;
        var request = new PictureExportRequest(destination,
            LocalFolders: scope.Key is null || scope.LocalKeys is null ? null : subfolders ? scope.LocalKeys : [scope.Key],
            ArchiveFolder: scope.LocalKeys is null ? scope.Key : null,
            IncludeSubfolders: subfolders, IncludeInline: _inline.Checked, MinimumBytes: _skipSmall.Checked ? 10 * 1024 : 1);
        try { _database.SetSetting(DestinationSetting, destination); } catch (Exception) { }
        _running = new CancellationTokenSource();
        var token = _running.Token;
        _export.Text = "Stop";
        _lookIn.Enabled = _destination.Enabled = _subfolders.Enabled = _inline.Enabled = _skipSmall.Enabled = false;
        _status.Text = "Finding the mail with pictures — over all mail this takes a few minutes…";
        _progress.Style = ProgressBarStyle.Marquee;
        var progress = new Progress<PictureExportProgress>(tick =>
        {
            _progress.Style = ProgressBarStyle.Blocks;
            _progress.Maximum = Math.Max(1, tick.Total);
            _progress.Value = Math.Min(tick.Messages, _progress.Maximum);
            _status.Text = $"{tick.Messages:N0} of {tick.Total:N0} messages — {tick.Written:N0} pictures saved";
        });
        try
        {
            var result = await Task.Run(() => _database.ExportPictures(request, progress, token), token);
            _status.Text = (result.Written == 1 ? "1 picture" : $"{result.Written:N0} pictures") + $" saved from {result.Messages:N0} message" + (result.Messages == 1 ? "" : "s")
                + (result.Duplicates > 0 ? $"; {result.Duplicates:N0} repeats left out" : "")
                + (result.Small > 0 ? $"; {result.Small:N0} small ones skipped" : "")
                + (result.Unreadable > 0 ? $"; {result.Unreadable:N0} messages could not be read" : "") + ".";
            _open.Enabled = true;
        }
        catch (OperationCanceledException) { _status.Text = "Stopped. The pictures saved so far are in the folder."; _open.Enabled = true; }
        catch (Exception error) when (!IsDisposed) { _status.Text = "Could not export: " + error.Message; }
        finally
        {
            _running = null;
            if (!IsDisposed)
            {
                _export.Text = "Export";
                _progress.Style = ProgressBarStyle.Blocks;
                _lookIn.Enabled = _destination.Enabled = _subfolders.Enabled = _inline.Enabled = _skipSmall.Enabled = true;
            }
        }
    }
}
