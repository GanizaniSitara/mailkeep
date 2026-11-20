using Mailkeep.Core;

namespace Mailkeep.WinForms;

internal sealed partial class MainForm
{
    private readonly System.Windows.Forms.Timer _messageMarksRefresh = new() { Interval = 5000 };
    private bool _refreshingMessageMarks;

    private void StartMessageMarksRefresh()
    {
        _messageMarksRefresh.Tick += async (_, _) =>
        {
            if (ActiveForm == this) await RefreshMessageMarksAsync();
        };
        _messageMarksRefresh.Start();
    }

    // CLI delivery cannot notify this window. Read only the rows on screen, off the UI
    // thread, and repaint their icons without replacing the folder or reading pane.
    private async Task RefreshMessageMarksAsync()
    {
        if (!_ready || _refreshingMessageMarks || IsDisposed || Disposing
            || !Visible || WindowState == FormWindowState.Minimized || _rows.Count == 0
            || !_messages.IsHandleCreated || _messages.VirtualListSize != _rows.Count) return;
        var rows = _rows;
        var first = Math.Clamp(_messages.TopItem?.Index ?? 0, 0, rows.Count - 1);
        var height = Math.Max(1, _messages.GetItemRect(first).Height);
        var count = Math.Min(rows.Count - first, _messages.ClientSize.Height / height + 2);
        var ids = Enumerable.Range(first, count).Select(index => rows[index])
            .OfType<MessageSummary>().Select(message => message.Id).ToArray();
        if (ids.Length == 0) return;
        _refreshingMessageMarks = true;
        try
        {
            var current = await Task.Run(() => _services.Database.MessageMarks(ids));
            if (IsDisposed || Disposing || !ReferenceEquals(rows, _rows)) return;
            if (!ids.Any(id => _marks.GetValueOrDefault(id) != current.GetValueOrDefault(id))) return;
            var updated = new Dictionary<long, MessageMarks>(_marks);
            foreach (var id in ids) updated[id] = current.GetValueOrDefault(id);
            _marks = updated;
            RedrawMessageList(first, first + count - 1);
        }
        catch (Exception) { } // Keep existing arrows on a busy/unavailable store; retry next time.
        finally { _refreshingMessageMarks = false; }
    }
}
