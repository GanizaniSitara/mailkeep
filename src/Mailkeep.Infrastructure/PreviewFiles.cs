using System.Security.Cryptography;
using System.Text;

namespace Mailkeep.Infrastructure;

public static class PreviewFiles
{
    public static string HtmlDirectory(AppPaths paths) => Path.Combine(Path.GetTempPath(), "Mailkeep", "html-preview",
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(paths.Root.ToUpperInvariant())))[..24]);

    public static void Cleanup(AppPaths paths, bool includeLegacyShared = false)
    {
        Clear(paths.PreviewDirectory);
        Clear(HtmlDirectory(paths));
        if (includeLegacyShared && paths.Root.Equals(new AppPaths().Root, StringComparison.OrdinalIgnoreCase))
        {
            var legacy = Path.GetDirectoryName(HtmlDirectory(paths))!;
            if (Directory.Exists(legacy))
                foreach (var file in Directory.EnumerateFiles(legacy, "message-*.html"))
                    try { if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1)) File.Delete(file); }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void Clear(string root)
    {
        if (!Directory.Exists(root)) return;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            foreach (var directory in Directory.EnumerateDirectories(root, "*", options).OrderByDescending(path => path.Length))
                try { Directory.Delete(directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static async Task ExpireHtmlAsync(string file)
    {
        await Task.Delay(TimeSpan.FromMinutes(5)); // Let the external browser open and render its local file first.
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
