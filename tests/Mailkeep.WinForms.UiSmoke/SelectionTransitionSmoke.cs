using System.Diagnostics;
using System.Reflection;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

/// <summary>Native selection transitions keep the current preview through intermediate deselection; isolated mail only.</summary>
internal static class SelectionTransitionSmoke
{
    internal static void Run(string app, string root, MailkeepServices services, string? screenshots)
    {
        services.Database.SetSetting("sync.auto", "off");
        Exception? failure = null;
        var thread = new Thread(() => { try { RunUi(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(45)), "selection transition timed out");
        if (failure is not null) throw new InvalidOperationException("selection transition failed", failure);
        Console.WriteLine("PASS selection-transition intermediate-empty=preserved latest=shown multi=cleared empty=cleared");

        void RunUi()
        {
            Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
            Forms.Application.EnableVisualStyles();
            Forms.Application.SetCompatibleTextRenderingDefault(false);
            Forms.Application.SetDefaultFont(new System.Drawing.Font("Tahoma", 8.25f));
            Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
            Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            var directory = Path.GetDirectoryName(Path.GetFullPath(app))!;
            var loader = Path.Combine(directory, "WebView2Loader.dll");
            if (!File.Exists(loader)) loader = Path.Combine(directory, "runtimes", "win-x64", "native", "WebView2Loader.dll");
            System.Runtime.InteropServices.NativeLibrary.Load(loader);
            var type = Assembly.LoadFrom(Path.Combine(directory, "Mailkeep.WinForms.dll")).GetType("Mailkeep.WinForms.MainForm", true)!;
            using var main = (Forms.Form)Activator.CreateInstance(type, [root])!;
            T Field<T>(string name) => (T)type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            void Until(Func<bool> condition)
            {
                var clock = Stopwatch.StartNew();
                while (!condition()) { Check(clock.ElapsedMilliseconds < 12000, "preview did not settle"); Forms.Application.DoEvents(); Thread.Sleep(10); }
            }
            main.Show(); Until(() => Field<bool>("_ready"));
            var list = Field<Forms.ListView>("_messages");
            var body = Field<Forms.RichTextBox>("_body");
            var subject = Field<Forms.Label>("_subject");
            Check(list.VirtualListSize == 2, "two-message fixture missing");
            list.SelectedIndices.Clear(); list.SelectedIndices.Add(0);
            Until(() => Field<MessageContent?>("_currentContent") is not null);
            for (var iteration = 0; iteration < 8; iteration++)
            {
                var target = (iteration + 1) % 2;
                var previous = subject.Text;
                var observed = new List<string>();
                EventHandler changed = (_, _) => observed.Add(subject.Text);
                subject.TextChanged += changed;
                list.SelectedIndices.Clear();
                Check(subject.Text == previous && !string.IsNullOrEmpty(body.Text), "transient deselection cleared the preview synchronously");
                list.SelectedIndices.Add(target);
                var id = ((MessageSummary)Field<IReadOnlyList<object>>("_rows")[target]).Id;
                Until(() => Field<MessageContent?>("_currentContent")?.Summary.Id == id);
                subject.TextChanged -= changed;
                Check(!observed.Contains("Select a message to read it"), "blank preview was shown between messages");
            }
            list.SelectedIndices.Add(1);
            Until(() => subject.Text == "2 messages selected");
            Check(string.IsNullOrEmpty(body.Text), "multiple selection retains a message body");
            list.SelectedIndices.Clear();
            Until(() => subject.Text == "Select a message to read it");
            Check(string.IsNullOrEmpty(body.Text), "genuine empty selection retains a message body");
            // Inspect the native viewport independently of the guest's optional HTML runtime.
            var html = Field<Forms.Control>("_html");
            var preview = html.Parent!.Parent!.Parent!;
            foreach (var size in new[] { new System.Drawing.Size(1100, 800), new System.Drawing.Size(720, 1000) })
            {
                main.Size = size;
                html.Visible = true;
                html.Parent!.PerformLayout();
                main.PerformLayout(); Forms.Application.DoEvents();
                var bounds = preview.RectangleToClient(html.Parent.RectangleToScreen(html.Bounds));
                Check(bounds.Left == 0 && bounds.Right == preview.ClientSize.Width && bounds.Bottom == preview.ClientSize.Height,
                    $"message viewport has an outer frame: {bounds}, container {preview.ClientSize}");
                html.Visible = false;
                if (screenshots is not null)
                {
                    // A colored native body makes the container edge visible; this is not an HTML paint test.
                    body.BackColor = System.Drawing.Color.LightSteelBlue;
                    body.Text = "Reading pane container fixture — HTML runtime is not exercised.";
                    body.Show(); body.BringToFront();
                    Directory.CreateDirectory(screenshots);
                    using var bitmap = new System.Drawing.Bitmap(main.Width, main.Height);
                    main.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, main.Size));
                    bitmap.Save(Path.Combine(screenshots, $"reading-container-{size.Width}.png"));
                }
            }
            main.Close();
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
