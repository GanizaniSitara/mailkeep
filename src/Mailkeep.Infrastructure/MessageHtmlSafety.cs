using System.Text.RegularExpressions;

namespace Mailkeep.Infrastructure;

public static class MessageHtmlSafety
{
    public const string NoDnsPrefetch = "<meta http-equiv=\"x-dns-prefetch-control\" content=\"off\">";

    public static string StripActiveContent(string html)
    {
        var body = Regex.Replace(html, @"<(script|iframe|object|embed|form|base)\b[^>]*>.*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = Regex.Replace(body, @"<(script|iframe|object|embed|form|base)\b[^>]*/?>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = Regex.Replace(body, """<link\b(?:[^<>"']|"[^"]*"|'[^']*')*(?:>|$)""", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = Regex.Replace(body, """<meta\b[^>]*http-equiv\s*=\s*['"]?(?:refresh|x-dns-prefetch-control)[^>]*>""", "", RegexOptions.IgnoreCase);
        return NoDnsPrefetch + body;
    }
}
