namespace Mailkeep.WinForms;

internal sealed class ImageSitesForm : Form
{
    private readonly Dictionary<string, RadioButton[]> _choices = new(StringComparer.OrdinalIgnoreCase);
    private bool _fitting, _shown;
    private string? _monitor;
    private Rectangle _workArea;
    private readonly Func<int> _contentHeight;
    public IReadOnlyDictionary<string, string> Choices => _choices.ToDictionary(pair => pair.Key, pair => pair.Value.Single(choice => choice.Checked).Text, StringComparer.OrdinalIgnoreCase);

    public ImageSitesForm(RemoteImagePermissions permissions, IEnumerable<string>? messageSites = null)
        : this(permissions, messageSites, null) { }

    public ImageSitesForm(RemoteImagePermissions permissions, IEnumerable<string>? messageSites, string? messageHtml)
    {
        Text = "Image domain permissions";
        MinimumSize = new Size(620, 180);
        StartPosition = FormStartPosition.Manual; MinimizeBox = false; MaximizeBox = false;
        var policies = permissions.Policies();
        var dnsSuggestions = permissions.DnsSuggestionsEnabled;
        var sites = (messageSites ?? policies.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var urls = RemoteImages.Urls(messageHtml ?? "");
        var images = RemoteImages.Contexts(messageHtml ?? "");
        string? DnsMatch(string site) => dnsSuggestions ? TrackerSuggestions.Match(site) : null;
        var noDnsMatch = dnsSuggestions ? "DNS block list: no match." : "DNS block-list suggestions disabled in Options.";
        var reasons = sites.ToDictionary(site => site, site =>
        {
            var findings = new List<string>();
            if (DnsMatch(site) is { } domain) findings.Add("Recommended DNS block list: " + domain + " (Block List Project).");
            foreach (var url in urls)
                if (RemoteImages.TryOrigin(url, out var origin) && origin.Equals(site, StringComparison.OrdinalIgnoreCase)
                    && TrackerSuggestions.ImageReason(url, images) is { } reason && !findings.Contains(reason)) findings.Add(reason);
            return string.Join("\r\n", findings);
        }, StringComparer.OrdinalIgnoreCase);
        var explanation = new Label { Dock = DockStyle.Top, Height = 76, Padding = new Padding(8), AccessibleName = "Image site scope",
            Text = (messageSites is null ? "All saved image domains: blocked, allowed and Ask." : "Only image domains in this message.") + " Permissions apply to all mail.\r\n"
                + "Always allow / Never allow are saved for future emails. Ask waits for Load pictures once; reopening the email asks again.\r\n"
                + "Choose a permission for each domain, then Save permissions. Cancel keeps the current permissions. Recommendations are suggestions.\r\n"
                + (dnsSuggestions ? "DNS suggestions: Block List Project tracking and analytics list, bundled 5 October 2026." : "DNS block-list suggestions are disabled in Options.") };
        var list = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 4, 0) };
        var scroll = new Panel { Dock = DockStyle.Fill, AccessibleName = "Image site list viewport" };
        var scrollbar = new VScrollBar { Dock = DockStyle.Right, Width = 28, SmallChange = 40, Visible = false, AccessibleName = "Scroll image sites" };
        list.Controls.Add(scroll); list.Controls.Add(scrollbar);
        var table = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, AccessibleName = "Image sites" };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var site in sites)
        {
            var reason = reasons[site];
            var saved = policies.TryGetValue(site, out var policy);
            var selected = policies.GetValueOrDefault(site, reason.Length == 0 ? RemoteImagePermissions.Ask : RemoteImagePermissions.Never);
            var row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var domain = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2, Padding = new Padding(4), Margin = new Padding(0, 0, 0, 4), BorderStyle = BorderStyle.FixedSingle,
                AccessibleName = "Image domain " + site };
            domain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
            domain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
            var details = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = new Padding(0, 2, 8, 4) };
            details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var siteLabel = new Label { Text = new Uri(site).Authority, Dock = DockStyle.Fill,
                AutoSize = true, MaximumSize = new Size(320, 0), AccessibleName = site, Font = new Font(Font, FontStyle.Bold) };
            domain.Controls.Add(siteLabel, 0, 0); domain.SetColumnSpan(siteLabel, 2);
            var classification = new Label { Text = saved ? "Current permission: " + policy + " (saved).\r\n" + (policy == RemoteImagePermissions.Never ? "Images are blocked." : policy == RemoteImagePermissions.Allow ? "Images load automatically." : "Images wait for Load pictures once.") : "Current permission: Ask (default).\r\nNo saved permission for this domain.",
                Dock = DockStyle.Fill, AutoSize = true, MaximumSize = new Size(320, 0), ForeColor = PermissionColor(saved ? policy! : RemoteImagePermissions.Ask), AccessibleName = "Classification for " + site };
            var siteUrls = urls.Where(url => RemoteImages.TryOrigin(url, out var origin) && origin.Equals(site, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToArray();
            var siteImages = images.Where(image => siteUrls.Contains(image.Url)).ToArray();
            if (siteImages.Length > 0)
            {
                var context = new Label { Text = string.Join("\r\n", siteUrls.Select(url => TrackerSuggestions.ImageSummary(url, images))),
                    Dock = DockStyle.Fill, AutoSize = true, MaximumSize = new Size(320, 0), AccessibleName = "Image context for " + site };
                details.Controls.Add(context, 0, 1);
                details.SizeChanged += (_, _) => context.MaximumSize = new Size(Math.Max(1, details.ClientSize.Width - details.Padding.Horizontal - 6), 0);
            }
            if (siteUrls.Length > 0)
            {
                var urlBox = new TextBox { Text = string.Join("\r\n", siteUrls), Multiline = true, ReadOnly = true, WordWrap = true,
                    ScrollBars = ScrollBars.None, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control,
                    Dock = DockStyle.Top, Visible = false, AccessibleName = "Full image URLs from " + site };
                void SizeUrl() => urlBox.Height = (urlBox.GetLineFromCharIndex(Math.Max(0, urlBox.TextLength - 1)) + 1) * urlBox.Font.Height + 4;
                urlBox.Resize += (_, _) => SizeUrl();
                urlBox.HandleCreated += (_, _) => SizeUrl();
                var showUrls = new LinkLabel { Text = "Show URLs (" + siteUrls.Length + ")", AutoSize = true,
                    AccessibleName = "Toggle image URLs from " + site };
                showUrls.LinkClicked += (_, _) =>
                {
                    urlBox.Visible = !urlBox.Visible;
                    showUrls.Text = (urlBox.Visible ? "Hide" : "Show") + " URLs (" + siteUrls.Length + ")";
                    SizeUrl();
                    PerformLayout();
                    _fitting = true;
                    try
                    {
                        Height = Math.Min((int)Math.Round(_workArea.Height * 0.7), Math.Max(180, _contentHeight!()));
                        Top = Math.Clamp(Top, _workArea.Top, _workArea.Bottom - Height);
                    }
                    finally { _fitting = false; }
                };
                details.Controls.Add(showUrls, 0, 2);
                details.Controls.Add(urlBox, 0, 3);
            }
            domain.Controls.Add(details, 1, 1);
            var group = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = new Padding(0, 2, 0, 4), Margin = Padding.Empty, AccessibleName = "Pictures from " + site };
            group.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var radioGroup = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
            var choices = new[] { RemoteImagePermissions.Allow, RemoteImagePermissions.Ask, RemoteImagePermissions.Never }
                .Select(value => new RadioButton { Text = value, AutoSize = true, ForeColor = PermissionColor(value), Margin = new Padding(1, 0, 3, 0), AccessibleName = value + " pictures from " + site }).ToArray();
            radioGroup.Controls.AddRange(choices);
            group.Controls.Add(classification, 0, 0);
            group.Controls.Add(new Label { Text = "Choose permission to save:", AutoSize = true, Dock = DockStyle.Top, AccessibleName = "Permission after Save for " + site }, 0, 1);
            group.Controls.Add(radioGroup, 0, 2);
            var change = new Label { AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(310, 0),
                AccessibleName = "Permission change for " + site, Padding = new Padding(0, 4, 0, 0) };
            group.Controls.Add(change, 0, 3);
            void UpdateChange()
            {
                var choice = choices.SingleOrDefault(button => button.Checked)?.Text;
                if (choice is null) return;
                change.Text = !saved ? "Save will set: " + choice + " (new permission)."
                    : choice == policy ? "No change to saved permission."
                    : "Save will change: " + policy + " → " + choice + ".";
                change.ForeColor = saved && choice == policy ? Color.DimGray : Color.RoyalBlue;
            }
            foreach (var choice in choices) choice.CheckedChanged += (_, _) => UpdateChange();
            var signedAssets = siteUrls.Any(url => TrackerSuggestions.UrlReason(url) is not null && TrackerSuggestions.ImageReason(url, images) is null);
            var recommendation = new Label { Text = reason.Length > 0 ? "Recommend: Never allow\r\n" + (DnsMatch(site) is null ? noDnsMatch + " " : "") + reason : "Recommend: Ask — safety is unknown.\r\n" + noDnsMatch + (signedAssets ? " Opaque tokens on visible signed images; recipient tracking is not established." : " No identifier match."),
                ForeColor = reason.Length > 0 ? Color.Firebrick : PermissionColor(RemoteImagePermissions.Ask), AutoSize = true, MaximumSize = new Size(310, 0),
                Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0), AccessibleName = "Blocking recommendation for " + site };
            details.Controls.Add(recommendation, 0, 0);
            domain.SizeChanged += (_, _) => siteLabel.MaximumSize = new Size(Math.Max(1, domain.ClientSize.Width - domain.Padding.Horizontal - 6), 0);
            details.SizeChanged += (_, _) => recommendation.MaximumSize = new Size(Math.Max(1, details.ClientSize.Width - details.Padding.Horizontal - 6), 0);
            group.SizeChanged += (_, _) =>
            {
                var width = Math.Max(1, group.ClientSize.Width - group.Padding.Horizontal - 6);
                classification.MaximumSize = new Size(width, 0);
                change.MaximumSize = new Size(width, 0);
                radioGroup.MaximumSize = new Size(width, 0);
            };
            choices.Single(choice => choice.Text == selected).Checked = true;
            _choices.Add(site, choices); domain.Controls.Add(group, 0, 1); table.Controls.Add(domain, 0, row);
        }
        if (sites.Length == 0) table.Controls.Add(new Label { Text = messageSites is null ? "No saved image domains yet." : "No remote image domains in this message.", AutoSize = true });
        scroll.Controls.Add(table);
        var updatingScroll = false;
        void UpdateScroll()
        {
            if (updatingScroll) return;
            updatingScroll = true;
            try
            {
                table.MinimumSize = new Size(scroll.ClientSize.Width, 0);
                table.Width = scroll.ClientSize.Width;
                var maximum = Math.Max(0, table.Height - scroll.ClientSize.Height);
                scrollbar.LargeChange = Math.Max(1, scroll.ClientSize.Height);
                scrollbar.Maximum = maximum + scrollbar.LargeChange - 1;
                scrollbar.Value = Math.Min(scrollbar.Value, maximum);
                scrollbar.Enabled = maximum > 0;
                if (scrollbar.Visible != (maximum > 0)) scrollbar.Visible = maximum > 0;
                table.Top = -scrollbar.Value;
            }
            finally { updatingScroll = false; }
        }
        scrollbar.ValueChanged += (_, _) => table.Top = -scrollbar.Value;
        scroll.Resize += (_, _) => UpdateScroll();
        table.SizeChanged += (_, _) => UpdateScroll();
        scroll.MouseWheel += (_, args) => scrollbar.Value = Math.Clamp(scrollbar.Value - args.Delta / 120 * scrollbar.SmallChange, 0, Math.Max(0, scrollbar.Maximum - scrollbar.LargeChange + 1));
        KeepFocusVisible(table);
        void KeepFocusVisible(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                control.Enter += (_, _) =>
                {
                    var top = scroll.PointToClient(control.PointToScreen(Point.Empty)).Y;
                    var delta = top < 0 ? top : Math.Max(0, top + control.Height - scroll.ClientSize.Height);
                    scrollbar.Value = Math.Clamp(scrollbar.Value + delta, 0, Math.Max(0, scrollbar.Maximum - scrollbar.LargeChange + 1));
                };
                KeepFocusVisible(control);
            }
        }
        var bulk = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6) };
        var permit = new Button { Text = "Always allow all", AutoSize = true, AccessibleName = "Permit all image sites" };
        var reject = new Button { Text = "Never allow all", AutoSize = true, AccessibleName = "Reject all image sites" };
        var suggested = new Button { Text = "Use blocking suggestions", AutoSize = true, AccessibleName = "Use tracker suggestions", Enabled = reasons.Values.Any(reason => reason.Length > 0) };
        permit.Click += (_, _) => SetAll(RemoteImagePermissions.Allow);
        reject.Click += (_, _) => SetAll(RemoteImagePermissions.Never);
        suggested.Click += (_, _) => { foreach (var site in sites.Where(site => reasons[site].Length > 0)) _choices[site].Single(choice => choice.Text == RemoteImagePermissions.Never).Checked = true; };
        bulk.Controls.AddRange([permit, reject, suggested]);
        bulk.Controls.Add(new Label { Text = "Green: allow · Red: block · Amber: Ask · Blue: unsaved change", AutoSize = true, Margin = new Padding(10, 6, 0, 0), AccessibleName = "Image permission colour legend" });
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(4), FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var save = new Button { Text = "Save permissions", AutoSize = true, DialogResult = DialogResult.OK, AccessibleName = "Save image site permissions" };
        footer.Controls.AddRange([cancel, save]); Controls.Add(list); Controls.Add(bulk); Controls.Add(explanation); Controls.Add(footer);
        AcceptButton = save; CancelButton = cancel;
        Resize += (_, _) => explanation.Height = TextRenderer.MeasureText(explanation.Text, explanation.Font,
            new Size(Math.Max(1, ClientSize.Width - explanation.Padding.Horizontal), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + explanation.Padding.Vertical;
        _contentHeight = () => table.Height + explanation.Height + bulk.Height + footer.Height + Height - ClientSize.Height + 12;

        void SetAll(string value) { foreach (var choices in _choices.Values) choices.Single(choice => choice.Text == value).Checked = true; }
    }

    private static Color PermissionColor(string permission) => permission switch
    {
        RemoteImagePermissions.Allow => Color.DarkGreen,
        RemoteImagePermissions.Never => Color.Firebrick,
        _ => Color.FromArgb(128, 85, 0)
    };

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var screen = Screen.FromControl(Owner ?? this);
        _monitor = screen.DeviceName;
        FitToWorkArea(screen.WorkingArea, Owner?.Bounds ?? screen.WorkingArea);
    }

    public void FitToWorkArea(Rectangle area, Rectangle anchor)
    {
        _workArea = area;
        _fitting = true;
        try
        {
            MinimumSize = new Size(Math.Min(620, (int)(area.Width * 0.85)), Math.Min(180, (int)(area.Height * 0.7)));
            MaximumSize = area.Size;
            Bounds = WorkAreaBounds(area, anchor);
            PerformLayout();
            Bounds = WorkAreaBounds(area, anchor, _contentHeight());
        }
        finally { _fitting = false; }
    }

    public static Rectangle WorkAreaBounds(Rectangle area, Rectangle anchor, int contentHeight = int.MaxValue)
    {
        var width = (int)Math.Round(area.Width * 0.85);
        var height = Math.Min((int)Math.Round(area.Height * 0.7), Math.Max(180, contentHeight));
        return new Rectangle(Math.Clamp(anchor.Left + (anchor.Width - width) / 2, area.Left, area.Right - width),
            Math.Clamp(anchor.Top + (anchor.Height - height) / 2, area.Top, area.Bottom - height), width, height);
    }

    protected override void OnShown(EventArgs e) { base.OnShown(e); _shown = true; }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        if (!_shown || _fitting) return;
        var screen = Screen.FromControl(this);
        if (screen.DeviceName == _monitor) return;
        _monitor = screen.DeviceName;
        FitToWorkArea(screen.WorkingArea, Bounds);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (!_shown || _fitting) return;
        var screen = Screen.FromRectangle(e.SuggestedRectangle);
        _monitor = screen.DeviceName;
        FitToWorkArea(screen.WorkingArea, e.SuggestedRectangle);
    }

    public static bool Edit(IWin32Window owner, RemoteImagePermissions permissions, IEnumerable<string>? messageSites = null)
    {
        using var dialog = new ImageSitesForm(permissions, messageSites);
        if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
        permissions.Save(dialog.Choices);
        return true;
    }

    public static bool EditMessage(IWin32Window owner, RemoteImagePermissions permissions, string messageHtml)
    {
        using var dialog = new ImageSitesForm(permissions, RemoteImages.Origins(messageHtml), messageHtml);
        if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
        permissions.Save(dialog.Choices);
        return true;
    }
}
