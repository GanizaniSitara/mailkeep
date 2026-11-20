using Mailkeep.Core;
using Mailkeep.Infrastructure;

namespace Mailkeep.WinForms;

internal sealed class OAuthAccountForm : Form
{
    private readonly TextBox _address = new();
    private readonly TextBox _name = new();
    private readonly TextBox _folder = new() { Text = "Inbox" };

    public OAuthAccountForm(string provider, bool localMail = false)
    {
        Text = (localMail ? "Add Local Mail " : "Add Connected ") + (provider == "gmail" ? "Gmail account" : "Hotmail account");
        AccessibleName = Text;
        ClientSize = new Size(430, 178);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        var layout = DialogLayout();
        AddRow(layout, "Email address", _address);
        AddRow(layout, "Account name", _name);
        AddRow(layout, "Folder label", _folder);
        if (localMail) { _folder.Text = "Inbox"; _folder.Enabled = false; }
        layout.Controls.Add(Buttons(() => ValidateAndClose(_address.Text.Contains('@'), "Enter a valid email address.")), 0, 3);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 3)!, 2);
        Controls.Add(layout);
    }

    public string Address => _address.Text.Trim();
    public string AccountName => string.IsNullOrWhiteSpace(_name.Text) ? Address : _name.Text.Trim();
    public string FolderLabel => string.IsNullOrWhiteSpace(_folder.Text) ? Address : _folder.Text.Trim();
    private void ValidateAndClose(bool valid, string message) { if (!valid) { MessageBox.Show(this, message, Text); return; } DialogResult = DialogResult.OK; }

    internal static TableLayoutPanel DialogLayout() => new()
    {
        Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, AutoSize = true,
        ColumnStyles = { new ColumnStyle(SizeType.Absolute, 125), new ColumnStyle(SizeType.Percent, 100) }
    };

    internal static void AddRow(TableLayoutPanel layout, string label, Control control)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        control.Dock = DockStyle.Fill;
        control.AccessibleName = label;
        layout.Controls.Add(control, 1, row);
    }

    internal FlowLayoutPanel Buttons(Action accept)
    {
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.None, AccessibleName = "OK", Width = 82 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AccessibleName = "Cancel", Width = 82 };
        ok.Click += (_, _) => accept();
        panel.Controls.Add(ok); panel.Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;
        return panel;
    }
}

internal sealed class KeepAliveForm : Form
{
    private readonly CheckBox _enabled;
    private readonly NumericUpDown _hours;

    public KeepAliveForm(MailAccount account)
    {
        Text = "Connected account keep-alive";
        AccessibleName = Text;
        ClientSize = new Size(390, 130);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        _enabled = new CheckBox { Text = "Enable scheduled keep-alive", Checked = account.EffectiveKeepAlive.Enabled, AutoSize = true, AccessibleName = "Enable scheduled keep alive" };
        _hours = new NumericUpDown { Minimum = 1, Maximum = 720, Value = account.EffectiveKeepAlive.IntervalHours, AccessibleName = "Interval hours" };
        var layout = OAuthAccountForm.DialogLayout();
        layout.Controls.Add(_enabled, 0, 0); layout.SetColumnSpan(_enabled, 2);
        OAuthAccountForm.AddRow(layout, "Interval (hours)", _hours);
        var buttons = Buttons(); layout.Controls.Add(buttons, 0, 2); layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
    }

    public bool KeepAliveEnabled => _enabled.Checked;
    public int IntervalHours => decimal.ToInt32(_hours.Value);
    private FlowLayoutPanel Buttons()
    {
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AccessibleName = "OK", Width = 82 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AccessibleName = "Cancel", Width = 82 };
        panel.Controls.Add(ok); panel.Controls.Add(cancel); AcceptButton = ok; CancelButton = cancel; return panel;
    }
}

internal sealed class ServerAccountForm : Form
{
    private readonly ComboBox _preset = Combo(["Custom", "Gmail", "Outlook.com", "Yahoo", "AOL", "iCloud", "Fastmail"], 0);
    private readonly Label _providerHelp = new() { TextAlign = ContentAlignment.MiddleLeft };
    private readonly ComboBox _mode = Combo(["Local Mail — keep copy on server", "Connected Account — server mailbox"], 0);
    private readonly TextBox _address = new();
    private readonly TextBox _name = new();
    private readonly TextBox _folder = new() { Text = "Inbox" };
    private readonly TextBox _username = new();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly ComboBox _protocol = Combo(["POP3", "IMAP"], 0);
    private readonly TextBox _host = new();
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 995 };
    private readonly ComboBox _security = Combo(["SSL on connect", "STARTTLS", "Automatic", "None (no encryption)"], 0);
    private readonly TextBox _displayName = new();
    private readonly TextBox _smtpHost = new();
    private readonly NumericUpDown _smtpPort = new() { Minimum = 1, Maximum = 65535, Value = 587 };
    private readonly ComboBox _smtpSecurity = Combo(["SSL on connect", "STARTTLS", "Automatic", "None (no encryption)"], 1);
    private readonly TextBox _smtpUsername = new();
    private readonly TextBox _smtpPassword = new() { UseSystemPasswordChar = true };

    public ServerAccountForm(string mailboxMode = MailboxModes.LocalKeep) : this(mailboxMode, null) { }

    public ServerAccountForm(string mailboxMode, string? preset)
    {
        Text = "Add mail account";
        AccessibleName = "Add Other Account";
        ClientSize = mailboxMode == MailboxModes.LocalKeep ? new Size(600, 754) : new Size(560, 564);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        _mode.SelectedIndex = mailboxMode == MailboxModes.Connected ? 1 : 0;
        _mode.Enabled = false;
        _protocol.SelectedIndex = mailboxMode == MailboxModes.Connected ? 1 : 0;
        _preset.SelectedIndexChanged += (_, _) => { _password.Clear(); _smtpPassword.Clear(); ApplyPreset(); };
        _mode.SelectedIndexChanged += (_, _) => { if (_mode.SelectedIndex == 1) _protocol.SelectedIndex = 1; };
        _protocol.SelectedIndexChanged += (_, _) => { _port.Value = _protocol.SelectedIndex == 0 ? 995 : 993; ApplyPreset(); };
        var layout = OAuthAccountForm.DialogLayout();
        OAuthAccountForm.AddRow(layout, "Preset", _preset);
        OAuthAccountForm.AddRow(layout, "Sign in", _providerHelp);
        layout.RowStyles[layout.RowCount - 1].Height = 54;
        OAuthAccountForm.AddRow(layout, "Account type", _mode);
        OAuthAccountForm.AddRow(layout, "Email address", _address);
        OAuthAccountForm.AddRow(layout, "Account name", _name);
        OAuthAccountForm.AddRow(layout, "Folder label", _folder);
        OAuthAccountForm.AddRow(layout, "Username", _username);
        OAuthAccountForm.AddRow(layout, "Password", _password);
        OAuthAccountForm.AddRow(layout, "Incoming protocol", _protocol);
        OAuthAccountForm.AddRow(layout, "Incoming server", _host);
        OAuthAccountForm.AddRow(layout, "Incoming port", _port);
        OAuthAccountForm.AddRow(layout, "Incoming security", _security);
        if (IsLocal)
        {
            OAuthAccountForm.AddRow(layout, "From name", _displayName);
            OAuthAccountForm.AddRow(layout, "Outgoing server", _smtpHost);
            OAuthAccountForm.AddRow(layout, "Outgoing port", _smtpPort);
            OAuthAccountForm.AddRow(layout, "Outgoing security", _smtpSecurity);
            OAuthAccountForm.AddRow(layout, "Outgoing username", _smtpUsername);
            OAuthAccountForm.AddRow(layout, "Outgoing password", _smtpPassword);
            OAuthAccountForm.AddRow(layout, "", new Label { Text = "Blank outgoing login fields use the incoming login. Leave the outgoing server blank for receiving only.", AutoSize = true });
        }
        var buttons = Buttons(); layout.Controls.Add(buttons, 0, layout.RowCount++); layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        if (preset is not null)
            _preset.SelectedIndex = preset switch { "yahoo" => 3, "aol" => 4, "icloud" => 5, "fastmail" => 6, _ => 0 };
        ApplyPreset();
    }

    public MailAccount Account => new(
        string.IsNullOrWhiteSpace(_name.Text) ? (IsLocal ? "local:" : "connected:") + _address.Text.Trim().ToLowerInvariant() : _name.Text.Trim(),
        _preset.SelectedIndex switch { 1 => "gmail", 2 => "outlook", _ => "custom" },
        _address.Text.Trim(), "INBOX", IsLocal ? LocalFolderKinds.Inbox : string.IsNullOrWhiteSpace(_folder.Text) ? _address.Text.Trim() : _folder.Text.Trim(),
        null, null, _password.Text, new KeepAliveSettings(), _mode.SelectedIndex == 0 ? MailboxModes.LocalKeep : MailboxModes.Connected,
        new IncomingServerSettings(_protocol.SelectedIndex == 0 ? MailProtocols.Pop3 : MailProtocols.Imap, _host.Text.Trim(), decimal.ToInt32(_port.Value), Security()),
        string.IsNullOrWhiteSpace(_username.Text) ? _address.Text.Trim() : _username.Text.Trim());

    internal string? BetaProvider => _preset.SelectedItem is string name && name is "Yahoo" or "AOL" or "iCloud" or "Fastmail" ? name : null;
    private bool IsLocal => _mode.SelectedIndex == 0;

    public SendingIdentity? Identity => !IsLocal || string.IsNullOrWhiteSpace(_smtpHost.Text) ? null
        : new SendingIdentity(Account.Name, null, _displayName.Text.Trim(), Account.Address, null, false,
            Account.Provider, new OutgoingServerSettings(_smtpHost.Text.Trim(), decimal.ToInt32(_smtpPort.Value),
                _smtpSecurity.SelectedIndex switch { 0 => MailSecurityModes.SslOnConnect, 1 => MailSecurityModes.StartTls, 3 => MailSecurityModes.None, _ => MailSecurityModes.Auto }),
            string.IsNullOrWhiteSpace(_smtpUsername.Text) ? (_preset.SelectedIndex == 5 ? Account.Address : Account.EffectiveUsername) : _smtpUsername.Text.Trim(), null,
            _smtpPassword.Text.Length == 0 ? _password.Text : _smtpPassword.Text);

    private FlowLayoutPanel Buttons()
    {
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        var add = new Button { Text = "Add account", AccessibleName = "Add account", Width = 96 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AccessibleName = "Cancel", Width = 82 };
        add.Click += (_, _) => Save(); panel.Controls.Add(add); panel.Controls.Add(cancel); AcceptButton = add; CancelButton = cancel; return panel;
    }

    private void Save()
    {
        if (!_address.Text.Contains('@') || string.IsNullOrWhiteSpace(_host.Text)) { MessageBox.Show(this, "Enter an email address and incoming server.", Text); return; }
        if (_mode.SelectedIndex == 1 && _protocol.SelectedIndex == 0) { MessageBox.Show(this, "Connected Accounts require IMAP.", Text); return; }
        DialogResult = DialogResult.OK;
    }

    private void ApplyPreset()
    {
        if (_preset.SelectedIndex == 5 && _protocol.SelectedIndex != 1) { _protocol.SelectedIndex = 1; return; }
        _protocol.Enabled = IsLocal && _preset.SelectedIndex != 5;
        _providerHelp.Text = _preset.SelectedIndex switch
        {
            3 => "Use a Yahoo app password from Account Security and your full email address, not your usual Yahoo password.",
            4 => "Use an AOL app password from Account Security and your full email address, not your usual AOL password.",
            5 => "iCloud uses IMAP, not POP3. Use an Apple app-specific password. Sending requires your full iCloud email address.",
            6 => "Use a Fastmail app password and full login address. Your plan must include access for email apps.",
            1 or 2 => "For browser sign-in, use the Gmail or Hotmail option in Add Account. This form uses an app password.",
            _ => "Enter the server settings and login supplied by your email provider."
        };
        if (_preset.SelectedIndex == 0) return;
        var pop = _protocol.SelectedIndex == 0;
        _host.Text = _preset.SelectedIndex switch
        {
            1 => pop ? "pop.gmail.com" : "imap.gmail.com",
            2 => "outlook.office365.com",
            3 => pop ? "pop.mail.yahoo.com" : "imap.mail.yahoo.com",
            4 => pop ? "pop.aol.com" : "imap.aol.com",
            5 => "imap.mail.me.com",
            6 => pop ? "pop.fastmail.com" : "imap.fastmail.com",
            _ => _host.Text
        };
        _port.Value = pop ? 995 : 993;
        _security.SelectedIndex = 0;
        _smtpHost.Text = _preset.SelectedIndex switch
        {
            1 => "smtp.gmail.com", 2 => "smtp.office365.com", 3 => "smtp.mail.yahoo.com",
            4 => "smtp.aol.com", 5 => "smtp.mail.me.com", 6 => "smtp.fastmail.com", _ => _smtpHost.Text
        };
        var ssl = _preset.SelectedIndex is 3 or 4 or 6;
        _smtpPort.Value = ssl ? 465 : 587;
        _smtpSecurity.SelectedIndex = ssl ? 0 : 1;
    }

    private string Security() => _security.SelectedIndex switch { 0 => MailSecurityModes.SslOnConnect, 1 => MailSecurityModes.StartTls, 3 => MailSecurityModes.None, _ => MailSecurityModes.Auto };
    private static ComboBox Combo(string[] values, int selected) => Dialog.Combo(values, selected);
}

internal sealed class FetchLogForm : Form
{
    public FetchLogForm(IReadOnlyList<FetchAttempt> attempts)
    {
        Text = "Mailkeep fetch log"; AccessibleName = Text; Size = new Size(1400, 800); StartPosition = FormStartPosition.CenterParent;
        var grid = Grid(attempts);
        // Failed attempts are what this window gets opened for, so they stand out from the successes.
        grid.DataBindingComplete += (_, _) =>
        {
            foreach (DataGridViewRow row in grid.Rows)
                if (row.DataBoundItem is FetchAttempt { Ok: false })
                    row.DefaultCellStyle.BackColor = Color.MistyRose;
        };
        Controls.Add(grid);
    }

    internal static DataGridView Grid(object data) => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, DataSource = data, AccessibleName = "Data grid"
    };
}


/// <summary>Shared dialog plumbing. Lists are filled through Items: a DataSource-bound combo stays empty
/// until the control has a binding context, so setting SelectedIndex on one throws in the constructor.</summary>
internal static class Dialog
{
    public static ComboBox Combo(string[] values, int selected = -1)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(values.Cast<object>().ToArray());
        if (selected >= 0 && values.Length > 0) combo.SelectedIndex = selected;
        return combo;
    }

    /// <summary>The program's own icon, taken from its executable. Reading it can fail — the file is replaced
    /// under a running copy on every install — and a window without an icon is a far smaller loss than a window
    /// that never opens, so a failure is simply no icon.</summary>
    public static Icon? WindowIcon()
    {
        try
        {
            return Environment.ProcessPath is { } executablePath ? Icon.ExtractAssociatedIcon(executablePath) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static Label Note(string text, string accessibleName) => new()
    {
        Dock = DockStyle.Top,
        Height = 38,
        Padding = new Padding(8, 8, 8, 0),
        ForeColor = Color.DimGray,
        Text = text,
        AccessibleName = accessibleName
    };
}

internal enum AccountsAction { None, Add, Properties, Authorize, SetDefaultSender, Remove }

/// <summary>Every account in one window, split by what it can actually do: Local Mail sends and receives,
/// Connected accounts only receive. Each tab says where its list came from, because the two have different origins.</summary>
internal sealed class AccountsForm : Form
{
    private readonly DataGridView _localGrid;
    private readonly DataGridView _connectedGrid;
    // The full fetch error per account, shown as the Fetched cell's tooltip; the cell itself stays short.
    private readonly Dictionary<string, string> _fetchErrors = new(StringComparer.OrdinalIgnoreCase);
    // Fixed tab width so the two modes read as equals rather than one label being longer than the other.
    private readonly TabControl _tabs = new()
    {
        Dock = DockStyle.Fill,
        AccessibleName = "Account kinds",
        SizeMode = TabSizeMode.Fixed,
        ItemSize = new Size(150, 24)
    };

    public AccountsAction RequestedAction { get; private set; }
    public string? SelectedAccountName { get; private set; }
    public string MailboxMode => _tabs.SelectedIndex == 1 ? MailboxModes.Connected : MailboxModes.LocalKeep;

    public AccountsForm(IReadOnlyList<MailAccount> accounts, IReadOnlyList<SendingIdentity> identities,
        IReadOnlyList<FetchAttempt> attempts)
    {
        Text = "Accounts";
        AccessibleName = Text;
        Size = new Size(980, 460);
        StartPosition = FormStartPosition.CenterParent;
        var latest = attempts.GroupBy(value => value.Account, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(value => value.StartedAt).First(), StringComparer.OrdinalIgnoreCase);

        var localRows = accounts.Where(value => !value.IsConnected).Select(value => new LocalRow(
            value.Name, Display(value), value.Address, value.Provider, $"{value.EffectiveProtocol.ToUpperInvariant()} {value.Incoming?.Host ?? ""}".Trim(),
            SendState(identities, value.Address), Fetched(latest, value.Name, _fetchErrors))).ToArray();
        _localGrid = List(localRows, "Local account list");
        _tabs.TabPages.Add(Page("Local Mail", _localGrid,
            "Downloads mail to this machine and leaves the copy on the server, so another client still receives it. "
            + "Deleting here is local only, and these are the only accounts you can send from."
            + (localRows.Length == 0 ? " None yet — use Add." : ""),
            "Local account guidance"));

        var connectedRows = accounts.Where(value => value.IsConnected).Select(value => new ConnectedRow(
            value.Name, Display(value), value.Address, value.Provider, value.EffectiveProtocol.ToUpperInvariant(), value.Incoming?.Host ?? "",
            value.EffectiveKeepAlive.Enabled, Fetched(latest, value.Name, _fetchErrors))).ToArray();
        _connectedGrid = List(connectedRows, "Connected account list");
        _tabs.TabPages.Add(Page("Connected", _connectedGrid,
            "Works on the mailbox as it sits on the server: nothing is kept only here, and deleting removes it from the "
            + "server too. Receiving only — sending as these addresses is not configured.", "Connected account guidance"));

        // Both action rows live on the form rather than inside the pages, so the buttons stay in one place
        // as the tab changes and never sit behind a grid in the accessibility tree.
        var local = LocalButtons();
        var connected = ConnectedButtons();
        connected.Visible = false;
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            local.Visible = _tabs.SelectedIndex == 0;
            connected.Visible = _tabs.SelectedIndex == 1;
        };
        Controls.Add(_tabs);
        Controls.Add(local);
        Controls.Add(connected);
    }

    /// <summary>Account names carry a mode prefix so a Local and a Connected account for the same address can
    /// coexist as separate keys. That is storage's problem, not the reader's.</summary>
    private static string Display(MailAccount account)
    {
        var name = account.Name;
        foreach (var prefix in new[] { "local:", "connected:" })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return name[prefix.Length..];
        return name;
    }

    private static string SendState(IReadOnlyList<SendingIdentity> identities, string address)
    {
        var identity = identities.FirstOrDefault(value => value.Email.Equals(address, StringComparison.OrdinalIgnoreCase));
        return identity is null ? "Not configured"
            : identity.IsDefault && identity.IsReady ? "Ready — default From"
            : SendState(identity);
    }

    /// <summary>One stored state covers "no credential" and "credential not proven yet", but telling someone to
    /// authorize an account they just authorized is nonsense — so the credential decides the wording.</summary>
    internal static string SendState(SendingIdentity identity) => identity switch
    {
        { IsReady: true } => "Ready to send",
        { Status: SendingIdentityStates.FailedTest } => "Test failed",
        { HasCredential: true } => "Authorized — run Test",
        _ => "Needs authorization"
    };

    /// <summary>A failed fetch reads as a short reason, not the provider's whole error text, which would stretch the
    /// column off the window. An expired sign-in says what to do about it.</summary>
    private static string Fetched(IReadOnlyDictionary<string, FetchAttempt> latest, string account, IDictionary<string, string> errors)
    {
        if (latest.GetValueOrDefault(account) is not { } attempt) return "Never";
        if (attempt.Ok) return attempt.StartedAt.ToLocalTime().ToString("g");
        var error = attempt.Error ?? "";
        errors[account] = error;
        if (error.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
            return "Sign-in expired — select it and press Authorize";
        var firstLine = error.Split('\n')[0].Trim();
        return "Failed: " + (firstLine.Length > 80 ? firstLine[..80] + "…" : firstLine);
    }

    private static DataGridView List(object rows, string accessibleName)
    {
        var grid = FetchLogForm.Grid(rows);
        grid.Dock = DockStyle.Fill;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AccessibleName = accessibleName;
        return grid;
    }

    private static TabPage Page(string title, Control grid, string note, string noteName)
    {
        var page = new TabPage(title) { AccessibleName = title, Padding = new Padding(4) };
        page.Controls.Add(grid);
        page.Controls.Add(Dialog.Note(note, noteName));
        return page;
    }

    private FlowLayoutPanel LocalButtons()
    {
        var panel = ButtonPanel("Local account actions");
        panel.Controls.Add(CloseButton());
        panel.Controls.Add(Action("Remove", "Remove local account", AccountsAction.Remove));
        panel.Controls.Add(Action("Default From", "Set default From address", AccountsAction.SetDefaultSender));
        panel.Controls.Add(Action("Authorize", "Authorize local account", AccountsAction.Authorize));
        panel.Controls.Add(Action("Properties", "Edit local account", AccountsAction.Properties));
        panel.Controls.Add(Action("Add…", "Add local account", AccountsAction.Add, needsRow: false));
        return panel;
    }

    private FlowLayoutPanel ConnectedButtons()
    {
        // No Test here: the only check for a Connected account is a real fetch, and that is what
        // Send/Receive on the toolbar already does for the selected account.
        var panel = ButtonPanel("Connected account actions");
        panel.Controls.Add(CloseButton());
        panel.Controls.Add(Action("Remove", "Remove connected account", AccountsAction.Remove));
        // Signs in again in the browser, keeping the account's name, folder and sync state.
        panel.Controls.Add(Action("Authorize", "Authorize connected account", AccountsAction.Authorize));
        panel.Controls.Add(Action("Add…", "Add connected account", AccountsAction.Add, needsRow: false));
        return panel;
    }

    private static FlowLayoutPanel ButtonPanel(string accessibleName) => new()
    {
        Dock = DockStyle.Bottom,
        Height = 44,
        FlowDirection = FlowDirection.RightToLeft,
        Padding = new Padding(6),
        AccessibleName = accessibleName
    };

    private Button CloseButton()
    {
        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AccessibleName = "Close", Width = 82 };
        CancelButton = close;
        return close;
    }

    private Button Action(string text, string accessibleName, AccountsAction action, bool needsRow = true)
    {
        var button = new Button { Text = text, AccessibleName = accessibleName, Width = 100 };
        button.Click += (_, _) => Finish(action, needsRow);
        return button;
    }

    private void Finish(AccountsAction action, bool needsRow)
    {
        SelectedAccountName = null;
        if (needsRow)
        {
            var grid = MailboxMode == MailboxModes.Connected ? _connectedGrid : _localGrid;
            SelectedAccountName = grid.CurrentRow?.DataBoundItem switch
            {
                LocalRow local => local.Key,
                ConnectedRow connected => connected.Key,
                _ => null
            };
            if (SelectedAccountName is null) return;
        }
        RequestedAction = action;
        DialogResult = DialogResult.OK;
    }

    // Auto-generated columns only exist once a grid has bound, so the storage key is hidden after the form loads.
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        foreach (var grid in new[] { _localGrid, _connectedGrid })
        {
            if (grid.Columns["Key"] is { } key) key.Visible = false;
            grid.CellToolTipTextNeeded += (_, args) =>
            {
                if (args.RowIndex < 0 || grid.Columns[args.ColumnIndex].Name != "Fetched") return;
                var name = grid.Rows[args.RowIndex].DataBoundItem switch { LocalRow local => local.Key, ConnectedRow connected => connected.Key, _ => null };
                if (name is not null && _fetchErrors.TryGetValue(name, out var error)) args.ToolTipText = error;
            };
        }
    }

    private sealed record LocalRow(string Key, string Account, string Address, string Provider, string Incoming,
        string Send, string Fetched);

    private sealed record ConnectedRow(string Key, string Account, string Address, string Provider, string Protocol,
        string Server, bool KeepAlive, string Fetched);
}

/// <summary>Search, summoned rather than parked in the toolbar. The same little window doubles as the
/// shortcut editor, because both are one line of text with a hint underneath.</summary>
internal sealed class SearchPromptForm : Form
{
    private readonly TextBox _query = new() { Dock = DockStyle.Top, AccessibleName = "Search query" };

    public string Query => _query.Text;

    public SearchPromptForm(string initial = "", bool shortcutMode = false)
    {
        Text = shortcutMode ? "Search shortcut" : "Search all mail";
        AccessibleName = Text;
        ClientSize = new Size(460, 128);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        _query.Text = initial;
        _query.Font = new Font("Tahoma", 9f);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(6), AccessibleName = "Search actions"
        };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AccessibleName = "Cancel", Width = 82 };
        var accept = new Button { Text = shortcutMode ? "Save" : "Search", DialogResult = DialogResult.OK, AccessibleName = shortcutMode ? "Save shortcut" : "Run search", Width = 82 };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(accept);
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 10, 10, 0) };
        host.Controls.Add(Dialog.Note(shortcutMode
            ? "Combine Ctrl, Shift, Alt or Win with a key — this summons search from anywhere on the desktop."
            : "Phrases in quotes, AND/OR, prefix*, subject: sender: filters — searches every account and the archive.",
            "Search guidance"));
        host.Controls.Add(_query);
        _query.BringToFront();
        Controls.Add(host);
        Controls.Add(buttons);
        AcceptButton = accept;
        CancelButton = cancel;
        Shown += (_, _) => { _query.Focus(); _query.SelectAll(); };
    }
}

/// <summary>Where to file a message. A plain list of this mailbox's folders, chosen and dismissed with one
/// double-click or the keyboard.</summary>
internal sealed class FolderChoiceForm : Form
{
    private readonly ListBox _folders = new() { Dock = DockStyle.Fill, AccessibleName = "Folder list", IntegralHeight = false };

    private sealed record Destination(LocalFolder? Folder, string Name);
    public LocalFolder? Chosen => (_folders.SelectedItem as Destination)?.Folder;

    public FolderChoiceForm(IReadOnlyList<LocalFolder> folders, IReadOnlyList<LocalFolder>? hierarchy = null,
        bool movingFolder = false, bool copying = false, bool restoring = false, bool archiveCopy = false)
    {
        Text = restoring ? archiveCopy ? "Copy archived folders to Local Mail" : "Restore from Archive" : copying ? "Copy to Local Mail" : "Move to folder";
        AccessibleName = Text;
        ClientSize = new Size(320, 340);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        var paths = FindFolder.LocalChoices(hierarchy ?? folders).ToDictionary(folder => folder.Key!, folder => folder.Name);
        _folders.DisplayMember = nameof(Destination.Name);
        if (movingFolder) _folders.Items.Add(new Destination(null, restoring ? "All Local Mail (original paths)" : "All Local Mail (top level)"));
        _folders.Items.AddRange(folders.Select(folder => new Destination(folder, paths[folder.Key]))
            .OrderBy(destination => destination.Name, StringComparer.CurrentCultureIgnoreCase).Cast<object>().ToArray());
        if (_folders.Items.Count > 0) _folders.SelectedIndex = 0;
        _folders.DoubleClick += (_, _) => { if (_folders.SelectedIndex >= 0) DialogResult = DialogResult.OK; };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(6), AccessibleName = copying ? "Copy actions" : "Move actions"
        };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AccessibleName = "Cancel", Width = 82 };
        var move = new Button { Text = restoring ? archiveCopy ? "Copy" : "Restore" : copying ? "Copy" : "Move", AccessibleName = restoring ? archiveCopy ? "Copy archived folders" : "Restore archived folders" : copying ? "Copy message" : "Move message", Width = 82 };
        move.Click += (_, _) => { if (_folders.SelectedIndex >= 0) DialogResult = DialogResult.OK; };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(move);
        Controls.Add(_folders);
        Controls.Add(Dialog.Note(restoring ? archiveCopy ? "Choose where to copy the folders. The originals stay in Archive."
                : "Choose where to restore. The folders will leave Archive."
            : movingFolder ? "Choose where to move this folder."
            : copying ? "Choose where to keep the local copies. The originals remain Connected."
            : "Choose where to file these messages.", copying ? "Copy guidance" : "Move guidance"));
        Controls.Add(buttons);
        AcceptButton = move;
        CancelButton = cancel;
    }
}

/// <summary>Which provider a new account is with. Add is one button in the Accounts window, so the choice
/// happens here instead of in a menu the user has to find.</summary>
internal sealed class ProviderChoiceForm : Form
{
    public string Choice { get; private set; } = "";

    public ProviderChoiceForm(string mailboxMode)
    {
        var local = mailboxMode == MailboxModes.LocalKeep;
        Text = local ? "Add Local Mail account" : "Add Connected account";
        AccessibleName = Text;
        ClientSize = new Size(420, 410);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(16, 8, 16, 8),
            AccessibleName = "Provider choices"
        };
        buttons.Controls.Add(Pick("Gmail…", "gmail", "Add Gmail account"));
        buttons.Controls.Add(Pick("Hotmail or Outlook.com…", "outlook", "Add Hotmail account"));
        buttons.Controls.Add(Pick("Yahoo…", "yahoo", "Add Yahoo account"));
        buttons.Controls.Add(Pick("AOL…", "aol", "Add AOL account"));
        buttons.Controls.Add(Pick("iCloud…", "icloud", "Add iCloud account"));
        buttons.Controls.Add(Pick("Fastmail…", "fastmail", "Add Fastmail account"));
        buttons.Controls.Add(Pick("Other (enter servers)…", "other", "Add other account"));
        var contact = new LinkLabel
        {
            Text = "Provider not listed? Get in touch",
            AccessibleName = "Request another email provider",
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 12)
        };
        contact.Links.Add(0, contact.Text.Length, "https://github.com/GanizaniSitara/mailkeep/issues/new");
        contact.LinkClicked += (_, e) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo((string)e.Link!.LinkData!) { UseShellExecute = true });
            }
            catch (Exception)
            {
                MessageBox.Show(this, "Open https://github.com/GanizaniSitara/mailkeep/issues to request your email provider.", "Get in touch");
            }
        };
        buttons.Controls.Add(contact);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AccessibleName = "Cancel", Width = 240, Height = 30 };
        buttons.Controls.Add(cancel);
        CancelButton = cancel;
        Controls.Add(buttons);
        Controls.Add(Dialog.Note(local
            ? "Local Mail keeps the copy on the server and can send as this address."
            : "Connected accounts are read over IMAP and cannot send.", "Add account guidance"));
    }

    private Button Pick(string text, string choice, string accessibleName)
    {
        var button = new Button { Text = text, AccessibleName = accessibleName, Width = 240, Height = 30, Margin = new Padding(0, 0, 0, 6) };
        button.Click += (_, _) => { Choice = choice; DialogResult = DialogResult.OK; };
        return button;
    }
}

/// <summary>One account, both directions. Sending is a property of the account rather than a separate
/// concept, so the incoming server and the From address are configured in the same place.</summary>
internal sealed class AccountPropertiesForm : Form
{
    private readonly MailAccount _account;
    private readonly SendingIdentity? _identity;
    private readonly TextBox _address = new();
    private readonly ComboBox _protocol = Dialog.Combo(["POP3", "IMAP"], 0);
    private readonly TextBox _incomingHost = new();
    private readonly NumericUpDown _incomingPort = new() { Minimum = 1, Maximum = 65535 };
    private readonly ComboBox _incomingSecurity = Dialog.Combo(Securities, 0);
    private readonly TextBox _username = new();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly TextBox _displayName = new();
    private readonly TextBox _smtpHost = new();
    private readonly NumericUpDown _smtpPort = new() { Minimum = 1, Maximum = 65535, Value = 587 };
    private readonly ComboBox _smtpSecurity = Dialog.Combo(Securities, 1);
    private readonly TextBox _smtpPassword = new() { UseSystemPasswordChar = true };
    private readonly TextBox _smtpUsername = new();
    private readonly CheckBox _default = new() { Text = "Use as the default From address", AutoSize = true, AccessibleName = "Default From address" };

    private static string[] Securities => ["SSL on connect", "STARTTLS", "Automatic", "None (no encryption)"];

    public AccountPropertiesForm(MailAccount account, SendingIdentity? identity)
    {
        _account = account;
        _identity = identity;
        Text = $"{account.Address} properties";
        AccessibleName = "Account properties";
        ClientSize = new Size(560, 594);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;

        _address.Text = account.Address;
        _protocol.SelectedIndex = account.EffectiveProtocol == MailProtocols.Imap ? 1 : 0;
        _incomingHost.Text = account.Incoming?.Host ?? "";
        _incomingPort.Value = account.Incoming?.Port ?? (account.EffectiveProtocol == MailProtocols.Imap ? 993 : 995);
        _incomingSecurity.SelectedIndex = SecurityIndex(account.Incoming?.Security);
        _username.Text = account.Username ?? "";
        _displayName.Text = identity?.DisplayName ?? "";
        _smtpHost.Text = identity?.Smtp.Host ?? DefaultSmtpHost(account.Provider);
        _smtpPort.Value = identity?.Smtp.Port ?? DefaultSmtpPort(account.Provider);
        _smtpUsername.Text = identity?.Username ?? account.Username ?? "";
        _smtpSecurity.SelectedIndex = SecurityIndex(identity?.Smtp.Security);
        _default.Checked = identity?.IsDefault ?? false;

        var layout = OAuthAccountForm.DialogLayout();
        OAuthAccountForm.AddRow(layout, "Email address", _address);
        OAuthAccountForm.AddRow(layout, "Incoming protocol", _protocol);
        OAuthAccountForm.AddRow(layout, "Incoming server", _incomingHost);
        OAuthAccountForm.AddRow(layout, "Incoming port", _incomingPort);
        OAuthAccountForm.AddRow(layout, "Incoming security", _incomingSecurity);
        OAuthAccountForm.AddRow(layout, "Username", _username);
        OAuthAccountForm.AddRow(layout, "Incoming password", _password);
        OAuthAccountForm.AddRow(layout, "From display name", _displayName);
        OAuthAccountForm.AddRow(layout, "Outgoing server", _smtpHost);
        OAuthAccountForm.AddRow(layout, "Outgoing port", _smtpPort);
        OAuthAccountForm.AddRow(layout, "Outgoing security", _smtpSecurity);
        OAuthAccountForm.AddRow(layout, "Outgoing username", _smtpUsername);
        OAuthAccountForm.AddRow(layout, "Outgoing password", _smtpPassword);
        var hintRow = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft,
            AccessibleName = "Credential hint",
            Text = identity?.OAuth is not null ? "Authorized with OAuth2. Leave passwords blank to keep it."
                : identity?.Password is not null ? "A password is stored. Leave blank to keep it."
                : "Authorize grants both receiving and sending in one browser sign-in."
        }, 1, hintRow);
        var defaultRow = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.Controls.Add(_default, 1, defaultRow);
        var buttonRow = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var buttons = Buttons();
        layout.Controls.Add(buttons, 0, buttonRow);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
    }

    public MailAccount Account
    {
        get
        {
            var updated = _account with
            {
                Address = _address.Text.Trim(),
                Username = string.IsNullOrWhiteSpace(_username.Text) ? null : _username.Text.Trim(),
                Incoming = new IncomingServerSettings(_protocol.SelectedIndex == 1 ? MailProtocols.Imap : MailProtocols.Pop3,
                    _incomingHost.Text.Trim(), decimal.ToInt32(_incomingPort.Value), Security(_incomingSecurity))
            };
            return _password.Text.Length > 0 ? updated with { AppPassword = _password.Text, OAuth = null } : updated;
        }
    }

    /// <summary>The identity paired with this account, or null when no outgoing server has been entered yet.</summary>
    public SendingIdentity? Identity
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_smtpHost.Text)) return null;
            var smtp = new OutgoingServerSettings(_smtpHost.Text.Trim(), decimal.ToInt32(_smtpPort.Value), Security(_smtpSecurity));
            var address = _address.Text.Trim();
            var existing = _identity ?? new SendingIdentity(_account.Name, null, _displayName.Text.Trim(), address, null,
                _default.Checked, _account.Provider, smtp, null, _account.OAuth, _account.AppPassword);
            var updated = existing with
            {
                DisplayName = _displayName.Text.Trim(),
                Email = address,
                IsDefault = _default.Checked,
                Smtp = smtp,
                Username = string.IsNullOrWhiteSpace(_smtpUsername.Text) ? null : _smtpUsername.Text.Trim()
            };
            if (_smtpPassword.Text.Length > 0) updated = updated with { Password = _smtpPassword.Text, OAuth = null };
            return SendingIdentityStore.ResetTestIfConnectionChanged(existing, updated);
        }
    }

    private static int SecurityIndex(string? security) => security switch
    {
        MailSecurityModes.SslOnConnect => 0,
        MailSecurityModes.StartTls => 1,
        MailSecurityModes.None => 3,
        _ => 2
    };

    private static string Security(ComboBox combo) => combo.SelectedIndex switch
    {
        0 => MailSecurityModes.SslOnConnect,
        1 => MailSecurityModes.StartTls,
        3 => MailSecurityModes.None,
        _ => MailSecurityModes.Auto
    };

    internal static string DefaultSmtpHost(string provider) => provider.ToLowerInvariant() switch
    {
        "gmail" => "smtp.gmail.com",
        "outlook" or "microsoft" => "smtp.office365.com",
        _ => ""
    };

    internal static int DefaultSmtpPort(string provider) => provider.Equals("gmail", StringComparison.OrdinalIgnoreCase) ? 465 : 587;

    private FlowLayoutPanel Buttons()
    {
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        var ok = new Button { Text = "OK", AccessibleName = "OK", Width = 82 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AccessibleName = "Cancel", Width = 82 };
        ok.Click += (_, _) =>
        {
            if (!_address.Text.Contains('@') || string.IsNullOrWhiteSpace(_incomingHost.Text))
            {
                MessageBox.Show(this, "Enter an email address and an incoming server.", Text);
                return;
            }
            DialogResult = DialogResult.OK;
        };
        panel.Controls.Add(ok); panel.Controls.Add(cancel); AcceptButton = ok; CancelButton = cancel; return panel;
    }
}
