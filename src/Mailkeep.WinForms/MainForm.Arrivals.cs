namespace Mailkeep.WinForms;

internal sealed partial class MainForm
{
    private long _lastMailInput = Environment.TickCount64;
    private long _mailInputVersion;
    private long? _manualReceiveInputVersion;
    private bool _receiveArrivalsPending;

    private bool ShowingInbox => _selectedAccount?.IsConnected == true
        || (_selectedArchive is { } imported ? imported.LocalKey == Mailkeep.Core.LocalFolderKinds.Inbox
            : _selectedLocalFolder == Mailkeep.Core.LocalFolderKinds.Inbox);

    private void TrackMailActivity()
    {
        var filter = new MailInputFilter(this);
        Application.AddMessageFilter(filter);
        Disposed += (_, _) => Application.RemoveMessageFilter(filter);
    }

    private bool PreserveInboxPosition()
        // Explicit receive asks to reveal arrivals. Earlier pane input must not veto
        // that request, but new work during the receive still preserves position.
        => (_manualReceiveInputVersion != _mailInputVersion
                && (Environment.TickCount64 - _lastMailInput < 5_000
                    || (ActiveForm == this && (_body.ContainsFocus || _html.ContainsFocus))))
            || _messages.SelectedIndices.Count > 1 || _composes.Count > 0
            || Application.OpenForms.Cast<Form>().Any(form => form.Modal);

    private sealed class MailInputFilter(MainForm owner) : IMessageFilter
    {
        public bool PreFilterMessage(ref Message message)
        {
            // Real keyboard, click, wheel and scrollbar input, including native browser children.
            // Mouse movement alone must not keep an otherwise idle Inbox pinned forever.
            if (message.Msg is 0x100 or 0x104 && message.WParam == (IntPtr)Keys.M && Control.ModifierKeys == Keys.Control) return false;
            var target = message.HWnd;
            if (message.Msg is 0x100 or 0x104 or 0x201 or 0x204 or 0x207 or 0x20A or 0x20E or 0x114 or 0x115
                && new Control[] { owner._messages, owner._body, owner._html, owner._localTree, owner._accounts }
                    .Any(control => control.IsHandleCreated && (target == control.Handle || IsChild(control.Handle, target))))
            {
                owner._lastMailInput = Environment.TickCount64;
                owner._mailInputVersion++;
            }
            return false;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr parent, IntPtr child);
    }
}
