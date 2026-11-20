using System;
using System.Drawing;
using System.Windows.Forms;
using Mailkeep.Infrastructure;
using Mailkeep.Core;

namespace Mailkeep.WinForms;

internal sealed class OptionsForm : Form
{
    private readonly MailkeepServices _services;
    
    private readonly List<(int Digit, ComboBox Choice)> _shortcuts = [];

    public bool AutoSync { get; private set; }
    public string AccountSort { get; private set; }

    public OptionsForm(MailkeepServices services, bool currentAutoSync, string currentAccountSort, Action<IWin32Window>? exportPictures = null)
    {
        _services = services;
        
        Text = "Options";
        Size = new Size(460, 494);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Tahoma", 8.25f);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(6, 4) };

        // General Tab
        var generalTab = new TabPage("General") { Padding = new Padding(12), BackColor = SystemColors.Window };
        
        var syncGroup = new GroupBox { Text = "Send / Receive Messages", Dock = DockStyle.Top, Height = 84, Margin = new Padding(0, 0, 0, 12) };
        var chkAutoSync = new CheckBox
        {
            Text = "Refresh connected accounts automatically",
            Checked = currentAutoSync,
            Location = new Point(16, 24),
            AutoSize = true
        };
        syncGroup.Controls.Add(chkAutoSync);
        // OE's own wording and default: Send delivers there and then; unticked, mail waits for Send/Receive.
        var chkSendNow = new CheckBox
        {
            Text = "Send messages immediately",
            Checked = SendSettings.Immediately(services.Database),
            Location = new Point(16, 48),
            AutoSize = true
        };
        syncGroup.Controls.Add(chkSendNow);
        
        var accountsGroup = new GroupBox { Text = "Connected Accounts", Dock = DockStyle.Top, Height = 70, Margin = new Padding(0, 12, 0, 0) };
        var lblSort = new Label { Text = "Sort order:", Location = new Point(16, 28), AutoSize = true };
        var cmbSort = new ComboBox 
        { 
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(90, 24),
            Width = 150
        };
        cmbSort.Items.AddRange(new[] { "Alphabetical (A-Z)", "Message Volume", "Newest Mail" });
        cmbSort.SelectedIndex = currentAccountSort switch 
        {
            "Volume" => 1,
            "New" => 2,
            _ => 0
        };
        
        accountsGroup.Controls.Add(lblSort);
        accountsGroup.Controls.Add(cmbSort);

        var toolbarGroup = new GroupBox { Text = "Toolbar", Dock = DockStyle.Top, Height = 62 };
        var showArchive = new CheckBox
        {
            Text = "Show Archive button on the toolbar",
            Checked = ToolbarSettings.ShowArchive(services.Database),
            Location = new Point(16, 24), AutoSize = true,
            AccessibleName = "Show Archive button on the toolbar"
        };
        toolbarGroup.Controls.Add(showArchive);
        generalTab.Controls.Add(toolbarGroup);
        generalTab.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 12 });

        if (exportPictures is not null)
        {
            var picturesGroup = new GroupBox { Text = "Pictures", Dock = DockStyle.Top, Height = 62 };
            var export = new Button { Text = "Export pictures from mail attachments…", AutoSize = true, Location = new Point(16, 24), AccessibleName = "Export pictures from mail" };
            export.Click += (_, _) => exportPictures(this);
            picturesGroup.Controls.Add(export);
            generalTab.Controls.Add(picturesGroup);
            generalTab.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 12 });
        }
        generalTab.Controls.Add(accountsGroup);
        generalTab.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 12 });
        generalTab.Controls.Add(syncGroup);

        tabs.TabPages.Add(generalTab);
        tabs.TabPages.Add(ShortcutsTab());
        var composeTab = new TabPage("Compose") { Padding = new Padding(12), BackColor = SystemColors.Window };
        var tabDestination = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 12, Top = 38,
            Width = 250, AccessibleName = "Tab after To" };
        tabDestination.Items.AddRange(["Subject field", "Cc field"]);
        tabDestination.SelectedIndex = ComposeSettings.TabToCc(services.Database) ? 1 : 0;
        var signature = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Left = 12, Top = 108,
            Width = 386, Height = 170, AccessibleName = "Signature", Text = ComposeSettings.Signature(services.Database) };
        composeTab.Controls.AddRange([
            new Label { Text = "After addressing To, Tab goes to:", Left = 12, Top = 14, AutoSize = true }, tabDestination,
            new Label { Text = "Signature (insert with Ctrl+Shift+S):", Left = 12, Top = 84, AutoSize = true }, signature]);
        tabs.TabPages.Add(composeTab);
        var imageTab = new TabPage("Blocked domains") { BackColor = SystemColors.Window };
        var dnsSuggestions = new CheckBox { Text = "Use DNS block lists for image blocking recommendations", Checked = TrackerSuggestions.Enabled(services.Database),
            Left = 12, Top = 16, AutoSize = true, AccessibleName = "Use DNS block-list recommendations" };
        var manageImages = new Button { Text = "Manage blocked domains…", Left = 12, Top = 132, AutoSize = true, AccessibleName = "Manage saved image sites" };
        manageImages.Click += (_, _) => BeginInvoke(() => ImageSitesForm.Edit(this, new RemoteImagePermissions(services.Database)));
        imageTab.Controls.AddRange([dnsSuggestions, new Label { Text = "Enabled by default. Uses the bundled Block List Project lists.\r\nURL-identifier suggestions remain available when this is off.\r\nManage blocked, allowed and Ask domains.\r\nSave in the domain dialog applies image permissions across all mail.", Left = 12, Top = 44, Width = 400, Height = 76 }, manageImages]);
        tabs.TabPages.Add(imageTab);
        
        // Footer buttons
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 46 };
        var btnOk = new Button { Text = "OK", Width = 80, Location = new Point(230, 8) };
        var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80, Location = new Point(316, 8) };
        footer.Controls.Add(btnOk);
        footer.Controls.Add(btnCancel);
        
        Controls.Add(tabs);
        Controls.Add(footer);
        
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        var saving = false;
        FormClosing += (_, e) => { if (saving) e.Cancel = true; };
        btnOk.Click += async (_, _) =>
        {
            if (saving) return;
            var autoSync = chkAutoSync.Checked;
            var archive = showArchive.Checked;
            var sendNow = chkSendNow.Checked;
            var tab = tabDestination.SelectedIndex == 0 ? "subject" : "cc";
            var signatureText = signature.Text;
            var dns = dnsSuggestions.Checked;
            var sort = cmbSort.SelectedIndex;
            var shortcuts = _shortcuts.Select(pair => (pair.Digit, Folder: (pair.Choice.SelectedItem as FilingShortcuts.TreeChoice)?.Key)).ToArray();
            btnOk.Enabled = false;
            btnCancel.Enabled = false;
            tabs.Enabled = false;
            saving = true;
            try
            {
                await Task.Run(() =>
                {
                    _services.Database.SetSetting(ToolbarSettings.ArchiveKey, archive ? "true" : "false");
                    SendSettings.SetImmediately(_services.Database, sendNow);
                    _services.Database.SetSetting(ComposeSettings.TabKey, tab);
                    _services.Database.SetSetting(ComposeSettings.SignatureKey, signatureText);
                    _services.Database.SetSetting(TrackerSuggestions.EnabledSetting, dns ? "true" : "false");
                    foreach (var (digit, folder) in shortcuts) FilingShortcuts.Save(_services.Database, digit, folder);
                });
                if (IsDisposed) return;
                AutoSync = autoSync;
                AccountSort = sort switch
                {
                    1 => "Volume",
                    2 => "New",
                    _ => "Alphabetical"
                };
                saving = false;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception error) { if (!IsDisposed) MessageBox.Show(this, error.Message, "Could not save Options", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { saving = false; if (!IsDisposed) { btnOk.Enabled = true; btnCancel.Enabled = true; tabs.Enabled = true; } }
        };
    }

    internal static class ToolbarSettings
    {
        internal const string ArchiveKey = "toolbar.show_archive";
        internal static bool ShowArchive(MailDatabase database) => database.GetSetting(ArchiveKey) != "false";
    }

    /// <summary>OE's "Send messages immediately": on unless turned off.</summary>
    internal static class SendSettings
    {
        private const string Key = "send.immediately";
        public static bool Immediately(MailDatabase database) => database.GetSetting(Key) != "false";
        public static void SetImmediately(MailDatabase database, bool value) => database.SetSetting(Key, value ? "true" : "false");
    }

    internal static class ComposeSettings
    {
        internal const string TabKey = "compose.tab_after_to";
        internal const string SignatureKey = "compose.signature";
        internal static bool TabToCc(MailDatabase? database) => database?.GetSetting(TabKey) == "cc";
        internal static string Signature(MailDatabase? database) => database?.GetSetting(SignatureKey) ?? "";
    }

    /// <summary>Alt+1 to Alt+0, each with the folder it files the selected mail into.</summary>
    private TabPage ShortcutsTab()
    {
        var tab = new TabPage("Filing shortcuts") { Padding = new Padding(12), BackColor = SystemColors.Window };
        var folders = FilingShortcuts.Tree(_services.Database.ListLocalFolders());
        var none = new FilingShortcuts.TreeChoice(null, "(none)", "(none)", 0, "");
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, AccessibleName = "Filing shortcuts" };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var digit in FilingShortcuts.Digits)
        {
            var row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            grid.Controls.Add(new Label { Text = $"Alt+{digit}", AutoSize = true, Margin = new Padding(0, 6, 0, 0) }, 0, row);
            var choice = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill,
                AccessibleName = $"Alt+{digit} folder", MaxDropDownItems = 24, DropDownHeight = 24 * 18 + 2
            };
            FilingShortcuts.DrawAsTree(choice);
            choice.Items.Add(none);
            choice.Items.AddRange(folders.Cast<object>().ToArray());
            var current = FilingShortcuts.Folder(_services.Database, digit);
            choice.SelectedItem = folders.FirstOrDefault(folder => folder.Key == current) ?? (object)none;
            grid.Controls.Add(choice, 1, row);
            _shortcuts.Add((digit, choice));
        }
        tab.Controls.Add(grid);
        tab.Controls.Add(new Label
        {
            Text = "Select messages and press the key to file them. Ctrl+Z puts them back.",
            Dock = DockStyle.Top, Height = 26, AccessibleName = "Filing shortcuts guidance"
        });
        return tab;
    }
}
