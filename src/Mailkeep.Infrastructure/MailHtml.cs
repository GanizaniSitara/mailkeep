using System.Net;
using System.Text.RegularExpressions;
using MimeKit;

namespace Mailkeep.Infrastructure;

internal static class MailHtml
{
    public static string? WithInlineImages(MimeMessage message)
    {
        var html = message.HtmlBody;
        if (html is null || !html.Contains("cid:", StringComparison.OrdinalIgnoreCase)) return html;
        var images = message.BodyParts.OfType<MimePart>()
            .Where(part => part.Content is not null && part.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(part.ContentId))
            .GroupBy(part => part.ContentId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var decoded = new Dictionary<string, string>(StringComparer.Ordinal);
        string Replace(Match match)
        {
            var uri = match.Groups["uri"].Value;
            var id = Uri.UnescapeDataString(WebUtility.HtmlDecode(uri[4..]));
            if (!images.TryGetValue(id, out var part)) return match.Value;
            if (!decoded.TryGetValue(id, out var data))
            {
                using var bytes = new MemoryStream();
                part.Content!.DecodeTo(bytes);
                data = "data:" + part.ContentType.MimeType + ";base64," + Convert.ToBase64String(bytes.ToArray());
                decoded.Add(id, data);
            }
            return match.Value.Replace(uri, data, StringComparison.Ordinal);
        }
        // Only replace image attributes and CSS URLs. Remote URLs and unresolved CIDs stay untouched;
        // the reader's existing content policy continues to block network resources.
        html = Regex.Replace(html, "\\b(?:src|background)\\s*=\\s*(?<quote>['\"]?)(?<uri>cid:[^'\"\\s<>]+)\\k<quote>", Replace, RegexOptions.IgnoreCase);
        return Regex.Replace(html, "url\\(\\s*(?<quote>['\"]?)(?<uri>cid:[^'\"\\s<>)]+)\\k<quote>\\s*\\)", Replace, RegexOptions.IgnoreCase);
    }
}
