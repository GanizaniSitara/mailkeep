using System.Net;
using Mailkeep.Core;
using Microsoft.Web.WebView2.Core;

namespace Mailkeep.WinForms;

internal sealed class HtmlPrintForm : Form
{
    private readonly HtmlView _html = new(MainForm.OpenLink);
    private readonly ToolStripButton _print = new("Print") { Enabled = false };

    public HtmlPrintForm(MessageContent content, string browserDataFolder)
    {
        Text = "Print — " + content.Summary.Subject;
        Size = new Size(850, 750);
        MinimumSize = new Size(450, 350);
        StartPosition = FormStartPosition.CenterParent;
        var toolbar = new ToolStrip();
        toolbar.Items.Add(_print);
        toolbar.Items.Add(new ToolStripButton("Close", null, (_, _) => Close()));
        Controls.Add(_html);
        Controls.Add(toolbar);
        _print.Click += (_, _) =>
        {
            try { _html.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.System); }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "Could not print", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        _html.DocumentLoadCompleted += (_, _) =>
        {
            _print.Enabled = _html.DocumentLoaded;
            if (!_html.DocumentLoaded) { DialogResult = DialogResult.Abort; Close(); }
        };
        Shown += async (_, _) =>
        {
            await _html.StartAsync(browserDataFolder);
            if (IsDisposed) return;
            if (_html.Failed) { DialogResult = DialogResult.Abort; Close(); return; }
            _html.Visible = true;
            _html.Show(CreateDocument(content));
        };
    }

    internal static string CreateDocument(MessageContent content)
    {
        var summary = content.Summary;
        var sender = string.IsNullOrWhiteSpace(summary.SenderName) ? summary.Sender : $"{summary.SenderName} <{summary.Sender}>";
        var when = summary.Date?.ToLocalTime().ToString("f") ?? "Date unavailable";
        var header = $"From: {sender}\nTo: {string.Join(", ", summary.Recipients)}\n{when}\nSubject: {summary.Subject}";
        return MainForm.SafeHtml("<title>" + WebUtility.HtmlEncode(summary.Subject) + "</title>" +
            "<div style=\"white-space:pre-wrap;font:12px Tahoma,sans-serif;color:#000;background:#fff;margin:0 0 16px;padding:0 0 12px;border-bottom:1px solid #888\">" +
            WebUtility.HtmlEncode(header) + "</div>" + content.BodyHtml);
    }
}
