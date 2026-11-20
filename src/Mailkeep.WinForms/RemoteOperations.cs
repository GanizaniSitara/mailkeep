namespace Mailkeep.WinForms;

internal sealed partial class MainForm
{
    private readonly ToolStripStatusLabel _remoteStatus = new() { AccessibleName = "Pending server changes" };
    private readonly System.Windows.Forms.Timer _remoteTimer = new() { Interval = 2000 };
    private readonly CancellationTokenSource _remoteStop = new();
    private bool _remoteTickRunning;
    private readonly HashSet<Task> _remoteLocalWrites = [];
    private bool _closingAfterRemoteWrites;
    private int _remoteStatusGeneration;

    private async Task RefreshRemoteStatusAsync()
    {
        var generation = ++_remoteStatusGeneration;
        try
        {
            var status = await Task.Run(_services.Database.RemoteStatus);
            if (IsDisposed || Disposing || generation != _remoteStatusGeneration) return;
            _remoteStatus.Text = status.Pending == 0 ? "" : $"Server changes: {status.Pending} pending" +
                (status.Failed > 0 ? $", {status.Failed} retrying" : "");
            _remoteStatus.ToolTipText = status.LastError ?? "Changes are saved here and sent while Mailkeep is open.";
            _remoteStatus.ForeColor = status.Failed > 0 ? Color.Firebrick : SystemColors.ControlText;
        }
        catch (Exception error)
        {
            if (!IsDisposed && !Disposing && generation == _remoteStatusGeneration)
            {
                _remoteStatus.Text = "Server changes waiting — see Fetch log";
                _remoteStatus.ToolTipText = error.Message;
            }
        }
    }

    private async Task<T> PersistRemoteChangeAsync<T>(Func<Task<T>> write)
    {
        var task = Task.Run(write);
        _remoteLocalWrites.Add(task);
        try { return await task; }
        finally { _remoteLocalWrites.Remove(task); }
    }

    private async Task CloseAfterRemoteWritesAsync()
    {
        if (_closingAfterRemoteWrites) return;
        _closingAfterRemoteWrites = true;
        try
        {
            await Task.WhenAll(_remoteLocalWrites.ToArray());
            // Let the command continuations publish their saved state before trying to close again.
            await Task.Yield();
            _closingAfterRemoteWrites = false;
            if (!IsDisposed) Close();
        }
        catch (Exception error)
        {
            _closingAfterRemoteWrites = false;
            if (!IsDisposed) _status.Text = "Could not save server changes: " + error.Message;
        }
    }

    private void StartRemoteOperations()
    {
        _remoteTimer.Tick += async (_, _) => await RemoteOperationsTickAsync();
        _remoteTimer.Start();
        _ = RemoteOperationsTickAsync();
    }

    private async Task RemoteOperationsTickAsync()
    {
        if (!_ready || IsDisposed) return;
        if (_remoteTickRunning) { await RefreshRemoteStatusAsync(); return; }
        _remoteTickRunning = true;
        try
        {
            await RefreshRemoteStatusAsync();
            if (IsDisposed || Disposing) return;
            await Task.Run(() => _services.Sync.DrainRemoteOperationsAsync(_remoteStop.Token));
            await RefreshRemoteStatusAsync();
        }
        catch (OperationCanceledException) when (_remoteStop.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!IsDisposed && !Disposing)
            {
                _remoteStatus.Text = "Server changes waiting — see Fetch log";
                _remoteStatus.ToolTipText = error.Message;
            }
        }
        finally { _remoteTickRunning = false; }
    }
}
