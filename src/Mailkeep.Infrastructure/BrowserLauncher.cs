using System.Diagnostics;

namespace Mailkeep.Infrastructure;

public static class BrowserLauncher
{
    public const string BravePath = @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe";

    public static ProcessStartInfo StartInfo(string target, string? preferredBrowser = BravePath)
    {
        if (preferredBrowser is null || !File.Exists(preferredBrowser)) return new(target) { UseShellExecute = true };
        var info = new ProcessStartInfo(preferredBrowser) { UseShellExecute = false };
        info.ArgumentList.Add(target);
        return info;
    }

    public static void Open(string target) => Process.Start(StartInfo(target));
}
