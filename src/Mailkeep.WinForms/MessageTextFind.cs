using Microsoft.Web.WebView2.Core;

namespace Mailkeep.WinForms;

internal static class MessageTextFind
{
    public static void Show(Form owner, TextBoxBase plain, CoreWebView2? html)
    {
        var existing = owner.OwnedForms.OfType<FindTextForm>().FirstOrDefault();
        if (existing is not null) { existing.Activate(); return; }
        new FindTextForm(plain, html).Show(owner);
    }
}

internal sealed class FindTextForm : Form
{
    private readonly TextBox _query = new() { Left = 12, Top = 28, Width = 290, AccessibleName = "Find text" };
    private readonly CheckBox _case = new() { Left = 12, Top = 60, Text = "Match case", AutoSize = true };
    private readonly Label _status = new() { Left = 12, Top = 90, Width = 290, AccessibleName = "Find result" };
    private readonly TextBoxBase _body;
    private readonly CoreWebView2? _html;

    public FindTextForm(TextBoxBase body, CoreWebView2? html = null)
    {
        _body = body;
        _html = html;
        Text = "Find Text";
        ClientSize = new Size(410, 122);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        var next = new Button { Left = 316, Top = 25, Width = 82, Text = "Find Next", AccessibleName = "Find Next" };
        next.Click += async (_, _) => await FindNextAsync();
        var close = new Button { Left = 316, Top = 60, Width = 82, Text = "Close", DialogResult = DialogResult.Cancel };
        close.Click += (_, _) => Close();
        Controls.AddRange([new Label { Text = "Find what:", Left = 12, Top = 8, AutoSize = true }, _query, _case, _status, next, close]);
        AcceptButton = next; CancelButton = close;
    }

    internal void FindNext()
    {
        var term = _query.Text;
        if (term.Length == 0) return;
        var start = _body.SelectionStart + _body.SelectionLength;
        var comparison = _case.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var index = _body.Text.IndexOf(term, Math.Min(start, _body.Text.Length), comparison);
        if (index < 0) index = _body.Text.IndexOf(term, comparison);
        _status.Text = index < 0 ? "Text not found" : "";
        if (index < 0) return;
        _body.Select(index, term.Length);
        if (_body is RichTextBox rich) rich.ScrollToCaret();
        else if (_body is TextBox box) box.ScrollToCaret();
    }

    internal async Task FindNextAsync()
    {
        if (_query.Text.Length == 0) return;
        if (_html is not { } core) { FindNext(); return; }
        try
        {
            // Host execution works with mail-page scripts disabled and with older installed runtimes.
            var term = System.Text.Json.JsonSerializer.Serialize(_query.Text);
            var result = await core.ExecuteScriptAsync($"window.find({term},{(_case.Checked ? "true" : "false")},false,true)");
            _status.Text = result == "true" ? "" : "Text not found";
        }
        catch (Exception error) { _status.Text = "Cannot search: " + error.Message; }
    }
}
