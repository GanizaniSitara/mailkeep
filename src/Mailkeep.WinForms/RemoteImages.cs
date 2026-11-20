using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mailkeep.Infrastructure;

namespace Mailkeep.WinForms;

internal static partial class RemoteImages
{
    public sealed record ImageContext(string Url, string Alt, int Width, int Height, bool Hidden);

    public static IReadOnlyList<ImageContext> Contexts(string html)
    {
        var images = new List<ImageContext>();
        foreach (Match image in ImageTag().Matches(html))
        {
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match attribute in Attributes().Matches(image.Value))
                attributes[attribute.Groups["name"].Value] = WebUtility.HtmlDecode(attribute.Groups["value"].Value);
            if (!attributes.TryGetValue("src", out var url) || !TryOrigin(url, out _)) continue;
            var style = attributes.GetValueOrDefault("style", "");
            int Dimension(string name)
            {
                var css = Regex.Match(style, @"(?:^|;)\s*" + name + @"\s*:\s*(\d+)(?:px)?\s*(?:;|$)", RegexOptions.IgnoreCase);
                var value = css.Success ? css.Groups[1].Value : attributes.GetValueOrDefault(name);
                return int.TryParse(value, out var pixels) ? pixels : -1;
            }
            images.Add(new(url, attributes.GetValueOrDefault("alt", ""), Dimension("width"), Dimension("height"),
                Regex.IsMatch(style, @"(?:display\s*:\s*none|visibility\s*:\s*hidden)", RegexOptions.IgnoreCase)));
        }
        return images;
    }

    [GeneratedRegex("""<img\b[^>]*>""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageTag();

    [GeneratedRegex("""\b(?<name>[\w-]+)\s*=\s*(?:['\"](?<value>[^'\"]*)['\"]|(?<value>[^\s>]+))""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Attributes();

    public static IReadOnlyList<string> Origins(string html)
        => Urls(html).Select(value => { TryOrigin(value, out var origin); return origin; })
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyCollection<string> Urls(string html)
    {
        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match attribute in ImageAttribute().Matches(html))
        {
            var value = WebUtility.HtmlDecode(attribute.Groups["value"].Value).Trim();
            if (attribute.Groups["name"].Value.Equals("srcset", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match candidate in HttpUrl().Matches(value)) AddUrl(candidate.Value);
            }
            else AddUrl(value);
        }
        foreach (Match css in CssUrl().Matches(html)) AddUrl(WebUtility.HtmlDecode(css.Groups["value"].Value).Trim());
        return urls;

        void AddUrl(string value)
        {
            if (TryOrigin(value, out _)) urls.Add(value);
        }
    }

    public static bool TryOrigin(string value, out string origin)
    {
        origin = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.Host.Length == 0 || uri.UserInfo.Length > 0) return false;
        origin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    [GeneratedRegex("""\b(?<name>src|srcset|background)\s*=\s*(?:['\"](?<value>[^'\"]*)['\"]|(?<value>[^\s>]+))""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageAttribute();

    [GeneratedRegex("""\burl\(\s*(?:['\"](?<value>[^'\"]*)['\"]|(?<value>[^)\s]+))\s*\)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssUrl();

    [GeneratedRegex("""https?://[^\s,'\"<>\)]+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HttpUrl();
}

internal sealed class RemoteImagePermissions
{
    public const string Setting = "images.allowed_origins";
    public const string PoliciesSetting = "images.site_policies";
    public const string Allow = "Always allow", Ask = "Ask", Never = "Never allow";
    private readonly MailDatabase _database;
    public event EventHandler? Changed;

    public RemoteImagePermissions(MailDatabase database) => _database = database;
    public bool DnsSuggestionsEnabled => TrackerSuggestions.Enabled(_database);

    public IReadOnlySet<string> Allowed()
        => Policies().Where(pair => pair.Value == Allow).Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Policies()
    {
        var policies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var stored = JsonSerializer.Deserialize<string[]>(_database.GetSetting(Setting) ?? "[]") ?? [];
            foreach (var value in stored) if (RemoteImages.TryOrigin(value, out var origin)) policies[origin] = Allow;
        }
        catch (JsonException) { }
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(_database.GetSetting(PoliciesSetting) ?? "{}") ?? [];
            foreach (var (value, policy) in stored)
                if (RemoteImages.TryOrigin(value, out var origin)) policies[origin] = policy is Allow or Never ? policy : Ask;
        }
        catch (JsonException) { }
        return policies;
    }

    public string Policy(string origin) => Policies().GetValueOrDefault(origin, Ask);

    public string[] ForLoad(IEnumerable<string> origins, bool includeAsk = false)
    {
        var policies = Policies();
        return origins.Where(origin => policies.GetValueOrDefault(origin, Ask) is Allow
            || includeAsk && policies.GetValueOrDefault(origin, Ask) == Ask).ToArray();
    }

    public void Save(IReadOnlyDictionary<string, string> choices)
    {
        var policies = new Dictionary<string, string>(Policies(), StringComparer.OrdinalIgnoreCase);
        foreach (var (value, policy) in choices)
            if (RemoteImages.TryOrigin(value, out var origin)) policies[origin] = policy is Allow or Never ? policy : Ask;
        _database.SetSetting(PoliciesSetting, JsonSerializer.Serialize(policies));
        // Keep the old allow list in sync for compatibility; explicit Ask/Never override old entries.
        _database.SetSetting(Setting, JsonSerializer.Serialize(policies.Where(pair => pair.Value == Allow).Select(pair => pair.Key).Order().ToArray()));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class RemoteImageBar : Panel
{
    private Action? _loadAction;
    private Action? _rememberAction;
    private readonly Button _load = new()
    {
        TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(0, 70, 140), FlatStyle = FlatStyle.Flat,
        Font = new Font("Tahoma", 8.25f), Padding = new Padding(7, 0, 4, 0), Cursor = Cursors.Hand,
        AccessibleName = "Load blocked pictures"
    };
    private readonly Button _always = new()
    {
        Width = 112, Text = "Image domains…", FlatStyle = FlatStyle.Flat,
        Font = new Font("Tahoma", 8.25f), AccessibleName = "Image site permissions"
    };
    private readonly Label _summary = new() { TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(7, 0, 4, 0), AccessibleName = "Image permissions summary" };

    public RemoteImageBar()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Dock = DockStyle.Fill;
        Height = 27;
        Margin = new Padding(0, 2, 0, 3);
        BackColor = Color.FromArgb(221, 235, 247);
        BorderStyle = BorderStyle.FixedSingle;
        Visible = false;
        _always.FlatAppearance.BorderColor = Color.FromArgb(130, 165, 195);
        _load.FlatAppearance.BorderSize = 0;
        Controls.Add(_always);
        Controls.Add(_load);
        Controls.Add(_summary);
        _load.Click += (_, _) => _loadAction?.Invoke();
        _always.Click += (_, _) => BeginInvoke(() => _rememberAction?.Invoke());
        Resize += (_, _) => LayoutActions();
        LayoutActions();
    }

    public void ShowFor(IReadOnlyList<string> origins, Action? load, Action? remember, int neverCount = 0)
    {
        // Saved Allow/Never decisions need no prompt. Loading Ask pictures once also dismisses it.
        if (origins.Count == 0 || load is null) { Clear(); return; }
        _load.Text = "Load pictures once — Ask domains only. Never allow domains stay blocked.";
        _loadAction = load;
        _rememberAction = remember;
        _load.Visible = load is not null;
        _summary.Visible = load is null;
        _summary.Text = neverCount > 0 ? $"Pictures from {neverCount} sites are set to Never allow."
            : "Manage saved permissions with Image domains…";
        _always.Visible = remember is not null;
        Visible = true;
    }

    public void Clear()
    {
        _loadAction = null;
        _rememberAction = null;
        Visible = false;
    }

    private void LayoutActions()
    {
        _always.SetBounds(Math.Max(0, ClientSize.Width - _always.Width), 0, _always.Width, ClientSize.Height);
        _load.SetBounds(0, 0, Math.Max(0, ClientSize.Width - _always.Width), ClientSize.Height);
        _summary.Bounds = _load.Bounds;
    }
}
