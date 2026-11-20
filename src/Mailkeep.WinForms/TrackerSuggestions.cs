using System.Net;
using System.Text.RegularExpressions;

namespace Mailkeep.WinForms;

/// <summary>Offline recommendations only; effective picture permissions change on Save.</summary>
internal static class TrackerSuggestions
{
    public const string EnabledSetting = "images.dns_suggestions";
    public static bool Enabled(Mailkeep.Infrastructure.MailDatabase database) => database.GetSetting(EnabledSetting) != "false";
    private static readonly HashSet<string> Domains = Load();
    public static string? ImageReason(string url, IReadOnlyList<RemoteImages.ImageContext> images)
    {
        var uses = images.Where(image => image.Url == url).ToArray();
        if (uses.Any(image => image.Hidden || image.Width >= 0 && image.Height >= 0 && image.Width <= 1 && image.Height <= 1))
            return "Likely tracking pixel: hidden or 1×1 image in this message.";
        var reason = UrlReason(url);
        if (reason is null) return null;
        // A visible image with an expiry and signature can use opaque asset tokens without identifying a reader.
        var query = new Uri(url).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => Uri.UnescapeDataString(part.Split('=', 2)[0]).ToLowerInvariant()).ToArray();
        var signedAsset = uses.Length > 0 && uses.All(image => !image.Hidden && image.Width > 1 && image.Height > 1)
            && query.Any(name => name is "e" or "expires" or "expiry" or "exp")
            && query.Any(name => name is "t" or "sig" or "signature")
            && query.All(name => name is "e" or "expires" or "expiry" or "exp" or "t" or "sig" or "signature" or "v");
        return signedAsset ? null : reason;
    }

    public static string ImageSummary(string url, IReadOnlyList<RemoteImages.ImageContext> images)
    {
        var uses = images.Where(image => image.Url == url).ToArray();
        if (uses.Length == 0) return "Image context unavailable (for example, a CSS background).";
        return string.Join("; ", uses.Select(image =>
            (image.Alt.Length > 0 ? "Image: " + image.Alt + ". " : "Image without a description. ")
            + (image.Width >= 0 && image.Height >= 0 ? $"{image.Width}×{image.Height}px. " : "Size unspecified. ")
            + (image.Hidden ? "Hidden. " : "")).Distinct());
    }
    public static string? UrlReason(string url)
    {
        if (!RemoteImages.TryOrigin(url, out _)) return null;
        var uri = new Uri(url);
        foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = parameter.Split('=', 2);
            var name = Uri.UnescapeDataString(parts[0]).ToLowerInvariant();
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "";
            if (value.Length > 0 && name is "uid" or "uuid" or "user_id" or "userid" or "recipient" or "recipient_id" or "email" or "email_id" or "subscriber" or "subscriber_id" or "contact_id" or "token")
                return "Possible unique identifier: URL parameter '" + name + "'.";
            if (Identifier(value)) return "Possible unique identifier: opaque value in URL parameter '" + name + "'.";
        }
        foreach (var segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (Identifier(Uri.UnescapeDataString(segment))) return "Possible unique identifier: long opaque value in image URL path.";
        return null;

        static bool Identifier(string value)
        {
            var stem = value.Split('.')[0];
            return Guid.TryParse(stem, out _)
                || Regex.IsMatch(stem, "^[a-fA-F0-9]{24,}$", RegexOptions.CultureInvariant)
                || stem.Length >= 32 && Regex.IsMatch(stem, "^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)
                    && stem.Any(char.IsLetter) && stem.Any(char.IsDigit);
        }
    }
    public static string? Match(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
        var host = uri.IdnHost.TrimEnd('.');
        if (IPAddress.TryParse(host, out _)) return null;
        while (true)
        {
            if (Domains.Contains(host)) return host;
            var dot = host.IndexOf('.');
            if (dot < 0) return null;
            host = host[(dot + 1)..];
        }
    }

    private static HashSet<string> Load()
    {
        using var stream = typeof(TrackerSuggestions).Assembly.GetManifestResourceStream("Mailkeep.TrackingDomains")!;
        using var reader = new StreamReader(stream);
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
            if (!line.StartsWith('#') && Uri.CheckHostName(line) == UriHostNameType.Dns) domains.Add(line);
        return domains;
    }
}
