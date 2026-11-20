namespace Mailkeep.WinForms;

internal sealed partial class MainForm
{
    private bool _openWithoutActivation;
    private bool _maximizeOnActivation;

    protected override bool ShowWithoutActivation => _openWithoutActivation || base.ShowWithoutActivation;

    protected override void OnActivated(EventArgs e)
    {
        if (_openWithoutActivation)
        {
            _openWithoutActivation = false;
            if (_maximizeOnActivation) WindowState = FormWindowState.Maximized;
        }
        base.OnActivated(e);
        _ = RefreshMessageMarksAsync();
    }

    // The pane order crosses the sidebar split, so container TabIndex cannot express it.
    // The HTML reader passes its Tab command to MainShortcut too.
    private void FocusNextPane(bool forward)
    {
        Control body = _html.Visible && _html.IsReady && !_html.Failed ? _html : _body;
        Control[] panes = [_localTree, _messages, body, _accounts];
        var current = Array.FindIndex(panes, pane => pane.ContainsFocus);
        for (var step = 1; step <= panes.Length; step++)
        {
            var index = current < 0 ? (forward ? step - 1 : panes.Length - step)
                : (current + (forward ? step : -step) + panes.Length) % panes.Length;
            if (!panes[index].CanFocus) continue;
            panes[index].Focus();
            // WebView2 immediately transfers focus to its native child window.
            if (panes[index].ContainsFocus) return;
        }
    }

    private void InitializePaneNavigation()
    {
        _html.RoutePaneKeys = true;
    }
}
