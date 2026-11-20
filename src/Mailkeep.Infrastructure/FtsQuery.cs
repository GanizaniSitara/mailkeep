using System.Text;

namespace Mailkeep.Infrastructure;

/// <summary>Turns what a person types into an FTS5 MATCH expression: quoted phrases, AND/OR/NOT in capitals,
/// trailing * for prefixes, and subject:/sender:/recipient:/body: column filters. folder: and account: are not
/// index columns, so they come back separately for the SQL side. Nothing here ever throws on user input — the
/// worst query in the world still deserves a result, so the caller falls back to plain terms if FTS5 refuses
/// the translation.</summary>
public static class FtsQuery
{
    public sealed record Translated(string Match, string? Folder, string? Account);

    private static readonly Dictionary<string, string> Columns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["subject"] = "subject",
        ["sender"] = "sender",
        ["from"] = "sender",
        ["recipient"] = "recipients",
        ["recipients"] = "recipients",
        ["to"] = "recipients",
        ["body"] = "body_text"
    };

    public static Translated Parse(string query)
    {
        string? folder = null;
        string? account = null;
        var pieces = new List<string>();
        var pendingOperator = "";

        foreach (var token in Tokenize(query))
        {
            if (token is "AND" or "OR" or "NOT")
            {
                // An operator binds two operands; one arriving first, last, or doubled is noise, not an error.
                if (pieces.Count > 0 && pendingOperator.Length == 0) pendingOperator = token;
                continue;
            }

            var (prefix, remainder) = SplitColumn(token);
            if (prefix is "folder") { folder = remainder.Trim('"'); pendingOperator = ""; continue; }
            if (prefix is "account") { account = remainder.Trim('"'); pendingOperator = ""; continue; }

            var term = Term(remainder);
            if (term.Length == 0) { pendingOperator = ""; continue; }
            if (prefix is not null && Columns.TryGetValue(prefix, out var column)) term = column + " : " + term;

            if (pieces.Count > 0) pieces.Add(pendingOperator.Length > 0 ? pendingOperator : "AND");
            pieces.Add(term);
            pendingOperator = "";
        }
        return new Translated(string.Join(' ', pieces), folder, account);
    }

    /// <summary>Today's behaviour, kept as the safety net: every token quoted, joined with AND. It cannot
    /// express anything interesting, and it cannot fail.</summary>
    public static string Fallback(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => '"' + term.Replace("\"", "\"\"") + '"');
        return string.Join(" AND ", terms);
    }

    /// <summary>One term for the MATCH expression: a quoted phrase, with a trailing * carried outside the
    /// quotes where FTS5 wants its prefix marker.</summary>
    private static string Term(string raw)
    {
        var text = raw.Trim();
        var prefix = text.EndsWith('*') && text.Length > 1;
        if (prefix) text = text[..^1];
        text = text.Trim('"').Trim();
        if (text.Length == 0) return "";
        return '"' + text.Replace("\"", "\"\"") + '"' + (prefix ? "*" : "");
    }

    private static (string? Prefix, string Remainder) SplitColumn(string token)
    {
        if (token.StartsWith('"')) return (null, token);
        var colon = token.IndexOf(':');
        if (colon <= 0 || colon == token.Length - 1) return (null, token);
        var name = token[..colon];
        if (!Columns.ContainsKey(name) && !name.Equals("folder", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("account", StringComparison.OrdinalIgnoreCase))
            return (null, token);
        return (name.ToLowerInvariant(), token[(colon + 1)..]);
    }

    /// <summary>Whitespace-separated tokens, except that quoted stretches stay together — including after a
    /// column prefix, so subject:"quarterly invoice" is one token. An unclosed quote runs to the end of the
    /// query rather than being an error.</summary>
    private static IEnumerable<string> Tokenize(string query)
    {
        var current = new StringBuilder();
        var quoted = false;
        foreach (var character in query)
        {
            if (character == '"') { quoted = !quoted; current.Append(character); continue; }
            if (!quoted && char.IsWhiteSpace(character))
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                continue;
            }
            current.Append(character);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}
