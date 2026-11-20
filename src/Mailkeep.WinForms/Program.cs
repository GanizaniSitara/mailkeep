using Mailkeep.Infrastructure;

namespace Mailkeep.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        StartupDiagnostics.Start();
        TimingLog.Mark("Main entered");
        var root = Option(args, "--root");
        var mailto = MailtoHandoff.Find(args);
        if (mailto is not null && StartupDiagnostics.Time("MailtoHandoff.TryHandOff", () => MailtoHandoff.TryHandOff(root, mailto))) return;
        // A failure used to arrive as a bare sentence in a box — "A generic error occurred in GDI+" — with no
        // way to tell afterwards where it came from, and nothing in the event log because the box handles it.
        // Whatever goes wrong now writes down where it happened, so an intermittent fault leaves evidence.
        Application.ThreadException += (_, e) => Complain(root, e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Record(root, e.ExceptionObject as Exception);

        StartupDiagnostics.Time("WinForms initialization", () =>
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetDefaultFont(new Font("Tahoma", 8.25f, FontStyle.Regular, GraphicsUnit.Point));
            Application.SetCompatibleTextRenderingDefault(false);
        });
        var main = StartupDiagnostics.Time("MainForm constructor", () => new MainForm(root,
            args.Contains("--no-activate", StringComparer.OrdinalIgnoreCase)));
        StartupDiagnostics.Watch(main);
        TimingLog.Mark("window built");
        if (!StartupDiagnostics.Skip("MAILTO")) StartupDiagnostics.Time("ListenForMailto", () => main.ListenForMailto(root));
        if (mailto is not null) main.ComposeMailtoWhenReady(mailto);
        // Test rig only (docs/vm-ui-regressions.md): attach an archive location and open the Archive window.
        if (Option(args, "--attach-archive") is { } archive || args.Contains("--show-archive"))
            main.AttachArchiveWhenReady(Option(args, "--attach-archive"), args.Contains("--show-archive"));
        try { Application.Run(main); }
        finally { if (StartupDiagnostics.Enabled) StartupDiagnostics.Finish(); }
    }

    /// <summary>Writes the fault down and then says so, keeping the program running: this replaces the
    /// framework's own dialog, which offers the same choice and remembers nothing.</summary>
    private static void Complain(string? root, Exception error)
    {
        var log = Record(root, error);
        var quit = MessageBox.Show(
            $"{error.Message}\n\nWritten to {log ?? "nowhere — the log could not be opened"}.\n\nClose Mailkeep?",
            "Mailkeep", MessageBoxButtons.YesNo, MessageBoxIcon.Error, MessageBoxDefaultButton.Button2);
        if (quit == DialogResult.Yes) Application.Exit();
    }

    private static string? Record(string? root, Exception? error)
    {
        if (error is null) return null;
        try
        {
            var paths = new AppPaths(root);
            Directory.CreateDirectory(paths.Root);
            var log = Path.Combine(paths.Root, "errors.log");
            File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
            return log;
        }
        catch (Exception)
        {
            return null;   // A program that cannot write its log still has a fault to report.
        }
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
