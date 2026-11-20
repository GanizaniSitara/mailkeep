using Mailkeep.Core;
using Mailkeep.Infrastructure;

namespace Mailkeep.WinForms;

/// <summary>Addressing from the address book: the To, Cc and Bcc captions open Select Recipients, as they do in
/// OE, and typing in an address line offers the contacts and the addresses already written to that it could
/// mean, beneath the line, to take with Enter or Tab or a click. Delete takes the highlighted address out of the
/// written-to history; a contact stays offered while it is in the address book.</summary>
internal sealed partial class ComposeForm
{
    private readonly MailDatabase? _addressBook;
    private readonly Dictionary<TextBox, Control> _captions = [];
    private readonly ListBox _suggestions = new()
    {
        Visible = false, IntegralHeight = true, TabStop = false, BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Tahoma", 8.25f), AccessibleName = "Address suggestions"
    };
    private const string HistoryOrigin = "history";
    private IReadOnlyList<Contact>? _contacts;
    private Dictionary<string, string> _lastUsed = new(StringComparer.OrdinalIgnoreCase);
    private Task<(IReadOnlyList<Contact>, Dictionary<string, string>)>? _loading;
    private Task<(IReadOnlyList<Contact>, Dictionary<string, string>)>? _observedLoading;
    private TextBox? _suggesting;
    private TextBox? _pendingSuggestionField;
    private bool _accepting;

    private sealed record Suggestion(Contact Contact)
    {
        public override string ToString() => Recipients.Format(Contact);
    }

    private void EnableAddressing()
    {
        if (_addressBook is null) return;
        Controls.Add(_suggestions);
        _suggestions.BringToFront();
        // A click takes the suggestion before the line loses focus to the list, which would close it.
        _suggestions.MouseDown += (_, e) =>
        {
            var index = _suggestions.IndexFromPoint(e.Location);
            if (index >= 0) { _suggestions.SelectedIndex = index; AcceptSuggestion(); }
        };
        foreach (var (field, caption) in _captions)
        {
            var target = field;
            caption.Click += (_, _) => PickRecipients(target);
            new ToolTip().SetToolTip(caption, "Choose recipients from the address book");
            field.TextChanged += (_, _) => { if (!_accepting) Suggest(target); };
            field.PreviewKeyDown += (_, e) => { if (e.KeyData == Keys.Tab && SuggestingFor(target)) e.IsInputKey = true; };
            field.KeyDown += (_, e) => SuggestionKey(target, e);
            field.Leave += (_, _) =>
            {
                if (_pendingSuggestionField == target) _pendingSuggestionField = null;
                BeginInvoke(() => { if (!_suggestions.Focused && _suggesting?.Focused != true) HideSuggestions(); });
            };
        }
        Move += (_, _) => HideSuggestions();
        Resize += (_, _) => HideSuggestions();
        // Read ahead: the first read of the history fills it from the sent folders, which takes a moment.
        var book = _addressBook;
        _loading = Task.Run(() => LoadSuggestions(book));
    }

    /// <summary>The contacts, then every address written to that is not one of them, with when each was last used.</summary>
    private static (IReadOnlyList<Contact>, Dictionary<string, string>) LoadSuggestions(MailDatabase book)
    {
        var lastUsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<Contact> all;
        try { all = [.. book.ListContacts()]; }
        catch (Exception) { all = []; } // no address book is no reason to stop writing
        try
        {
            var held = all.Select(contact => contact.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var use in book.ListAddressHistory())
            {
                lastUsed[use.Email] = use.LastUsed;
                if (held.Add(use.Email)) all.Add(new Contact(0, "", use.Email, Origin: HistoryOrigin));
            }
        }
        catch (Exception) { } // nor is a history that cannot be read
        return (all, lastUsed);
    }

    private IReadOnlyList<Contact> Contacts()
    {
        if (_contacts is not null || _addressBook is null) return _contacts ?? [];
        _loading ??= Task.Run(() => LoadSuggestions(_addressBook));
        if (!ReferenceEquals(_observedLoading, _loading))
        {
            _observedLoading = _loading;
            _ = CompleteContactsAsync(_loading);
        }
        return _contacts ?? [];
    }

    private async Task CompleteContactsAsync(Task<(IReadOnlyList<Contact>, Dictionary<string, string>)> loading)
    {
        var (contacts, lastUsed) = await loading;
        if (IsDisposed || Disposing || !ReferenceEquals(_loading, loading)) return;
        _loading = null;
        _lastUsed = lastUsed;
        _contacts = contacts;
        // Re-evaluate the current text, not the fragment that started this read. Never take focus.
        var field = _pendingSuggestionField;
        if (field is not null) Suggest(field);
    }

    private async Task<IReadOnlyList<Contact>> ContactsAsync()
    {
        if (_contacts is not null || _addressBook is null) return _contacts ?? [];
        _loading ??= Task.Run(() => LoadSuggestions(_addressBook));
        await CompleteContactsAsync(_loading);
        return _contacts ?? [];
    }

    /// <summary>Takes the highlighted address out of the written-to history and offers what is left.</summary>
    private async void RemoveSuggestion(TextBox field)
    {
        if (_addressBook is null || _suggestions.SelectedItem is not Suggestion chosen) return;
        try { await Task.Run(() => _addressBook.RemoveFromAddressHistory(chosen.Contact.Email)); }
        catch (Exception) { return; }
        if (IsDisposed || Disposing) return;
        _lastUsed.Remove(chosen.Contact.Email);
        if (chosen.Contact.Origin == HistoryOrigin)
            _contacts = Contacts().Where(contact => contact != chosen.Contact).ToArray();
        if (field.Focused || _suggesting == field) Suggest(field);
    }

    private bool SuggestingFor(TextBox field) => _suggestions.Visible && _suggesting == field && _suggestions.Items.Count > 0;

    private void Suggest(TextBox field)
    {
        _pendingSuggestionField = field;
        var caret = field.SelectionStart;
        var start = Recipients.TokenStart(field.Text, caret);
        var fragment = field.Text[start..caret];
        // Only a fragment at the end of what has been typed so far, not one being edited in the middle.
        var rest = field.Text[caret..];
        var restEnds = rest.Length == 0 || rest.TrimStart().StartsWith(';') || rest.TrimStart().StartsWith(',');
        var others = Recipients.Split(field.Text[..start] + ";" + rest);
        var matches = fragment.Trim().Length == 0 || !restEnds ? [] : Recipients.Suggest(Contacts(), fragment, others, lastUsed: _lastUsed);
        if (matches.Count == 0) { HideSuggestions(); return; }
        _suggesting = field;
        _suggestions.BeginUpdate();
        _suggestions.Items.Clear();
        _suggestions.Items.AddRange(matches.Select(contact => (object)new Suggestion(contact)).ToArray());
        _suggestions.SelectedIndex = 0;
        _suggestions.EndUpdate();
        var below = PointToClient(field.Parent!.PointToScreen(new Point(field.Left, field.Bottom)));
        _suggestions.SetBounds(below.X, below.Y, Math.Max(260, field.Width), _suggestions.ItemHeight * matches.Count + 4);
        _suggestions.Visible = true;
        _suggestions.BringToFront();
    }

    private void SuggestionKey(TextBox field, KeyEventArgs e)
    {
        if (!SuggestingFor(field)) return;
        if (e.KeyCode == Keys.Tab && e.KeyData != Keys.Tab) return;
        switch (e.KeyCode)
        {
            case Keys.Down: _suggestions.SelectedIndex = Math.Min(_suggestions.Items.Count - 1, _suggestions.SelectedIndex + 1); break;
            case Keys.Up: _suggestions.SelectedIndex = Math.Max(0, _suggestions.SelectedIndex - 1); break;
            case Keys.Enter: AcceptSuggestion(); break;
            case Keys.Tab: AcceptSuggestion(); if (field == _to) FocusAfterTo(); break;
            case Keys.Escape: HideSuggestions(); break;
            case Keys.Delete: RemoveSuggestion(field); break;
            default: return;
        }
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    /// <summary>Puts the chosen contact where the fragment was, followed by a separator ready for the next one.</summary>
    private void AcceptSuggestion()
    {
        if (_suggesting is not { } field || _suggestions.SelectedItem is not Suggestion chosen) return;
        var caret = field.SelectionStart;
        var start = Recipients.TokenStart(field.Text, caret);
        var inserted = Recipients.Format(chosen.Contact) + "; ";
        _accepting = true;
        try
        {
            field.Text = field.Text[..start] + inserted + field.Text[caret..].TrimStart(' ', ';', ',');
            field.SelectionStart = start + inserted.Length;
            field.SelectionLength = 0;
        }
        finally { _accepting = false; }
        HideSuggestions();
        field.Focus();
    }

    private void HideSuggestions()
    {
        _suggestions.Visible = false;
        _suggesting = null;
    }

    /// <summary>OE's Select Recipients: the address book with To, Cc and Bcc beside it, filled from the lines as
    /// they stand and written back to them on OK.</summary>
    private void PickRecipients(TextBox focus)
    {
        if (_addressBook is null) return;
        HideSuggestions();
        using var picker = new AddressBookForm(_addressBook, picking: true, _to.Text, _cc.Text, _bcc.Text);
        if (picker.ShowDialog(this) != DialogResult.OK) { _contacts = null; _loading = null; return; }
        _contacts = null; // the book may have been edited while it was open
        _loading = null;
        _accepting = true;
        try
        {
            _to.Text = string.Join("; ", picker.Chosen("To"));
            _cc.Text = string.Join("; ", picker.Chosen("Cc"));
            _bcc.Text = string.Join("; ", picker.Chosen("Bcc"));
        }
        finally { _accepting = false; }
        focus.Focus();
        focus.SelectionStart = focus.Text.Length;
    }

    private void OpenAddressBook()
    {
        if (_addressBook is null) return;
        using var book = new AddressBookForm(_addressBook);
        book.ShowDialog(this);
        _contacts = null;
        _loading = null;
    }
}
