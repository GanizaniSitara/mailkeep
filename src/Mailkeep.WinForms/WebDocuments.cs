using System.Text;

namespace Mailkeep.WinForms;

/// <summary>How a page is handed to the runtime. A small page travels as a data URL, which leaves nothing on
/// disk; a large one — a letter with its pictures written in — is refused that way, the runtime having a
/// limit on the length of an address, so it is written to a file beside the runtime's own data and the file
/// is opened instead. The previous file goes when the next page comes.</summary>
internal static class WebDocuments
{
    /// <summary>Below this the runtime takes a data URL; above it, it throws.</summary>
    private const int DataUrlLimit = 1_500_000;

    public static string UriFor(string document, string? folder, ref string? previousFile)
    {
        Forget(ref previousFile);
        var data = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(document));
        if (data.Length <= DataUrlLimit || folder is null) return data;
        var directory = Path.Combine(folder, "documents");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("n") + ".html");
        File.WriteAllText(path, document, new UTF8Encoding(false));
        previousFile = path;
        return new Uri(path).AbsoluteUri;
    }

    public static void Forget(ref string? previousFile)
    {
        if (previousFile is null) return;
        try { File.Delete(previousFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        previousFile = null;
    }

    /// <summary>Whether a navigation is to the page just handed over. Compared as addresses, since the runtime
    /// reports a file's address with its own escaping and casing.</summary>
    public static bool IsSame(string? expected, string? reported)
        => expected is not null && reported is not null
           && (string.Equals(expected, reported, StringComparison.Ordinal)
               || (Uri.TryCreate(expected, UriKind.Absolute, out var a) && Uri.TryCreate(reported, UriKind.Absolute, out var b) && a == b));

    private static bool _swept;
    private static readonly object SweepLock = new();

    /// <summary>Clears what an earlier run may have left behind: once per run, since every pane shares the
    /// folder and a later one starting must not take the current page out from under an earlier one.</summary>
    public static void Sweep(string? folder)
    {
        // Views now start independently. A second view must wait until cleanup finishes before writing a page.
        lock (SweepLock)
        {
            if (folder is null || _swept) return;
            _swept = true;
            try
            {
                var directory = Path.Combine(folder, "documents");
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
