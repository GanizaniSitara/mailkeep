using System.Diagnostics;

namespace Mailkeep.Infrastructure;

public static class AttachmentSafety
{
    private static readonly HashSet<string> UnsafeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "js", "vbs", "hta", "lnk", "scr", "bat", "ps1", "cmd", "com", "msi", "iso", "one",
        "jse", "vbe", "wsf", "wsh", "ws", "psm1", "psd1", "ps1xml", "psc1", "cpl", "msc", "pif",
        "reg", "inf", "ins", "msp", "mst", "application", "appref-ms", "appinstaller", "msix",
        "msixbundle", "appx", "appxbundle", "gadget", "jar", "scf", "sct", "url", "website",
        "search-ms", "searchconnector-ms", "library-ms", "settingcontent-ms", "diagcab", "hlp", "chm",
        "vhd", "vhdx", "img", "dll", "xll", "xlam", "py", "pyw"
    };

    private static bool IsBidiControl(char c) =>
        c is '\u061c' or '\u200e' or '\u200f' or >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069';

    public static string DisplayName(string name) => SafeFileName(string.Concat(name.Where(c => !IsBidiControl(c))));

    internal static string SafeFileName(string original)
    {
        var name = Path.GetFileName(original.Replace('\\', '/'));
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        name = name.TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(name)) name = "attachment";
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)) name = "_" + name;
        return name;
    }

    public static void EnsureCanOpen(string path)
    {
        var name = Path.GetFileName(path).TrimEnd(' ', '.');
        if (!Path.HasExtension(name) || name.Any(IsBidiControl) || name.Split('.').Skip(1).Any(part => UnsafeExtensions.Contains(part.Trim())))
            throw new InvalidDataException("Mailkeep blocked opening this attachment because of its filename or file type. You can save it from the message.");
    }

    public static void OpenWithShell(string path)
    {
        EnsureCanOpen(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    internal static void MarkInternetZone(string path)
    {
        if (OperatingSystem.IsWindows())
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
    }
}
