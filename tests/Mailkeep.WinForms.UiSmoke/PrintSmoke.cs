using System.Drawing;
using System.Drawing.Printing;
using System.Reflection;
using System.Security.Cryptography;
using Mailkeep.Core;
using Mailkeep.Infrastructure;

internal static class PrintSmoke
{
    public static void Run(string app, MailkeepServices services, string? screenshots)
    {
        var assembly = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(app))!, "Mailkeep.WinForms.dll"));
        var type = assembly.GetType("Mailkeep.WinForms.MainForm", throwOnError: true)!;
        var factory = type.GetMethod("CreatePrintDocument", BindingFlags.Static | BindingFlags.NonPublic)!;
        var message = services.Database.ListMessages().Single(message => message.Account == "local-fixture");
        var content = services.Database.GetMessage(message.Id)!;
        CheckStandalone(content);
        using var document = (PrintDocument)factory.Invoke(null, [content])!;
        var begin = typeof(PrintDocument).GetMethod("OnBeginPrint", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var page = typeof(PrintDocument).GetMethod("OnPrintPage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        CheckOutgoing();
        var first = Render();
        var repeated = Render();
        if (first != repeated) throw new InvalidOperationException("Printing the same document again did not restart at its first page.");
        begin.Invoke(document, [new PrintEventArgs()]);
        using var tiny = new Bitmap(50, 50);
        using var graphics = Graphics.FromImage(tiny);
        var invalid = new PrintPageEventArgs(graphics, new Rectangle(0, 0, 1, 1), new Rectangle(0, 0, 50, 50), new PageSettings());
        try { page.Invoke(document, [invalid]); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException)
        {
            Console.WriteLine($"PASS print-pages pages={first.Pages} repeat=identical tiny-margins=rejected standalone=own-message printer-jobs=none");
            return;
        }
        throw new InvalidOperationException("An unusable print area did not stop printing.");

        void CheckOutgoing()
        {
            var outgoingFactory = type.GetMethod("CreateOutgoingPrintDocument", BindingFlags.Static | BindingFlags.NonPublic)!;
            var identity = services.Identities.Find("fixture-3")!;
            var now = DateTimeOffset.UtcNow;
            var draft = new OutgoingMessage("print-fixture", identity.Id, ["to@example.invalid"],
                ["cc@example.invalid"], ["bcc@example.invalid"], "Outgoing print fixture", "Saved outgoing body.",
                OutgoingStates.Draft, null, 0, now.AddHours(-1), now, null, null, null);
            foreach (var state in new[] { OutgoingStates.Draft, OutgoingStates.Outbox })
            {
                using var outgoingDocument = (PrintDocument)outgoingFactory.Invoke(null, [draft with { Status = state }, identity])!;
                if (outgoingDocument.DocumentName != draft.Subject) throw new InvalidOperationException("Outgoing print lost its subject.");
                begin.Invoke(outgoingDocument, [new PrintEventArgs()]);
                using var bitmap = new Bitmap(850, 1100);
                bitmap.SetResolution(100, 100);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.White);
                var args = new PrintPageEventArgs(graphics, new Rectangle(50, 50, 750, 1000), new Rectangle(0, 0, 850, 1100), new PageSettings());
                page.Invoke(outgoingDocument, [args]);
                if (args.HasMorePages) throw new InvalidOperationException("Short outgoing message did not fit on one page.");
                if (screenshots is not null)
                {
                    Directory.CreateDirectory(screenshots);
                    bitmap.Save(Path.Combine(screenshots, $"print-{state}.png"));
                }
            }
            // An unfinished draft need not have recipients or a still-configured sending identity.
            using var unfinished = (PrintDocument)outgoingFactory.Invoke(null,
                [draft with { To = [], Cc = [], Bcc = [], Subject = "" }, null])!;
            begin.Invoke(unfinished, [new PrintEventArgs()]);
            Console.WriteLine("PASS outgoing-print draft=rendered outbox=rendered incomplete-draft=accepted printer-jobs=none");
        }

        void CheckStandalone(MessageContent ownMessage)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    MessageContent? printed = null;
                    var messageType = assembly.GetType("Mailkeep.WinForms.MessageWindow", throwOnError: true)!;
                    using var window = (System.Windows.Forms.Form)Activator.CreateInstance(messageType,
                        [ownMessage, (Func<MessageContent, bool, Task>)((_, _) => Task.CompletedTask),
                         (Func<MessageContent, Task>)(_ => Task.CompletedTask), (Action<MessageContent>)(value => printed = value),
                         (Action<MessageContent>)(_ => { }), Path.Combine(services.Paths.Root, "print-browser"),
                         (Func<long, int, string>)services.Database.ExtractAttachment])!;
                    var toolbar = window.Controls.OfType<System.Windows.Forms.ToolStrip>().Single();
                    toolbar.Items.Cast<System.Windows.Forms.ToolStripItem>().Single(item => item.Text == "Print").PerformClick();
                    if (!ReferenceEquals(ownMessage, printed)) throw new InvalidOperationException("Standalone Print did not use its own message.");
                }
                catch (Exception error) { failure = error; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Standalone Print check did not finish.");
            if (failure is not null) throw failure;
        }

        (int Pages, string Hash) Render()
        {
            begin.Invoke(document, [new PrintEventArgs()]);
            var pages = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (true)
            {
                using var bitmap = new Bitmap(850, 1100);
                bitmap.SetResolution(100, 100);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.White);
                var args = new PrintPageEventArgs(graphics, new Rectangle(50, 50, 750, 1000), new Rectangle(0, 0, 850, 1100), new PageSettings());
                page.Invoke(document, [args]);
                pages++;
                using var bytes = new MemoryStream();
                bitmap.Save(bytes, System.Drawing.Imaging.ImageFormat.Png);
                hash.AppendData(bytes.ToArray());
                if (screenshots is not null && (pages <= 2 || !args.HasMorePages))
                {
                    Directory.CreateDirectory(screenshots);
                    File.WriteAllBytes(Path.Combine(screenshots, pages == 1 ? "print-first-page.png" : pages == 2 ? "print-second-page.png" : "print-last-page.png"), bytes.ToArray());
                }
                if (!args.HasMorePages) break;
                if (pages >= 30) throw new InvalidOperationException("Print pagination did not terminate.");
            }
            if (pages < 3) throw new InvalidOperationException("Long message did not produce the expected multiple pages.");
            return (pages, Convert.ToHexString(hash.GetHashAndReset()));
        }
    }
}
