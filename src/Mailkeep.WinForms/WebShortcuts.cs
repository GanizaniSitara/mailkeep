using Microsoft.Web.WebView2.WinForms;

namespace Mailkeep.WinForms;

/// <summary>Routes mail commands out of the browser's native keyboard window. Defer actions until the
/// synchronous accelerator callback returns, so reading editor HTML and opening dialogs cannot block COM.</summary>
internal static class WebShortcuts
{
    public static void Attach(WebView2 view, Func<Keys, Action?> lookup)
    {
        var held = new HashSet<Keys>();
        view.KeyUp += (_, e) => held.Remove(e.KeyCode);
        view.LostFocus += (_, _) => held.Clear();
        view.KeyDown += (_, e) =>
        {
            if (lookup(e.KeyData) is not { } action) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (held.Add(e.KeyCode))
                view.BeginInvoke(() => { if (!view.IsDisposed) action(); });
        };
    }
}
