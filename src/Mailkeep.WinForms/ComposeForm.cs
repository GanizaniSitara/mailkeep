using System.Net;
using Mailkeep.Core;
using Mailkeep.Infrastructure;

namespace Mailkeep.WinForms;

internal sealed record ComposeSeed(string Title, string? IdentityId = null, string To = "", string Cc = "",
    string Bcc = "", string Subject = "", string Body = "", string? DraftId = null,
    IReadOnlyList<string>? AttachmentPaths = null, string? BodyHtml = null,
    long? OriginMessageId = null, string? OriginKind = null);

/// <summary>The window a message is written in, laid out as the client this is modelled on lays it: a menu,
/// a toolbar of the things done while writing (send, cut, copy, paste, undo, attach), the addressing in rows
/// beneath, a formatting bar, and the message itself as a page. A letter answered or passed on keeps the
/// look it arrived with, which is what a reader expects of forwarding and what plain text could not do.</summary>
internal sealed partial class ComposeForm : Form
{
    private readonly ComboBox _from = new() { DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Tahoma", 8.25f) };
    private readonly TextBox _to = Field();
    private readonly TextBox _cc = Field();
    private readonly TextBox _bcc = Field();
    private readonly TextBox _subject = Field();
    private readonly BufferedListView _attachments = new()
    {
        Dock = DockStyle.Fill, View = View.List, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Tahoma", 8.25f),
        MultiSelect = true, HideSelection = false, AccessibleName = "Compose attachment list"
    };
    private readonly HtmlEditor _editor = new();
    private readonly TextBox _plain = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = true, Visible = false,
        BorderStyle = BorderStyle.None, Font = new Font("Arial", 10f), AcceptsTab = true, AccessibleName = "Message body editor"
    };
    private readonly IReadOnlyList<SendingIdentity> _identities;
    private readonly ComposeSeed _seed;
    private readonly string? _dataFolder;
    private TableLayoutPanel? _attachmentRow;
    private bool _finishing;
    private bool _discard;

    public ComposeForm(IReadOnlyList<SendingIdentity> readyIdentities, ComposeSeed seed) : this(readyIdentities, seed, null) { }

    public ComposeForm(IReadOnlyList<SendingIdentity> readyIdentities, ComposeSeed seed, string? webDataFolder,
        MailDatabase? addressBook = null)
    {
        _addressBook = addressBook;
        if (readyIdentities.Count == 0) throw new InvalidOperationException("No Sending Identity has been configured and tested yet.");
        _identities = readyIdentities;
        _seed = seed;
        _dataFolder = webDataFolder;
        DraftId = seed.DraftId;
        Body = seed.Body;
        BodyHtml = seed.BodyHtml;
        Text = seed.Title;
        AccessibleName = "Compose message";
        ClientSize = new Size(820, 620);
        MinimumSize = new Size(600, 440);
        StartPosition = FormStartPosition.CenterParent;
        Icon = Dialog.WindowIcon();
        BackColor = SystemColors.Control;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

        // Items, not DataSource: a bound list stays empty until the control has a binding context, so selecting
        // the identity here would throw before the window ever opened.
        _from.Items.AddRange(readyIdentities.Select(identity => (object)Label(identity)).ToArray());
        var seeded = seed.IdentityId is null ? -1 : IndexOf(readyIdentities, seed.IdentityId);
        _from.SelectedIndex = seeded >= 0 ? seeded : Math.Max(0, readyIdentities.ToList().FindIndex(identity => identity.IsDefault));
        _to.Text = seed.To; _cc.Text = seed.Cc; _bcc.Text = seed.Bcc; _subject.Text = seed.Subject;
        foreach (var path in seed.AttachmentPaths ?? []) AddAttachment(path);

        // Bottom to top, so each docked band takes its place above the one before it and the page fills what
        // is left.
        var page = new BufferedPanel { Dock = DockStyle.Fill, BackColor = Color.White, BorderStyle = BorderStyle.Fixed3D };
        page.Controls.Add(_editor);
        page.Controls.Add(_plain);
        Controls.Add(page);
        Controls.Add(FormattingBar());
        Controls.Add(Headers());
        Controls.Add(Toolbar());
        var menu = Menu();
        Controls.Add(menu);
        MainMenuStrip = menu;
        _editor.ShortcutAction = ComposeShortcut;
        EnableAddressing();

        Shown += async (_, _) =>
        {
            // No runtime folder, as in a test harness, means the page stays plain text.
            if (_dataFolder is not null) await _editor.StartAsync(_dataFolder);
            if (_dataFolder is null || _editor.Failed)
            {
                _editor.Visible = false;
                _plain.Visible = true;
                _plain.Text = seed.Body;
            }
            else _editor.Load(seed.BodyHtml ?? TextAsHtml(seed.Body));
            if (_to.Text.Length == 0) _to.Focus();
            else if (_plain.Visible) _plain.Focus();
            else _editor.FocusBody();
        };
        FormClosing += OnClosing;
    }

    public string? DraftId { get; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Action? FindMessage { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<Task<bool>>? CommitMessage { get; set; }
    public bool ShouldQueue { get; private set; }
    public string IdentityId => _identities[Math.Max(0, _from.SelectedIndex)].Id;
    public IReadOnlyList<string> To => Addresses(_to.Text);
    public IReadOnlyList<string> Cc => Addresses(_cc.Text);
    public IReadOnlyList<string> Bcc => Addresses(_bcc.Text);
    public string Subject => _subject.Text.Trim();
    /// <summary>The words of the message, for a reader whose client shows only words.</summary>
    public string Body { get; private set; }
    /// <summary>The message as a page, or nothing when it was written plain.</summary>
    public string? BodyHtml { get; private set; }
    public IReadOnlyList<string> AttachmentPaths => _attachments.Items.Cast<ListViewItem>().Select(item => (string)item.Tag!).ToArray();

    // ---- building the window -------------------------------------------------------------------------

    private MenuStrip Menu()
    {
        var menu = new MenuStrip { Font = new Font("Tahoma", 8.25f), RenderMode = ToolStripRenderMode.System, AccessibleName = "Compose menu" };
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(Item("&Send Message", Keys.Alt | Keys.S, async () => await FinishAsync(queue: true)));
        file.DropDownItems.Add(Item("Sa&ve", Keys.Control | Keys.S, async () => await FinishAsync(queue: false)));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("&Close", Keys.None, Close));
        var edit = new ToolStripMenuItem("&Edit");
        edit.DropDownItems.Add(Item("&Find Text…", Keys.F4, FindBodyText));
        edit.DropDownItems.Add(Item("&Undo", Keys.None, () => Command("undo")));
        edit.DropDownItems.Add(Item("&Redo", Keys.None, () => Command("redo")));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Item("Cu&t", Keys.None, () => Command("cut")));
        edit.DropDownItems.Add(Item("&Copy", Keys.None, () => Command("copy")));
        edit.DropDownItems.Add(Item("&Paste", Keys.None, () => Command("paste")));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Item("Select &All", Keys.None, () => Command("selectAll")));
        var insert = new ToolStripMenuItem("&Insert");
        insert.DropDownItems.Add(Item("&File Attachment…", Keys.None, AddAttachments));
        insert.DropDownItems.Add(Item("&Signature", Keys.Control | Keys.Shift | Keys.S, InsertSignature));
        insert.DropDownItems.Add(Item("&Hyperlink…", Keys.None, InsertLink));
        var format = new ToolStripMenuItem("F&ormat");
        format.DropDownItems.Add(Item("&Bold", Keys.Control | Keys.B, () => Command("bold")));
        format.DropDownItems.Add(Item("&Italic", Keys.Control | Keys.I, () => Command("italic")));
        format.DropDownItems.Add(Item("&Underline", Keys.Control | Keys.U, () => Command("underline")));
        format.DropDownItems.Add(new ToolStripSeparator());
        format.DropDownItems.Add(Item("Bu&lleted List", Keys.None, () => Command("insertUnorderedList")));
        format.DropDownItems.Add(Item("&Numbered List", Keys.None, () => Command("insertOrderedList")));
        format.DropDownItems.Add(Item("Increase Inden&t", Keys.None, () => Command("indent")));
        format.DropDownItems.Add(Item("&Decrease Indent", Keys.None, () => Command("outdent")));
        var tools = new ToolStripMenuItem("&Tools");
        tools.DropDownItems.Add(Item("&Check Names", Keys.Control | Keys.K, CheckNames));
        tools.DropDownItems.Add(Item("&Spelling…", Keys.F7, async () => await CheckSpellingAsync()));
        tools.DropDownItems.Add(Item("Select &Recipients…", Keys.None, () => PickRecipients(_to)));
        tools.DropDownItems.Add(Item("&Address Book…", Keys.Control | Keys.Shift | Keys.B, OpenAddressBook));
        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(Item("&Mailkeep Help", Keys.F1, () => MailHelp.Show(this)));
        menu.Items.AddRange([file, edit, insert, format, tools, help]);
        return menu;
    }

    private ToolStrip Toolbar()
    {
        var toolbar = new ToolStrip
        {
            Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, ImageScalingSize = new Size(32, 32),
            RenderMode = ToolStripRenderMode.System, Font = new Font("Tahoma", 8.25f), Padding = new Padding(5, 1, 5, 1),
            AccessibleName = "Compose toolbar"
        };
        toolbar.Items.Add(Big("Send", 8, async () => await FinishAsync(queue: true)));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(Big("Cut", 18, () => Command("cut")));
        toolbar.Items.Add(Big("Copy", 19, () => Command("copy")));
        toolbar.Items.Add(Big("Paste", 20, () => Command("paste")));
        toolbar.Items.Add(Big("Undo", 9, () => Command("undo")));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(Big("Check Names", 27, CheckNames));
        toolbar.Items.Add(Big("Spelling", 21, async () => await CheckSpellingAsync()));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(Big("Attach", 26, AddAttachments));
        toolbar.Items.Add(Big("Insert Signature", 23, InsertSignature));
        return toolbar;
    }

    private Control Headers()
    {
        var headers = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Padding = new Padding(6, 6, 8, 2),
            BackColor = SystemColors.Control, AccessibleName = "Compose headers"
        };
        headers.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
        headers.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(headers, "From:", 22, _from, "From identity");
        AddRow(headers, "To:", 22, _to, "To recipients");
        AddRow(headers, "Cc:", 22, _cc, "Cc recipients");
        AddRow(headers, "Bcc:", 22, _bcc, "Bcc recipients");
        AddRow(headers, "Subject:", 23, _subject, "Subject");
        // The attachment row appears only once there is something attached, as it does in OE.
        _attachmentRow = headers;
        var row = headers.RowCount++;
        headers.RowStyles.Add(new RowStyle(SizeType.Absolute, _attachments.Items.Count > 0 ? 44 : 0));
        headers.Controls.Add(Caption("Attach:", 26), 0, row);
        headers.Controls.Add(_attachments, 1, row);
        _attachments.AccessibleName = "Compose attachment list";
        _attachments.Margin = new Padding(0, 2, 0, 2);
        _attachments.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) RemoveSelectedAttachments(); };
        var remove = new ContextMenuStrip();
        remove.Items.Add("Remove", null, (_, _) => RemoveSelectedAttachments());
        _attachments.ContextMenuStrip = remove;
        _attachments.SmallImageList = AttachmentIcons();
        return headers;
    }

    private ToolStrip FormattingBar()
    {
        var bar = new ToolStrip
        {
            Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, ImageScalingSize = new Size(16, 16),
            RenderMode = ToolStripRenderMode.System, Font = new Font("Tahoma", 8.25f), Padding = new Padding(4, 1, 4, 1),
            AccessibleName = "Formatting toolbar"
        };
        var fonts = new ToolStripComboBox { AutoSize = false, Width = 190, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Font" };
        fonts.Items.AddRange(["Arial", "Tahoma", "Verdana", "Times New Roman", "Georgia", "Courier New"]);
        fonts.SelectedIndex = 0;
        fonts.SelectedIndexChanged += (_, _) => { Command("fontName", fonts.Text); _editor.FocusBody(); };
        var sizes = new ToolStripComboBox { AutoSize = false, Width = 44, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Font size" };
        sizes.Items.AddRange(["8", "10", "12", "14", "18", "24"]);
        sizes.SelectedIndex = 1;
        // execCommand sizes run 1-7, not points; these are the browser's steps nearest OE's list.
        sizes.SelectedIndexChanged += (_, _) => { Command("fontSize", (sizes.SelectedIndex + 1).ToString()); _editor.FocusBody(); };
        bar.Items.Add(fonts);
        bar.Items.Add(sizes);
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(Small("Bold", 37, () => Command("bold")));
        bar.Items.Add(Small("Italic", 38, () => Command("italic")));
        bar.Items.Add(Small("Underline", 39, () => Command("underline")));
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(Small("Numbered list", 45, () => Command("insertOrderedList")));
        bar.Items.Add(Small("Bulleted list", 46, () => Command("insertUnorderedList")));
        bar.Items.Add(Small("Decrease indent", 47, () => Command("outdent")));
        bar.Items.Add(Small("Increase indent", 48, () => Command("indent")));
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(Small("Align left", 49, () => Command("justifyLeft")));
        bar.Items.Add(Small("Centre", 50, () => Command("justifyCenter")));
        bar.Items.Add(Small("Align right", 51, () => Command("justifyRight")));
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(Small("Insert hyperlink", 56, InsertLink));
        return bar;
    }

    private static ToolStripButton Big(string text, int icon, Action click)
    {
        var button = new ToolStripButton(text, OeIcons.At32(icon) ?? ToolbarFallback(text, 32))
        {
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText, TextImageRelation = TextImageRelation.ImageAboveText,
            AccessibleName = text, ToolTipText = text, AutoSize = true, Padding = new Padding(14, 0, 14, 0), Margin = new Padding(2, 0, 2, 0)
        };
        button.Click += (_, _) => click();
        return button;
    }

    private static ToolStripButton Small(string text, int icon, Action click)
    {
        var button = new ToolStripButton(text, OeIcons.At16(icon) ?? ToolbarFallback(text, 16))
        {
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            AccessibleName = text, ToolTipText = text
        };
        button.Click += (_, _) => click();
        return button;
    }

    private static Bitmap ToolbarFallback(string text, int size)
    {
        var image = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(Color.Transparent);
        var glyph = text switch
        {
            "Send" => "✉", "Cut" => "✂", "Copy" => "▣", "Paste" => "▤", "Undo" => "↶",
            "Check Names" => "✓", "Spelling" => "ABC", "Insert Signature" => "✎",
            "Bold" => "B", "Italic" => "I", "Underline" => "U", "Insert hyperlink" => "↗",
            "Decrease indent" => "←", "Increase indent" => "→", _ => ""
        };
        if (text == "Attach")
        {
            using var clip = new Pen(Color.DimGray, 2);
            graphics.DrawArc(clip, size * .25f, size * .1f, size * .5f, size * .7f, 180, 180);
            graphics.DrawArc(clip, size * .25f, size * .4f, size * .35f, size * .45f, 0, 180);
            graphics.DrawLine(clip, size * .25f, size * .45f, size * .25f, size * .6f);
            graphics.DrawLine(clip, size * .6f, size * .3f, size * .6f, size * .65f);
        }
        else if (glyph.Length > 0)
        {
            using var font = new Font("Segoe UI Symbol", size * (text == "Spelling" ? .32f : .7f),
                text == "Italic" ? FontStyle.Italic : text == "Underline" ? FontStyle.Underline : FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var ink = new SolidBrush(size == 32 ? Color.SteelBlue : Color.Black);
            graphics.DrawString(glyph, font, ink, new RectangleF(0, 0, size, size), format);
        }
        else
        {
            using var line = new Pen(Color.DimGray);
            for (var y = 3; y < size - 2; y += 4)
            {
                var inset = y == 7 ? 3 : 0;
                var x = text == "Align right" ? 2 + inset * 2 : text == "Centre" ? 2 + inset : 2;
                graphics.DrawLine(line, x, y, size - 3 - (text == "Align left" ? inset * 2 : inset), y);
                if (text.Contains("list")) graphics.FillRectangle(Brushes.Black, 0, y, 2, 2);
            }
        }
        return image;
    }

    private static ToolStripMenuItem Item(string text, Keys shortcut, Action click)
    {
        var item = new ToolStripMenuItem(text) { ShortcutKeys = shortcut, AccessibleName = text.Replace("&", "") };
        item.Click += (_, _) => click();
        return item;
    }

    private void AddRow(TableLayoutPanel layout, string label, int height, Control control, string accessibleName)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height + 4));
        // The card icon marks the address rows only; From and Subject are not addresses to look up.
        Control caption;
        if (control is TextBox field && _addressBook is not null && label is "To:" or "Cc:" or "Bcc:")
        {
            // With an address book, the address captions are buttons, as in OE: each opens Select Recipients.
            var button = new Button
            {
                FlatStyle = FlatStyle.Flat,
                Text = label, Image = OeIcons.At16(22), ImageAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Tahoma", 8.25f), Dock = DockStyle.Fill, Margin = new Padding(0, 1, 6, 1),
                Padding = Padding.Empty, UseVisualStyleBackColor = true, AccessibleName = label + " Select Recipients", TabStop = false
            };
            button.FlatAppearance.BorderSize = 0;
            _captions[field] = button;
            caption = button;
        }
        else caption = Caption(label, label is "To:" or "Cc:" or "Bcc:" ? 22 : -1);
        layout.Controls.Add(caption, 0, row);
        control.Dock = control is TextBox ? DockStyle.None : DockStyle.Fill;
        if (control is TextBox) control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        control.Margin = new Padding(0, 2, 0, 2);
        control.AccessibleName = accessibleName;
        layout.Controls.Add(control, 1, row);
    }

    /// <summary>OE's address captions: the small card icon and label, side by side.</summary>
    private static Control Caption(string text, int icon)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 3, 0, 0) };
        var image = icon >= 0 ? OeIcons.At16(icon) : null;
        panel.Controls.Add(new PictureBox { Image = image, Size = new Size(16, 16), Margin = new Padding(0, 1, 4, 0), SizeMode = PictureBoxSizeMode.CenterImage });
        panel.Controls.Add(new Label { Text = text, AutoSize = true, Font = new Font("Tahoma", 8.25f), Margin = new Padding(0, 2, 0, 0) });
        return panel;
    }

    private static TextBox Field()
    {
        var field = new TextBox { Font = new Font("Tahoma", 8.25f), BorderStyle = BorderStyle.None, AutoSize = false };
        void FitHeight() => field.Height = field.Font.Height + (int)Math.Round(4 * field.DeviceDpi / 96.0);
        field.FontChanged += (_, _) => FitHeight();
        field.DpiChangedAfterParent += (_, _) => FitHeight();
        FitHeight();
        return field;
    }

    private static Bitmap Blank(int size) => new(size, size);

    private static ImageList AttachmentIcons()
    {
        var list = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
        list.Images.Add("file", OeIcons.At16(26) ?? Blank(16));
        return list;
    }

    // ---- doing things -----------------------------------------------------------------------------------

    private Action? ComposeShortcut(Keys keys) => keys switch
    {
        Keys.Alt | Keys.S or Keys.Control | Keys.Enter => () => { _ = FinishAsync(queue: true); },
        Keys.Control | Keys.S => () => { _ = FinishAsync(queue: false); },
        Keys.Escape when _suggestions.Visible => HideSuggestions,
        Keys.Escape => Close,
        Keys.Control | Keys.Shift | Keys.B when _addressBook is not null => OpenAddressBook,
        Keys.Control | Keys.Shift | Keys.F => FindMessage,
        Keys.F4 or Keys.F3 => FindBodyText,
        Keys.F1 => () => MailHelp.Show(this),
        Keys.F7 => () => { _ = CheckSpellingAsync(); },
        Keys.Control | Keys.K or Keys.Alt | Keys.K => CheckNames,
        Keys.Control | Keys.Shift | Keys.S => InsertSignature,
        Keys.Tab when _to.Focused => TabAfterTo,
        Keys.Tab when _subject.Focused => FocusBody,
        Keys.Tab when _editor.ContainsFocus => () => _editor.Command("insertText", "\t"),
        _ => null
    };

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (ComposeShortcut(keys) is { } action) { action(); return true; }
        return base.ProcessCmdKey(ref message, keys);
    }

    private void Command(string command, string? value = null)
    {
        if (_plain.Visible)
        {
            switch (command)
            {
                case "undo": _plain.Undo(); break;
                case "cut": _plain.Cut(); break;
                case "copy": _plain.Copy(); break;
                case "paste": _plain.Paste(); break;
                case "selectAll": _plain.SelectAll(); break;
            }
            return;
        }
        _editor.Command(command, value);
    }

    private void InsertLink()
    {
        if (_plain.Visible) return;
        var address = Prompt("Insert Hyperlink", "Address:", "https://");
        if (string.IsNullOrWhiteSpace(address) || address.Trim() == "https://") return;
        _editor.Command("createLink", address.Trim());
        _editor.FocusBody();
    }

    private string? Prompt(string title, string label, string initial)
    {
        using var dialog = new Form
        {
            Text = title, ClientSize = new Size(380, 96), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = new Font("Tahoma", 8.25f)
        };
        var caption = new Label { Text = label, AutoSize = true, Left = 10, Top = 12 };
        var box = new TextBox { Left = 70, Top = 9, Width = 298, Text = initial, AccessibleName = label };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 212, Top = 58, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 293, Top = 58, Width = 75 };
        dialog.Controls.AddRange([caption, box, ok, cancel]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        box.SelectAll();
        return dialog.ShowDialog(this) == DialogResult.OK ? box.Text : null;
    }

    private void AddAttachments()
    {
        using var dialog = new OpenFileDialog { Multiselect = true, CheckFileExists = true, Title = "Insert Attachment" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        foreach (var path in dialog.FileNames)
            if (!AttachmentPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) AddAttachment(path);
    }

    private void AddAttachment(string path)
    {
        var info = new FileInfo(path);
        var size = info.Exists ? (info.Length < 1024 ? $"{info.Length} B" : $"{Math.Ceiling(info.Length / 1024d):N0} KB") : "";
        _attachments.Items.Add(new ListViewItem($"{info.Name} ({size})", "file") { Tag = path, ToolTipText = path });
        ShowAttachmentRow();
    }

    private void RemoveSelectedAttachments()
    {
        foreach (var item in _attachments.SelectedItems.Cast<ListViewItem>().ToArray()) _attachments.Items.Remove(item);
        ShowAttachmentRow();
    }

    private void ShowAttachmentRow()
    {
        if (_attachmentRow is null) return;
        _attachmentRow.RowStyles[^1].Height = _attachments.Items.Count > 0 ? 44 : 0;
    }

    private async Task FinishAsync(bool queue)
    {
        if (_finishing) return;
        var recipients = To.Concat(Cc).Concat(Bcc).ToArray();
        if (queue && recipients.Length == 0)
        {
            MessageBox.Show(this, "Enter at least one recipient.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (queue && recipients.FirstOrDefault(address => !MimeKit.MailboxAddress.TryParse(address, out _)) is { } invalid)
        {
            MessageBox.Show(this, $"'{invalid}' is not a valid email address.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (queue && Subject.Length == 0
            && MessageBox.Show(this, "Send this message without a subject?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _finishing = true;
        try
        {
            if (_plain.Visible) { Body = _plain.Text; BodyHtml = null; }
            else
            {
                BodyHtml = await _editor.GetHtmlAsync();
                Body = MailText.FromHtml(BodyHtml);
            }
            ShouldQueue = queue;
            if (CommitMessage is not null)
            {
                Enabled = false;
                if (!await CommitMessage()) return;
            }
            DialogResult = DialogResult.OK;
            if (!Modal) Close();
        }
        finally { _finishing = false; if (!IsDisposed) Enabled = true; }
    }

    /// <summary>Closing with the window's close box asks what to do with the message, as OE does, rather than
    /// losing it: keep it as a draft, throw it away, or carry on writing.</summary>
    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK || _discard) return;
        e.Cancel = true;
        if (_finishing) return;
        var empty = _to.Text.Length == 0 && _subject.Text.Length == 0 && AttachmentPaths.Count == 0
            && (_plain.Visible ? _plain.Text.Trim().Length == 0 : await _editor.IsEmptyAsync());
        var answer = empty && _seed.DraftId is null ? DialogResult.No
            : MessageBox.Show(this, "Save changes to this message?", Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        // Acted on once this closing has finished being refused. Done inside it, a close asked for here was
        // undone on the way out — the closing it ran in is the one cancelled above — so No left the window open,
        // as did Yes whenever saving needed no wait.
        if (answer is DialogResult.Yes or DialogResult.No) BeginInvoke(async () =>
        {
            if (answer == DialogResult.Yes) { await FinishAsync(queue: false); return; }
            _discard = true;
            DialogResult = DialogResult.Cancel;
            if (!Modal) Close();
        });
    }

    // ---- seeds ---------------------------------------------------------------------------------------------

    public static ComposeSeed Reply(MessageContent message, bool all, IReadOnlyList<string> ownAddresses, string? identityId = null)
    {
        static string Address(string value) => MimeKit.MailboxAddress.TryParse(value, out var mailbox) ? mailbox.Address : value;
        var excluded = ownAddresses.Select(Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        excluded.Add(Address(message.Summary.Sender));
        var targets = message.ReplyTo.Count > 0 ? message.ReplyTo : [message.Summary.Sender];
        foreach (var target in targets) excluded.Add(Address(target));
        var copies = all ? message.Summary.Recipients.Where(value => excluded.Add(Address(value))) : [];
        return new ComposeSeed("Re: " + Strip(message.Summary.Subject, "Re:"), IdentityId: identityId,
            To: string.Join("; ", targets.DistinctBy(Address, StringComparer.OrdinalIgnoreCase)), Cc: string.Join("; ", copies),
            Subject: "Re: " + Strip(message.Summary.Subject, "Re:"),
            Body: Environment.NewLine + Environment.NewLine + Quote(message), BodyHtml: QuoteHtml(message));
    }

    public static ComposeSeed Forward(MessageContent message) => new(
        "Fw: " + Strip(message.Summary.Subject, "Fw:"),
        Subject: "Fw: " + Strip(message.Summary.Subject, "Fw:"),
        Body: Environment.NewLine + Environment.NewLine + Quote(message), BodyHtml: QuoteHtml(message));

    public static ComposeSeed Edit(OutgoingMessage draft) => new("Draft - " + (draft.Subject.Length == 0 ? "no subject" : draft.Subject),
        draft.SendingIdentityId, string.Join("; ", draft.To), string.Join("; ", draft.Cc), string.Join("; ", draft.Bcc),
        draft.Subject, draft.BodyText, draft.Id, BodyHtml: draft.BodyHtml);

    private static int IndexOf(IReadOnlyList<SendingIdentity> identities, string id)
    {
        for (var index = 0; index < identities.Count; index++)
            if (identities[index].Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return index;
        return -1;
    }

    private static string Label(SendingIdentity identity)
        => string.IsNullOrWhiteSpace(identity.DisplayName) ? identity.Email : $"{identity.DisplayName} <{identity.Email}>";

    private static string Strip(string subject, string prefix)
    {
        var value = subject.Trim();
        while (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) value = value[prefix.Length..].TrimStart();
        return value;
    }

    private static string Sender(MessageContent message)
        => string.IsNullOrWhiteSpace(message.Summary.SenderName) ? message.Summary.Sender : $"{message.Summary.SenderName} <{message.Summary.Sender}>";

    private static string Quote(MessageContent message) => string.Join(Environment.NewLine,
        "----- Original Message -----",
        "From: " + Sender(message),
        "Sent: " + (message.Summary.Date?.ToLocalTime().ToString("f") ?? "unknown"),
        "To: " + string.Join("; ", message.Summary.Recipients),
        "Subject: " + message.Summary.Subject,
        "",
        message.BodyText);

    /// <summary>The letter being answered, beneath room to write and the header OE puts above a quoted
    /// message, kept as the page it arrived as. A letter that came as text alone is shown as text.</summary>
    private static string QuoteHtml(MessageContent message)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        var original = string.IsNullOrWhiteSpace(message.BodyHtml)
            ? "<div style=\"white-space:pre-wrap\">" + E(message.BodyText) + "</div>"
            : HtmlEditor.Fragment(message.BodyHtml);
        return "<div><br></div><div><br></div>"
            + "<div style=\"font-family:Tahoma,sans-serif;font-size:8pt\"><b>----- Original Message -----</b><br>"
            + "<b>From:</b> " + E(Sender(message)) + "<br>"
            + "<b>Sent:</b> " + E(message.Summary.Date?.ToLocalTime().ToString("f") ?? "unknown") + "<br>"
            + "<b>To:</b> " + E(string.Join("; ", message.Summary.Recipients)) + "<br>"
            + "<b>Subject:</b> " + E(message.Summary.Subject) + "</div><br>"
            + original;
    }

    private static string TextAsHtml(string text)
        => text.Length == 0 ? "" : WebUtility.HtmlEncode(text).Replace("\r\n", "\n").Replace("\n", "<br>");

    // Quote-aware: a name with a comma in it, as an address book entry may have, is one recipient.
    private static string[] Addresses(string value) => Recipients.Split(value);
}
