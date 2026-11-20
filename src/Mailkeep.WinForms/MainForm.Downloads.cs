using Mailkeep.Core;

namespace Mailkeep.WinForms;

internal sealed partial class MainForm
{
    private DownloadProgressForm? _downloadProgress;
    private CancellationTokenSource? _receiveCancellation;

    private DownloadProgressForm? BeginDownloads(IReadOnlyList<MailAccount> accounts)
    {
        if (accounts.Count == 0 || IsDisposed || Disposing) return null;
        _downloadProgress?.Dispose();
        _receiveCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _receiveCancellation = cancellation;
        var dialog = new DownloadProgressForm(accounts);
        dialog.StopRequested += () => cancellation.Cancel();
        _downloadProgress = dialog;
        dialog.ShowCentered(this);
        return dialog;
    }

    private IProgress<ReceiveProgress>? DownloadReporter(DownloadProgressForm? dialog)
        => dialog is null ? null : new Progress<ReceiveProgress>(tick =>
        {
            if (!IsDisposed && ReferenceEquals(dialog, _downloadProgress))
            {
                dialog.Report(tick);
                // One stalled provider must not hide mail already committed by another.
                if (tick.Result is { NewCount: > 0 } && ShowingInbox)
                {
                    _receiveArrivalsPending = true;
                    LoadMessages();
                }
            }
        });

    private void EndDownloads()
    {
        _receiveCancellation?.Dispose();
        _receiveCancellation = null;
    }

    private void ShowDownloadProgress()
    {
        if (_downloadProgress is not { IsDisposed: false } dialog)
        {
            _status.Text = "No download progress to show. Use Send/Receive to check Local Mail.";
            return;
        }
        if (!dialog.Visible) dialog.ShowCentered(this);
    }
}
