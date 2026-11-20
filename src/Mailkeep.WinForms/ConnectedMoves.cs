using Mailkeep.Core;

namespace Mailkeep.WinForms;

internal sealed partial class MainForm
{
    private bool _serverMoveRunning;
    private async Task MoveToServerFolderAsync()
    {
        if (!RequireReady() || _serverMoveRunning) return;
        var selected = Selected();
        if (selected.Length == 0) return;
        _serverMoveRunning = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var folders = await _services.Sync.ConnectedMoveFoldersAsync(selected, timeout.Token);
            if (IsDisposed) return;
            using var chooser = new Form { Text = "Move to server folder", ClientSize = new Size(440, 320),
                StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
            var list = new ListBox { Dock = DockStyle.Fill, AccessibleName = "Server folders" };
            list.Items.AddRange(folders.Cast<object>().ToArray());
            var label = new Label { Dock = DockStyle.Top, Height = 48,
                Text = $"Move {selected.Length} message(s) in {selected[0].Account}.\r\nThis changes the mailbox on all devices." };
            var move = new Button { Text = "Move", DialogResult = DialogResult.OK, Enabled = false, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.RightToLeft };
            buttons.Controls.AddRange([cancel, move]);
            chooser.Controls.AddRange([list, label, buttons]);
            chooser.AcceptButton = move;
            chooser.CancelButton = cancel;
            list.SelectedIndexChanged += (_, _) => move.Enabled = list.SelectedItem is string;
            if (chooser.ShowDialog(this) != DialogResult.OK || list.SelectedItem is not string destination) return;
            AdjustConnectedRemovalCounts(selected, -1);
            RemoveRows(selected.Select(message => message.Id));
            _status.Text = "Saving server move…";
            var count = await PersistRemoteChangeAsync(() => _services.Sync.MoveConnectedAsync(selected, destination, timeout.Token));
            if (!IsDisposed)
            {
                ++_readCountsGeneration;
                _status.Text = $"Moved {count} message(s) here — server move to {destination} queued";
                _ = RemoteOperationsTickAsync();
            }
        }
        catch (Exception error)
        {
            if (!IsDisposed)
            {
                // Folder discovery/selection can fail before any rows were removed.
                _ = RefreshConnectedCountsAsync();
                LoadMessages(); _status.Text = "Could not queue move: " + error.Message;
            }
        }
        finally
        {
            _serverMoveRunning = false;
        }
    }
}
