using Mailkeep.Core;

namespace Mailkeep.WinForms;

internal sealed partial class MainForm
{
    private string AccountCountDescription(string account)
        => $"{_accountCounts.GetValueOrDefault(account).Total:N0} messages, {_accountUnreadCounts.GetValueOrDefault(account):N0} unread";

    private void UpdateMessageCount()
    {
        var total = _selectedAccount?.IsConnected == true
            ? _accountCounts.GetValueOrDefault(_selectedAccount.Name).Total : _rows.Count;
        var unread = _selectedAccount?.IsConnected == true
            ? _accountUnreadCounts.GetValueOrDefault(_selectedAccount.Name)
            : _rows.OfType<MessageSummary>().Count(message => !message.IsRead);
        _count.Text = $"{total:N0} messages, {unread:N0} unread";
    }

    // Use the displayed state immediately. No server call, database recount, list rebuild or selection change.
    private void AdjustConnectedRemovalCounts(IReadOnlyList<MessageSummary> messages, int direction)
    {
        ++_readCountsGeneration;
        var totals = _accountCounts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var unread = _accountUnreadCounts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var group in messages.DistinctBy(message => message.Id).Where(IsConnectedMessage)
            .GroupBy(message => message.Account, StringComparer.OrdinalIgnoreCase))
        {
            var before = totals.GetValueOrDefault(group.Key);
            var total = Math.Max(0, before.Total + direction * group.Count());
            totals[group.Key] = (total, Math.Min(total, before.New));
            unread[group.Key] = Math.Clamp(unread.GetValueOrDefault(group.Key) + direction * group.Count(message => !message.IsRead), 0, total);
        }
        _accountCounts = totals;
        _accountUnreadCounts = unread;
        UpdateConnectedCountRows();
    }

    private void UpdateConnectedCountRows()
    {
        foreach (ListViewItem item in _accounts.Items)
        {
            if (item.Tag is not MailAccount account) continue;
            var total = _accountCounts.GetValueOrDefault(account.Name).Total.ToString("N0");
            if (item.SubItems[1].Text != total) item.SubItems[1].Text = total;
            item.ToolTipText = AccountCountDescription(account.Name);
        }
        _accounts.Invalidate();
    }

    private async Task RefreshConnectedCountsAsync()
    {
        var generation = ++_readCountsGeneration;
        var counts = await Task.Run(() => (_services.Database.AccountMessageCounts(), _services.Database.AccountUnreadCounts()));
        if (IsDisposed || generation != _readCountsGeneration || _remoteLocalWrites.Count > 0) return;
        _accountCounts = counts.Item1;
        _accountUnreadCounts = counts.Item2;
        UpdateConnectedCountRows();
    }
}
