namespace Mailkeep.Core;

/// <summary>What a mailto: link asks for (RFC 6068): the addresses before the '?', and to, cc, bcc, subject
/// and body after it. Every part is percent-decoded; '+' stays a plus, because RFC 6068 does not make it a
/// space and addresses such as name+tag@example.com depend on that.</summary>
public sealed record MailtoLink(string To, string Cc, string Bcc, string Subject, string Body)
{
    public static bool IsMailto(string? value)
        => value is not null && value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    public static MailtoLink Parse(string link)
    {
        if (!IsMailto(link)) throw new ArgumentException("Not a mailto: link.", nameof(link));
        var rest = link["mailto:".Length..];
        var question = rest.IndexOf('?');
        var path = question < 0 ? rest : rest[..question];
        var query = question < 0 ? "" : rest[(question + 1)..];

        var to = new List<string>(Addresses(path));
        var cc = new List<string>();
        var bcc = new List<string>();
        string subject = "", body = "";
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var value = parts.Length > 1 ? Decode(parts[1]) : "";
            switch (Decode(parts[0]).ToLowerInvariant())
            {
                case "to": to.AddRange(Split(value)); break;
                case "cc": cc.AddRange(Split(value)); break;
                case "bcc": bcc.AddRange(Split(value)); break;
                case "subject": subject = value; break;
                case "body": body = value.ReplaceLineEndings("\r\n"); break;
            }
        }
        return new MailtoLink(Join(to), Join(cc), Join(bcc), subject, body);
    }

    private static IEnumerable<string> Addresses(string path) => Split(Decode(path));

    private static IEnumerable<string> Split(string value)
        => value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Join(List<string> addresses) => string.Join("; ", addresses);

    private static string Decode(string value) => Uri.UnescapeDataString(value);
}
