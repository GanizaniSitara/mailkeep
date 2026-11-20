using System.Diagnostics;

namespace Mailkeep.WinForms;

/// <summary>How long opening the window, and every rebuild of the folder tree, really took on this machine,
/// written to logs\timing.log beside the store. Times are from the moment the process started, which is as
/// near to the click as the process can see, with the process's CPU time beside them: a large gap between
/// the two is waiting — on the disk, mostly — rather than work.</summary>
internal static class TimingLog
{
    private static readonly string File = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mailkeep", "logs", "timing.log");
    private static readonly object Gate = new();
    private static bool _started;

    // Interactive measurements must not wait for the log file or its writer lock.
    public static void MarkBackground(string phase)
        => ThreadPool.QueueUserWorkItem(_ => Mark(phase));

    public static void Mark(string phase)
    {
        StartupDiagnostics.Mark(phase);
        try
        {
            using var process = Process.GetCurrentProcess();
            var since = DateTime.Now - process.StartTime;
            var line = $"{since.TotalMilliseconds,8:F0} ms  cpu {process.TotalProcessorTime.TotalMilliseconds,6:F0} ms  {phase}{Environment.NewLine}";
            lock (Gate)
            {
                if (!_started)
                {
                    _started = true;
                    Directory.CreateDirectory(Path.GetDirectoryName(File)!);
                    // Kept to the last few hundred starts.
                    if (new FileInfo(File) is { Exists: true, Length: > 1_000_000 }) System.IO.File.Move(File, File + ".old", true);
                    line = $"=== {process.StartTime:yyyy-MM-dd HH:mm:ss.fff} pid {process.Id}{Environment.NewLine}" + line;
                }
                System.IO.File.AppendAllText(File, line);
            }
        }
        catch (Exception) { } // timing is never a reason for Mailkeep to fail
    }

    public static T Time<T>(string phase, Func<T> work)
    {
        var clock = Stopwatch.StartNew();
        var result = work();
        Mark($"{phase}: {clock.ElapsedMilliseconds} ms");
        return result;
    }
}
