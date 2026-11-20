using Mailkeep.Core;
using Mailkeep.Infrastructure;

namespace Mailkeep.WinForms;

/// <summary>How recipients are written in an address line: "Name &lt;address&gt;", quoted where the name would
/// otherwise break the line apart, and separated by semicolons or commas outside quotes and brackets.</summary>
internal static class Recipients
{
    /// <summary>"Name &lt;address&gt;", with the name quoted only where a character in it would otherwise be read as
    /// part of the address line's structure — a comma, a semicolon, brackets or quotes.</summary>
    public static string Format(Contact contact)
    {
        if (contact.Name.Length == 0) return contact.Email;
        var name = contact.Name.IndexOfAny([',', ';', '<', '>', '"', '@', ':', '(', ')', '[', ']', '\\']) >= 0
            ? "\"" + contact.Name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
            : contact.Name;
        return $"{name} <{contact.Email}>";
    }

    /// <summary>The address alone, whatever form it was written in.</summary>
    public static string Address(string recipient)
        => MimeKit.MailboxAddress.TryParse(recipient, out var mailbox) ? mailbox.Address : recipient.Trim();

    /// <summary>Splits a line into recipients. A separator inside quotes or angle brackets is part of a name or
    /// an address — "Smith, John" &lt;j@example.com&gt; is one recipient, not two.</summary>
    public static string[] Split(string line)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var bracketed = false;
        foreach (var character in line)
        {
            if (character == '"' && !bracketed) quoted = !quoted;
            else if (character == '<' && !quoted) bracketed = true;
            else if (character == '>' && !quoted) bracketed = false;
            if ((character is ';' or ',') && !quoted && !bracketed)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(character);
        }
        parts.Add(current.ToString());
        return parts.Select(part => part.Trim()).Where(part => part.Length > 0).ToArray();
    }

    /// <summary>Where the recipient being typed starts: just past the last separator before the caret.</summary>
    public static int TokenStart(string line, int caret)
    {
        var quoted = false;
        var bracketed = false;
        var start = 0;
        for (var index = 0; index < caret && index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"' && !bracketed) quoted = !quoted;
            else if (character == '<' && !quoted) bracketed = true;
            else if (character == '>' && !quoted) bracketed = false;
            else if ((character is ';' or ',') && !quoted && !bracketed) start = index + 1;
        }
        while (start < caret && start < line.Length && char.IsWhiteSpace(line[start])) start++;
        return start;
    }

    /// <summary>The contacts a typed fragment could mean, best first: a name or address that starts with it, then
    /// a word of the name that does, then anything containing it. Within each, the most recently written to
    /// come first, when it is known.</summary>
    public static IReadOnlyList<Contact> Suggest(IReadOnlyList<Contact> contacts, string fragment, IEnumerable<string> already,
        int limit = 8, IReadOnlyDictionary<string, string>? lastUsed = null)
    {
        fragment = fragment.Trim();
        if (fragment.Length == 0) return [];
        var taken = already.Select(Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int Rank(Contact contact)
        {
            if (contact.Name.StartsWith(fragment, StringComparison.CurrentCultureIgnoreCase)
                || contact.Email.StartsWith(fragment, StringComparison.OrdinalIgnoreCase)) return 0;
            if (contact.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(word => word.StartsWith(fragment, StringComparison.CurrentCultureIgnoreCase))) return 1;
            if (contact.Name.Contains(fragment, StringComparison.CurrentCultureIgnoreCase)
                || contact.Email.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return 2;
            return -1;
        }
        return contacts.Where(contact => !taken.Contains(contact.Email))
            .Select(contact => (Contact: contact, Rank: Rank(contact)))
            .Where(match => match.Rank >= 0)
            .OrderBy(match => match.Rank)
            .ThenByDescending(match => lastUsed?.GetValueOrDefault(match.Contact.Email) ?? "", StringComparer.Ordinal)
            .ThenBy(match => match.Contact.Name.Length == 0 ? match.Contact.Email : match.Contact.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit).Select(match => match.Contact).ToArray();
    }
}

/// <summary>The address book, as OE draws it: the contacts in a searchable list with New, Properties and Delete
/// beside them, and an import from OE Classic. Opened from Compose it is also the Select Recipients window: the
/// chosen contacts go into To, Cc or Bcc, and come back to the message on OK.</summary>
internal sealed class AddressBookForm : Form
{
    private readonly MailDatabase _database;
    private readonly TextBox _search = new() { Dock = DockStyle.Top, AccessibleName = "Search contacts", PlaceholderText = "Type a name or address to find it" };
    private readonly BufferedListView _contacts = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true,
        AccessibleName = "Contacts", Font = new Font("Tahoma", 8.25f)
    };
    private readonly Dictionary<string, ListBox> _chosen = new(StringComparer.Ordinal);
    private IReadOnlyList<Contact> _all = [];
    private readonly bool _picking;
    private int _loadGeneration;
    private bool _saving;

    public AddressBookForm(MailDatabase database, bool picking = false, string to = "", string cc = "", string bcc = "")
    {
        _database = database;
        _picking = picking;
        Text = picking ? "Select Recipients" : "Address Book";
        AccessibleName = Text;
        ClientSize = picking ? new Size(760, 460) : new Size(560, 460);
        MinimumSize = new Size(480, 320);
        StartPosition = FormStartPosition.CenterParent;
        Icon = Dialog.WindowIcon();
        ShowInTaskbar = false;
        Font = new Font("Tahoma", 8.25f);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

        _contacts.Columns.Add("Name", 200);
        _contacts.Columns.Add("E-Mail Address", 240);
        _contacts.DoubleClick += (_, _) => { if (_picking) Add("To"); else EditSelected(); };
        _contacts.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; } };
        _search.TextChanged += (_, _) => Fill();

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(0, 2, 0, 2), WrapContents = false, AccessibleName = "Address book actions" };
        actions.Controls.Add(ActionButton("New Contact…", "New contact", NewContact));
        actions.Controls.Add(ActionButton("Properties…", "Contact properties", EditSelected));
        actions.Controls.Add(ActionButton("Delete", "Delete contact", DeleteSelected));
        actions.Controls.Add(ActionButton("Import from OE Classic…", "Import OE Classic contacts", ImportOe, 150));

        var left = new BufferedPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 6, 4, 4) };
        left.Controls.Add(_contacts);
        left.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 4 });
        left.Controls.Add(_search);
        left.Controls.Add(actions);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        if (picking)
        {
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 82, AccessibleName = "Cancel" };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 82, AccessibleName = "OK" };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(left);
            Controls.Add(RecipientPanel(to, cc, bcc));
        }
        else
        {
            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Width = 82, AccessibleName = "Close" };
            buttons.Controls.Add(close);
            CancelButton = close;
            Controls.Add(left);
        }
        Controls.Add(buttons);
        Shown += async (_, _) => { _search.Focus(); await ReloadAsync(); };
    }

    /// <summary>The chosen recipients, written as the address lines take them.</summary>
    public IReadOnlyList<string> Chosen(string field) => _chosen.TryGetValue(field, out var list) ? list.Items.Cast<string>().ToArray() : [];

    private Control RecipientPanel(string to, string cc, string bcc)
    {
        var panel = new BufferedTableLayoutPanel { Dock = DockStyle.Right, Width = 300, ColumnCount = 2, RowCount = 4, Padding = new Padding(4, 6, 8, 4), AccessibleName = "Message recipients" };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        foreach (var _ in Enumerable.Range(0, 3)) panel.RowStyles.Add(new RowStyle(SizeType.Percent, 33.3f));
        panel.Controls.Add(new Label { Text = "Message recipients:", AutoSize = true, Margin = new Padding(0, 14, 0, 0) }, 0, 0);
        panel.SetColumnSpan(panel.GetControlFromPosition(0, 0)!, 2);
        var row = 1;
        foreach (var (field, initial) in new[] { ("To", to), ("Cc", cc), ("Bcc", bcc) })
        {
            var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, AccessibleName = field + " recipients chosen", SelectionMode = SelectionMode.MultiExtended };
            list.Items.AddRange(Recipients.Split(initial).Cast<object>().ToArray());
            list.KeyDown += (_, e) =>
            {
                if (e.KeyCode != Keys.Delete) return;
                foreach (var item in list.SelectedItems.Cast<object>().ToArray()) list.Items.Remove(item);
                e.Handled = true;
            };
            _chosen[field] = list;
            var add = new Button { Text = field + ": ->", Width = 64, AccessibleName = "Add to " + field, Margin = new Padding(0, 0, 4, 0) };
            add.Click += (_, _) => Add(field);
            panel.Controls.Add(add, 0, row);
            panel.Controls.Add(list, 1, row);
            row++;
        }
        return panel;
    }

    private static Button ActionButton(string text, string accessibleName, Action click, int width = 100)
    {
        var button = new Button { Text = text, AccessibleName = accessibleName, AutoSize = true, MinimumSize = new Size(Math.Min(width, 80), 24), Height = 24, Margin = new Padding(0, 0, 4, 0) };
        button.Click += (_, _) => click();
        return button;
    }

    private Contact[] SelectedContacts() => _contacts.SelectedItems.Cast<ListViewItem>().Select(item => (Contact)item.Tag!).ToArray();

    private void Add(string field)
    {
        if (!_chosen.TryGetValue(field, out var list)) return;
        var present = list.Items.Cast<string>().Select(Recipients.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var contact in SelectedContacts())
            if (present.Add(contact.Email)) list.Items.Add(Recipients.Format(contact));
    }

    private async Task ReloadAsync(long? select = null)
    {
        var generation = ++_loadGeneration;
        try
        {
            var contacts = await Task.Run(_database.ListContacts);
            if (IsDisposed || Disposing || generation != _loadGeneration) return;
            _all = contacts;
            Fill(select);
        }
        catch (Exception error) { if (!IsDisposed) MessageBox.Show(this, error.Message, "Address Book", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void Fill(long? select = null)
    {
        var query = _search.Text.Trim();
        var shown = query.Length == 0 ? _all
            : _all.Where(contact => contact.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || contact.Email.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        _contacts.BeginUpdate();
        _contacts.Items.Clear();
        foreach (var contact in shown)
        {
            var item = new ListViewItem(contact.Name.Length == 0 ? contact.Email : contact.Name) { Tag = contact, Name = contact.Id.ToString() };
            item.SubItems.Add(contact.Email);
            _contacts.Items.Add(item);
            if (contact.Id == select) { item.Selected = true; item.Focused = true; }
        }
        if (_contacts.SelectedItems.Count == 0 && _contacts.Items.Count > 0) _contacts.Items[0].Selected = true;
        _contacts.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.EnsureVisible();
        _contacts.EndUpdate();
    }

    private async void NewContact()
    {
        if (_saving) return;
        using var form = new ContactForm(new Contact(0, "", ""));
        while (form.ShowDialog(this) == DialogResult.OK)
        {
            _saving = true;
            try
            {
                var contact = form.Contact;
                var saved = await Task.Run(() => _database.SaveContact(contact));
                await ReloadAsync(saved.Id);
                return;
            }
            catch (Exception error)
            {
                if (IsDisposed) return;
                MessageBox.Show(this, error.Message, "New Contact", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { _saving = false; }
            if (IsDisposed) return;
        }
    }

    private async void EditSelected()
    {
        if (_saving) return;
        if (SelectedContacts().FirstOrDefault() is not { } contact) return;
        using var form = new ContactForm(contact);
        while (form.ShowDialog(this) == DialogResult.OK)
        {
            _saving = true;
            try
            {
                var changed = form.Contact;
                await Task.Run(() => _database.SaveContact(changed));
                await ReloadAsync(contact.Id);
                return;
            }
            catch (Exception error)
            {
                if (IsDisposed) return;
                MessageBox.Show(this, error.Message, "Contact Properties", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { _saving = false; }
            if (IsDisposed) return;
        }
    }

    private async void DeleteSelected()
    {
        if (_saving) return;
        var chosen = SelectedContacts();
        if (chosen.Length == 0) return;
        var what = chosen.Length == 1 ? Recipients.Format(chosen[0]) : $"these {chosen.Length:N0} contacts";
        if (MessageBox.Show(this, $"Delete {what} from the address book?", "Delete contact", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _saving = true;
        try
        {
            await Task.Run(() => { foreach (var contact in chosen) _database.DeleteContact(contact.Id); });
            await ReloadAsync();
        }
        catch (Exception error) { if (!IsDisposed) MessageBox.Show(this, error.Message, "Delete contact", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { _saving = false; }
    }

    /// <summary>Reads OE Classic's address book from a copy, never the file itself, and adds only addresses not
    /// already here, so running it again — or after editing an imported contact — changes nothing.</summary>
    private async void ImportOe()
    {
        if (_saving) return;
        var path = OeAddressBook.DefaultPath;
        if (!File.Exists(path))
        {
            using var open = new OpenFileDialog { Title = "Find OE Classic's address book", Filter = "OE Classic address book (_AddressBook.db)|_AddressBook.db|All files|*.*" };
            if (open.ShowDialog(this) != DialogResult.OK) return;
            path = open.FileName;
        }
        _saving = true;
        try
        {
            var (added, held, unusable) = await Task.Run(() => _database.ImportContacts(OeAddressBook.Read(path)));
            await ReloadAsync();
            if (IsDisposed) return;
            MessageBox.Show(this, $"{added:N0} contacts added from OE Classic. {held:N0} were already in the address book"
                + (unusable > 0 ? $", and {unusable:N0} had no usable address." : ".")
                + "\r\n\r\nOE Classic's own address book was read from a copy and has not been changed.",
                "Import from OE Classic", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception error)
        {
            if (!IsDisposed) MessageBox.Show(this, error.Message, "Import from OE Classic", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { _saving = false; }
    }
}

/// <summary>One contact's name, address and notes.</summary>
internal sealed class ContactForm : Form
{
    private readonly TextBox _name = new() { Width = 300, AccessibleName = "Contact name" };
    private readonly TextBox _email = new() { Width = 300, AccessibleName = "Contact email address" };
    private readonly TextBox _notes = new() { Width = 300, Height = 70, Multiline = true, ScrollBars = ScrollBars.Vertical, AccessibleName = "Contact notes" };
    private readonly Contact _original;

    public ContactForm(Contact contact)
    {
        _original = contact;
        Text = contact.Id == 0 ? "New Contact" : "Contact Properties";
        AccessibleName = Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Tahoma", 8.25f);
        ClientSize = new Size(420, 200);
        _name.Text = contact.Name;
        _email.Text = contact.Email;
        _notes.Text = contact.Notes;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10, 10, 10, 0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string caption, Control control)
        {
            var row = layout.RowCount++;
            layout.Controls.Add(new Label { Text = caption, AutoSize = true, Margin = new Padding(0, 6, 0, 0) }, 0, row);
            layout.Controls.Add(control, 1, row);
        }
        Row("Name:", _name);
        Row("E-Mail:", _email);
        Row("Notes:", _notes);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 82, AccessibleName = "Cancel" };
        var ok = new Button { Text = "OK", Width = 82, AccessibleName = "OK" };
        ok.Click += (_, _) =>
        {
            var address = _email.Text.Trim();
            if (!address.Contains('@') || !MimeKit.MailboxAddress.TryParse(address, out var parsed) || parsed.Address != address)
            {
                MessageBox.Show(this, "Enter an email address, such as name@example.com.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _email.Focus();
                return;
            }
            DialogResult = DialogResult.OK;
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        Controls.Add(layout);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public Contact Contact => _original with { Name = _name.Text.Trim(), Email = _email.Text.Trim(), Notes = _notes.Text.Trim() };
}
