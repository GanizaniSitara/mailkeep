using Mailkeep.Core;

namespace Mailkeep.WinForms;

/// <summary>A message opened in its own window, the way double-clicking one in Outlook Express always has:
/// the header block, the body with room to read it, and the replies to hand. Several can be open at once, and
/// each keeps the message it was opened with — so replying from one window never answers whatever happens to
/// be selected in the list behind it.</summary>
internal sealed class MessageWindow : Form
{
    private readonly Func<Keys, Action?>? _shortcut;
    private MessageContent _content;
    internal MessageContent CurrentContent => _content;
    private readonly ToolStrip _toolbar;
    private readonly ToolStripButton _openHtml;
    private readonly Label _subjectLabel;
    private readonly Control _fromLine;
    private readonly Control _dateLine;
    private readonly AttachmentBar _attachments;
    private readonly Panel _bodyPanel;
    private readonly RemoteImageBar _remoteImages;
    private readonly string _browserDataFolder;
    private readonly RemoteImagePermissions? _imagePermissions;
    private Task? _browserStart;
    private int _renderGeneration;
    private ToolStripButton? _previous;
    private ToolStripButton? _next;
    private bool _canPrevious, _canNext, _navigating;
    private HtmlView? _htmlBody;
    private Action? _refreshImages;
    internal void RefreshImages() => _refreshImages?.Invoke();
    private readonly RichTextBox _body = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White,
        ScrollBars = RichTextBoxScrollBars.Vertical, WordWrap = true, Font = new Font("Tahoma", 9f),
        AccessibleName = "Message body"
    };

    public MessageWindow(MessageContent content, Func<MessageContent, bool, Task> reply,
        Func<MessageContent, Task> forward, Action<MessageContent> print, Action<MessageContent> openHtml,
        string browserDataFolder, Func<long, int, string> extractAttachment, RemoteImagePermissions? imagePermissions = null,
        Func<Keys, Action?>? shortcut = null)
    {
        _shortcut = shortcut;
        _content = content;
        _browserDataFolder = browserDataFolder;
        _imagePermissions = imagePermissions;
        var summary = content.Summary;
        Text = string.IsNullOrWhiteSpace(summary.Subject) ? "(no subject)" : summary.Subject;
        AccessibleName = "Message window";
        ClientSize = new Size(820, 640);
        MinimumSize = new Size(520, 360);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Tahoma", 8.25f);
        BackColor = SystemColors.Control;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        KeyPreview = true;
        if (Environment.ProcessPath is { } executablePath) Icon = Icon.ExtractAssociatedIcon(executablePath);

        var toolbar = _toolbar = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden, ImageScalingSize = new Size(24, 24), Dock = DockStyle.Top,
            Padding = new Padding(4, 4, 4, 4), Renderer = new ToolStripProfessionalRenderer { RoundedEdges = false },
            AccessibleName = "Message window toolbar"
        };
        toolbar.Items.Add(CommandAsync("Reply", MainForm.ToolbarGlyphImage(MainForm.Glyph.Reply), () => reply(_content, false)));
        toolbar.Items.Add(CommandAsync("Reply All", MainForm.ToolbarGlyphImage(MainForm.Glyph.ReplyAll), () => reply(_content, true)));
        toolbar.Items.Add(CommandAsync("Forward", MainForm.ToolbarGlyphImage(MainForm.Glyph.Forward), () => forward(_content)));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(Command("Print", MainForm.ToolbarGlyphImage(MainForm.Glyph.Print), () => print(_content)));
        var html = _openHtml = Command("Open HTML", MainForm.ToolbarGlyphImage(MainForm.Glyph.Html), () => openHtml(_content));
        html.Enabled = !string.IsNullOrWhiteSpace(content.BodyHtml);
        toolbar.Items.Add(html);

        var reading = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill, RowCount = 6, ColumnCount = 1, Padding = new Padding(8, 6, 8, 4), BackColor = Color.White
        };
        reading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        reading.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        reading.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        reading.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        reading.RowStyles.Add(new RowStyle(SizeType.Absolute, 9));
        reading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        reading.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));

        // The subject with, at the far right of the same line, the paperclip for a message with files.
        var attachments = _attachments = new AttachmentBar(extractAttachment) { Anchor = AnchorStyles.Right };
        attachments.SetMessage(content);
        var subjectLine = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        subjectLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        subjectLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        subjectLine.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _subjectLabel = new Label
        {
            Text = Text, Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Tahoma", 9.75f, FontStyle.Bold), ForeColor = Color.FromArgb(0, 70, 180),
            AccessibleName = "Message subject"
        };
        subjectLine.Controls.Add(_subjectLabel, 0, 0);
        subjectLine.Controls.Add(attachments, 1, 0);
        reading.Controls.Add(subjectLine, 0, 0);

        var sender = string.IsNullOrWhiteSpace(summary.SenderName) ? summary.Sender : $"{summary.SenderName} <{summary.Sender}>";
        _fromLine = Pair("From:", sender, "To:", string.Join(", ", summary.Recipients));
        _dateLine = Pair("Date:", summary.Date?.ToLocalTime().ToString("f") ?? "Date unavailable", null, null);
        reading.Controls.Add(_fromLine, 0, 1);
        reading.Controls.Add(_dateLine, 0, 2);

        var ruleHost = new BufferedPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        ruleHost.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Color.FromArgb(210, 210, 210), Margin = Padding.Empty });
        reading.Controls.Add(ruleHost, 0, 3);

        _body.Text = content.BodyText;
        _body.LinkClicked += (_, e) => MainForm.OpenLink(e.LinkText);
        var bodyPanel = _bodyPanel = new BufferedPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        bodyPanel.Controls.Add(_body);
        var remoteImages = _remoteImages = new RemoteImageBar();
        var bodyHost = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, RowCount = 2, ColumnCount = 1 };
        bodyHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bodyHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        bodyHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        remoteImages.VisibleChanged += (_, _) => bodyHost.RowStyles[0].Height = remoteImages.Visible ? 32 : 0;
        bodyHost.Controls.Add(remoteImages, 0, 0);
        bodyHost.Controls.Add(bodyPanel, 0, 1);
        reading.Controls.Add(bodyHost, 0, 4);

        Controls.Add(reading);
        Controls.Add(toolbar);
        Shown += async (_, _) =>
        {
            MainForm.ApplyReadingLeading(_body);
            _refreshImages = () => { _ = RenderContentAsync(); };
            if (imagePermissions is not null)
            {
                EventHandler changed = (_, _) => RefreshImages();
                imagePermissions.Changed += changed;
                FormClosed += (_, _) => imagePermissions.Changed -= changed;
            }
            await RenderContentAsync();
        };
    }

    internal Task SetContentAsync(MessageContent content)
    {
        _content = content;
        var summary = content.Summary;
        Text = string.IsNullOrWhiteSpace(summary.Subject) ? "(no subject)" : summary.Subject;
        _subjectLabel.Text = Text;
        _fromLine.Controls[1].Text = string.IsNullOrWhiteSpace(summary.SenderName) ? summary.Sender : $"{summary.SenderName} <{summary.Sender}>";
        _fromLine.Controls[3].Text = string.Join(", ", summary.Recipients);
        _dateLine.Controls[1].Text = summary.Date?.ToLocalTime().ToString("f") ?? "Date unavailable";
        _attachments.SetMessage(content);
        _openHtml.Enabled = !string.IsNullOrWhiteSpace(content.BodyHtml);
        _body.Text = content.BodyText;
        // Remote pictures must not keep Previous/Next disabled while their network requests finish.
        _ = RenderContentAsync();
        return Task.CompletedTask;
    }

    private async Task RenderContentAsync(bool includeAsk = false)
    {
        var generation = ++_renderGeneration;
        var content = _content;
        _remoteImages.ShowFor([], null, null, 0);
        _body.Visible = true;
        if (_htmlBody is not null) _htmlBody.Visible = false;
        if (string.IsNullOrWhiteSpace(content.BodyHtml)) return;
        if (_htmlBody is null)
        {
            _htmlBody = new HtmlView(MainForm.OpenLink) { ShortcutAction = ReaderShortcut };
            _bodyPanel.Controls.Add(_htmlBody);
            _browserStart = _htmlBody.StartAsync(_browserDataFolder);
        }
        await _browserStart!;
        if (IsDisposed || Disposing || generation != _renderGeneration || _htmlBody.Failed) return;
        _body.Visible = false;
        _htmlBody.Visible = true;
        _htmlBody.Show(MainForm.SafeHtml(content.BodyHtml));
        var origins = RemoteImages.Origins(content.BodyHtml);
        IReadOnlyDictionary<string, string> policies;
        try { policies = await Task.Run(() => _imagePermissions?.Policies() ?? new Dictionary<string, string>()); }
        catch (Exception) { policies = new Dictionary<string, string>(); }
        if (IsDisposed || Disposing || generation != _renderGeneration) return;
        var permitted = origins.Where(origin => policies.GetValueOrDefault(origin, RemoteImagePermissions.Ask) == RemoteImagePermissions.Allow
            || includeAsk && policies.GetValueOrDefault(origin, RemoteImagePermissions.Ask) == RemoteImagePermissions.Ask).ToArray();
        var allowed = policies.Where(pair => pair.Value == RemoteImagePermissions.Allow).Select(pair => pair.Key).Concat(permitted).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var ask = origins.Any(origin => policies.GetValueOrDefault(origin, RemoteImagePermissions.Ask) == RemoteImagePermissions.Ask);
        var neverCount = origins.Count(origin => policies.GetValueOrDefault(origin) == RemoteImagePermissions.Never);
        _remoteImages.ShowFor(origins, ask && !includeAsk ? () => { _ = RenderContentAsync(true); } : null,
            _imagePermissions is null ? null : () => ImageSitesForm.EditMessage(this, _imagePermissions, content.BodyHtml), neverCount);
        if (permitted.Length == 0) return;
        var progress = new Progress<string>(pictures =>
        {
            if (!IsDisposed && generation == _renderGeneration) _htmlBody.UpdatePictures(MainForm.SafeHtml(pictures));
        });
        var inlined = await RemoteImageFetcher.InlineAsync(content.BodyHtml, allowed, progress: progress);
        if (!IsDisposed && generation == _renderGeneration) _htmlBody.UpdatePictures(MainForm.SafeHtml(inlined));
    }

    internal void UpdateNavigation(bool previous, bool next)
    {
        _canPrevious = previous;
        _canNext = next;
        if (_previous is not null) _previous.Enabled = previous && !_navigating;
        if (_next is not null) _next.Enabled = next && !_navigating;
    }

    internal void SetNavigation(Func<int, Task> navigate, bool previous, bool next)
    {
        async Task Move(int direction)
        {
            if (_navigating) return;
            _navigating = true;
            UpdateNavigation(_canPrevious, _canNext);
            try { await navigate(direction); }
            finally
            {
                _navigating = false;
                if (!IsDisposed) UpdateNavigation(_canPrevious, _canNext);
            }
        }
        _previous = CommandAsync("Previous", NavigationArrow(false), () => Move(-1));
        _next = CommandAsync("Next", NavigationArrow(true), () => Move(1));
        // Right-aligned items are laid out from the right edge inward.
        foreach (var button in new[] { _next, _previous })
        {
            button.Alignment = ToolStripItemAlignment.Right;
            button.Padding = new Padding(6, 0, 6, 0);
            button.Overflow = ToolStripItemOverflow.Never;
            _toolbar.Items.Add(button);
        }
        UpdateNavigation(previous, next);
    }

    private static Image NavigationArrow(bool right)
    {
        var image = new Bitmap(24, 24);
        using var graphics = Graphics.FromImage(image);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb(30, 145, 45));
        Point[] points = [new(3, 8), new(13, 8), new(13, 3), new(22, 12), new(13, 21), new(13, 16), new(3, 16)];
        if (!right) points = points.Select(point => new Point(24 - point.X, point.Y)).ToArray();
        graphics.FillPolygon(brush, points);
        return image;
    }

    private Action? ReaderShortcut(Keys keys) => keys switch
    {
        Keys.Escape => Close,
        Keys.F1 => () => MailHelp.Show(this),
        Keys.F4 or Keys.F3 => () => MessageTextFind.Show(this, _body, _body.Visible ? null : _htmlBody?.CoreWebView2),
        Keys.Alt | Keys.Enter => () => new MessagePropertiesForm(_content).Show(this),
        _ => _shortcut?.Invoke(keys)
    };

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (ReaderShortcut(keys) is { } action) { action(); return true; }
        return base.ProcessCmdKey(ref message, keys);
    }

    /// <summary>Distinct names on purpose: an expression-bodied `() => _ = click()` returns a Task, so an
    /// Action/Func&lt;Task&gt; overload pair binds to itself and recurses until the stack goes.</summary>
    private static ToolStripButton CommandAsync(string text, Image image, Func<Task> click)
        => Command(text, image, () => { _ = click(); });

    private static ToolStripButton Command(string text, Image image, Action click)
    {
        var button = new ToolStripButton(text, image)
        {
            TextImageRelation = TextImageRelation.ImageAboveText,
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            Padding = new Padding(16, 0, 16, 0),
            Margin = new Padding(2, 0, 2, 0),
            AccessibleName = text
        };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>One or two caption/value pairs on a line, matching the reading pane's From/To row.</summary>
    private static Control Pair(string caption, string value, string? secondCaption, string? secondValue)
    {
        var line = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = Padding.Empty };
        line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, secondCaption is null ? 100 : 46));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, secondCaption is null ? 0 : 54));
        line.Controls.Add(Caption(caption), 0, 0);
        line.Controls.Add(Value(value), 1, 0);
        if (secondCaption is not null)
        {
            line.Controls.Add(Caption(secondCaption), 2, 0);
            line.Controls.Add(Value(secondValue ?? ""), 3, 0);
        }
        return line;
    }

    private static Label Caption(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Tahoma", 8.25f, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 3, 4, 0)
    };

    private static Label Value(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft
    };
}
