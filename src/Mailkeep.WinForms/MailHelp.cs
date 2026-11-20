using Mailkeep.Core;

namespace Mailkeep.WinForms;

internal static class MailHelp
{
    public static void Show(Form owner)
    {
        var existing = owner.OwnedForms.FirstOrDefault(form => form.Text == "Mailkeep Help");
        if (existing is not null) { existing.Activate(); return; }
        var form = new Form { Text = "Mailkeep Help", ClientSize = new Size(650, 520), StartPosition = FormStartPosition.CenterParent };
        var tabs = new TabControl { Dock = DockStyle.Fill };
        Add("Reading mail", "Select a folder, then a message. Enter or Ctrl+O opens it in its own window.\r\n\r\nCtrl+R replies; Ctrl+Shift+R replies to all; Ctrl+F forwards.\r\nF4 (also F3) finds text within the displayed message. Ctrl+Shift+F searches messages across mail folders.\r\nAlt+Enter shows message properties and original headers. Ctrl+P prints.\r\nCtrl+period / Ctrl+comma move to the next / previous message. Ctrl+U moves to the next unread message.\r\nCtrl+Q marks read; Ctrl+Shift+U marks unread.\r\nCtrl+Y chooses a folder; Ctrl+I goes to Inbox. Ctrl+Shift+V moves selected mail. Delete moves mail to Deleted Items. Shift+Delete permanently deletes after confirmation, bypassing Deleted Items.");
        Add("Writing mail", "Ctrl+N starts a message. Enter complete recipient addresses or use address suggestions. Ctrl+K (also Alt+K) checks names against contacts and address history; ambiguous names require your choice.\r\n\r\nTab from To goes to Subject by default, then Tab from Subject goes to the message body. Choose Cc instead under Tools > Options > Compose. Shift+Tab moves backwards.\r\nCtrl+S saves a draft. Alt+S or Ctrl+Enter uses Send, following the Send immediately setting in Options.\r\nCtrl+Shift+S inserts the signature saved in Options > Compose at the cursor.\r\nF7 checks spelling using installed Windows dictionaries. Review changes, then Finish; Cancel preserves the original body.\r\nF4 (also F3) finds text in the body. Escape closes compose using the existing unsaved-message prompt.");
        Add("Accounts and safety", "Tools > Options controls sending and compose preferences. Sending immediately sends through the selected identity; otherwise Send queues the message in Outbox.\r\n\r\nCtrl+M or F5 sends and receives through configured accounts.\r\nRemote images stay blocked until you choose to load them.\r\nFeatures such as newsgroups and delivery receipts have no shortcut mapping because Mailkeep does not provide them.");
        form.Controls.Add(tabs);
        form.Show(owner);
        void Add(string title, string text)
        {
            var page = new TabPage(title);
            page.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Text = text, BorderStyle = BorderStyle.None, BackColor = Color.White, Font = new Font("Segoe UI", 10), AccessibleName = title + " help" });
            tabs.TabPages.Add(page);
        }
    }
}

internal sealed class MessagePropertiesForm : Form
{
    public MessagePropertiesForm(MessageContent content)
    {
        Text = "Message Properties";
        ClientSize = new Size(680, 470);
        StartPosition = FormStartPosition.CenterParent;
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var summary = content.Summary;
        Add("General", $"Subject: {summary.Subject}\r\nFrom: {summary.SenderName} <{summary.Sender}>\r\nTo: {string.Join("; ", summary.Recipients)}\r\nDate: {summary.Date?.ToLocalTime():f}\r\nSize: {summary.Size:N0} bytes\r\nAccount: {summary.Account}\r\nFolder: {summary.Folder}\r\nSource: {summary.Source}");
        Add("Headers", Headers(content.RawPath));
        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Dock = DockStyle.Bottom, Height = 32 };
        close.Click += (_, _) => Close();
        Controls.Add(tabs); Controls.Add(close); CancelButton = close;
        void Add(string title, string text)
        {
            var page = new TabPage(title);
            page.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both,
                WordWrap = false, Text = text, AccessibleName = "Message " + title.ToLowerInvariant(), Font = new Font("Consolas", 9) });
            tabs.TabPages.Add(page);
        }
    }

    internal static string Headers(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Original headers unavailable: no raw message is stored.";
        try
        {
            using var reader = new StreamReader(path);
            var lines = new List<string>();
            while (reader.ReadLine() is { Length: > 0 } line) lines.Add(line);
            return string.Join(Environment.NewLine, lines);
        }
        catch (IOException error) { return "Original headers unavailable: " + error.Message; }
        catch (UnauthorizedAccessException error) { return "Original headers unavailable: " + error.Message; }
    }
}
