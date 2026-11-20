using System.Reflection;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

internal static class HtmlPrintSmoke
{
    public static void Run(string app, MailkeepServices services, string output)
    {
        Directory.CreateDirectory(output);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
                var appDirectory = Path.GetDirectoryName(Path.GetFullPath(app))!;
                // Reflection loads managed dependencies beside the client; native discovery still starts
                // beside this smoke-test executable, whose dependency graph does not include WebView2.
                var loader = Path.Combine(appDirectory, "WebView2Loader.dll");
                if (!File.Exists(loader)) loader = Path.Combine(appDirectory, "runtimes", "win-x64", "native", "WebView2Loader.dll");
                System.Runtime.InteropServices.NativeLibrary.Load(loader);
                var assembly = Assembly.LoadFrom(Path.Combine(appDirectory, "Mailkeep.WinForms.dll"));
                var type = assembly.GetType("Mailkeep.WinForms.HtmlPrintForm", true)!;
                var message = services.Database.ListMessages().Single(message => message.Account == "local-fixture");
                var content = services.Database.GetMessage(message.Id)!;
                content = content with { BodyHtml = content.BodyHtml + "<table style='border-collapse:collapse'><tr><td style='border:2px solid blue'>Left cell</td><td style='border:2px solid blue'>Right cell</td></tr></table><div style='break-before:page'>SECOND PRINT PAGE</div>" };
                using var form = (Forms.Form)Activator.CreateInstance(type, [content, Path.Combine(services.Paths.Root, "print-browser")])!;
                var html = type.GetField("_html", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                var loaded = html.GetType().GetProperty("DocumentLoaded")!;
                var toolbar = form.Controls.OfType<Forms.ToolStrip>().Single();
                var print = toolbar.Items.Cast<Forms.ToolStripItem>().Single(item => item.Text == "Print");
                if (print.Enabled) throw new InvalidOperationException("Print was enabled before the document loaded.");
                form.Show();
                PumpUntil(() => form.IsDisposed || (bool)loaded.GetValue(html)!);
                if (form.IsDisposed || !print.Enabled) throw new InvalidOperationException("HTML print document failed to load.");
                var core = html.GetType().GetProperty("CoreWebView2")!.GetValue(html)!;
                var environment = core.GetType().GetProperty("Environment")!.GetValue(core)!;
                var settings = environment.GetType().GetMethod("CreatePrintSettings")!.Invoke(environment, null)!;
                settings.GetType().GetProperty("ShouldPrintBackgrounds")!.SetValue(settings, true);
                var pdf = (Task<bool>)core.GetType().GetMethod("PrintToPdfAsync")!.Invoke(core,
                    [Path.Combine(output, "print-html.pdf"), settings])!;
                PumpUntil(() => pdf.IsCompleted);
                if (!pdf.GetAwaiter().GetResult()) throw new InvalidOperationException("HTML PDF printing failed.");
                form.Close();
                Console.WriteLine("PASS print-html loaded=True print-enabled-after-load=True PDF=print-html.pdf printer-jobs=none");
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(50))) throw new TimeoutException("HTML printing did not finish.");
        if (failure is not null) throw failure;
    }

    private static void PumpUntil(Func<bool> done)
    {
        var timeout = DateTime.UtcNow.AddSeconds(20);
        while (!done())
        {
            if (DateTime.UtcNow >= timeout) throw new TimeoutException("HTML print operation timed out.");
            Forms.Application.DoEvents();
            Thread.Sleep(20);
        }
    }
}
