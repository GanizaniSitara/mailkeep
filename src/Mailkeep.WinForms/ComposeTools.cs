using Mailkeep.Core;

namespace Mailkeep.WinForms;

internal sealed partial class ComposeForm
{
    private void TabAfterTo()
    {
        if (SuggestingFor(_to)) AcceptSuggestion();
        FocusAfterTo();
    }

    private void FocusAfterTo()
    {
        HideSuggestions();
        if (OptionsForm.ComposeSettings.TabToCc(_addressBook)) _cc.Focus();
        else _subject.Focus();
    }

    private void FocusBody()
    {
        if (_plain.Visible) _plain.Focus();
        else _editor.FocusBody();
    }

    private void FindBodyText() => MessageTextFind.Show(this, _plain, _plain.Visible ? null : _editor.CoreWebView2);

    private void InsertSignature()
    {
        var signature = OptionsForm.ComposeSettings.Signature(_addressBook);
        if (signature.Length == 0)
        {
            MessageBox.Show(this, "Set your signature under Tools > Options > Compose in the main window.", "Signature");
            return;
        }
        if (_plain.Visible) { _plain.SelectedText = Environment.NewLine + signature; _plain.Focus(); }
        else { _editor.FocusBody(); _editor.Command("insertText", "\n" + signature); }
    }

    private bool _checkingNames;

    private async void CheckNames()
    {
        if (_addressBook is null || _checkingNames) return;
        _checkingNames = true;
        try
        {
            var contacts = await ContactsAsync();
            if (IsDisposed || Disposing) return;
            HideSuggestions();
            var resolved = new Dictionary<TextBox, string>();
            foreach (var field in new[] { _to, _cc, _bcc })
            {
                var addresses = new List<string>();
                foreach (var token in Recipients.Split(field.Text))
                {
                    if (MimeKit.MailboxAddress.TryParse(token, out var mailbox) && mailbox.Address.Contains('@'))
                    {
                        addresses.Add(token);
                        continue;
                    }
                    var choices = NameMatches(contacts, token);
                    if (choices.Count == 1) addresses.Add(Recipients.Format(choices[0]));
                    else
                    {
                        using var picker = new ResolveNameForm(token, choices);
                        if (picker.ShowDialog(this) != DialogResult.OK || picker.Chosen is not { } chosen)
                        {
                            field.Focus();
                            return; // No partially rewritten address lines on Cancel.
                        }
                        addresses.Add(Recipients.Format(chosen));
                    }
                }
                resolved[field] = string.Join("; ", addresses);
            }
            _accepting = true;
            try { foreach (var (field, text) in resolved) field.Text = text; }
            finally { _accepting = false; }
        }
        finally { _checkingNames = false; }
    }

    internal static IReadOnlyList<Contact> NameMatches(IReadOnlyList<Contact> contacts, string token)
    {
        var name = token.Trim().Trim('"');
        var exact = contacts.Where(contact => contact.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            || contact.Email.Equals(name, StringComparison.OrdinalIgnoreCase)).DistinctBy(contact => contact.Email, StringComparer.OrdinalIgnoreCase).ToArray();
        return exact.Length > 0 ? exact : contacts.Where(contact => contact.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)
            || contact.Email.StartsWith(name, StringComparison.OrdinalIgnoreCase)).DistinctBy(contact => contact.Email, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task CheckSpellingAsync()
    {
        try
        {
            if (_plain.Visible)
            {
                using var spelling = new SpellingForm(_plain.Text);
                if (spelling.ShowDialog(this) == DialogResult.OK) _plain.Text = spelling.CorrectedText;
            }
            else
            {
                var snapshot = await _editor.GetTextAsync();
                using var spelling = new SpellingForm(snapshot);
                if (spelling.ShowDialog(this) == DialogResult.OK)
                    await _editor.ApplyCorrectionsAsync(snapshot, spelling.Corrections);
            }
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Spelling", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}

internal sealed class ResolveNameForm : Form
{
    private readonly ListBox _choices = new() { Dock = DockStyle.Fill, AccessibleName = "Matching contacts" };
    internal Contact? Chosen => (_choices.SelectedItem as Choice)?.Contact;
    private sealed record Choice(Contact Contact) { public override string ToString() => Recipients.Format(Contact); }

    public ResolveNameForm(string token, IReadOnlyList<Contact> choices)
    {
        Text = "Check Names";
        ClientSize = new Size(450, 260);
        StartPosition = FormStartPosition.CenterParent;
        var note = new Label { Text = choices.Count == 0 ? $"No contact matches '{token}'. Correct the address and check again."
            : $"More than one contact matches '{token}'. Choose the intended recipient.", Dock = DockStyle.Top, Height = 48, Padding = new Padding(8) };
        _choices.Items.AddRange(choices.Select(contact => (object)new Choice(contact)).ToArray());
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "OK", Enabled = choices.Count > 0 };
        ok.Click += (_, _) => { if (Chosen is not null) DialogResult = DialogResult.OK; };
        buttons.Controls.AddRange([cancel, ok]);
        Controls.Add(_choices); Controls.Add(note); Controls.Add(buttons);
        AcceptButton = ok; CancelButton = cancel;
    }
}
