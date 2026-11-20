using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Mailkeep.Core;
using Mailkeep.Infrastructure;

namespace Mailkeep.WinForms;

/// <summary>Mailkeep as the system mail handler. Windows starts the program with the mailto: link as its
/// argument. When Mailkeep is already open, the new process passes the link to it over a named pipe and
/// exits, so a link opens a compose in the running window rather than a second copy of the mailbox.</summary>
internal static class MailtoHandoff
{
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
    private const int AnyProcess = -1;

    public static string? Find(string[] args) => args.FirstOrDefault(MailtoLink.IsMailto);

    /// <summary>One pipe per store, so a test copy run with --root never receives the live mailbox's links.</summary>
    public static string PipeName(string? root)
    {
        var store = Path.GetFullPath(new AppPaths(root).Root).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(store)))[..16];
        return "Mailkeep.Mailto." + hash;
    }

    /// <summary>True when a running Mailkeep took the link.</summary>
    public static bool TryHandOff(string? root, string link)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(root), PipeDirection.Out);
            pipe.Connect(500);
            // The clicked link gave this process the right to bring a window forward; pass it on.
            AllowSetForegroundWindow(AnyProcess);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
            writer.Write(link);
            return true;
        }
        catch (Exception)
        {
            return false;   // Nothing is listening: this process becomes the running Mailkeep.
        }
    }
}

internal sealed partial class MainForm
{
    private readonly CancellationTokenSource _mailtoListening = new();

    /// <summary>Takes links handed over by later launches, until this window closes.</summary>
    internal void ListenForMailto(string? root)
    {
        var name = MailtoHandoff.PipeName(root);
        FormClosed += (_, _) => _mailtoListening.Cancel();
        _ = Task.Run(async () =>
        {
            while (!_mailtoListening.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_mailtoListening.Token);
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    var link = await reader.ReadToEndAsync(_mailtoListening.Token);
                    if (MailtoLink.IsMailto(link) && IsHandleCreated)
                        BeginInvoke(new Action(() => ComposeMailtoWhenReady(link)));
                }
                catch (OperationCanceledException) { return; }
                catch (IOException) { await Task.Delay(250); }   // a broken handover; keep listening
            }
        });
    }

    /// <summary>Opens a compose for the link once the mailbox has loaded: a link that starts Mailkeep arrives
    /// before the identities a compose needs are ready.</summary>
    internal void ComposeMailtoWhenReady(string link)
    {
        if (_ready) { ComposeMailto(link); return; }
        var waited = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 200 };
        timer.Tick += (_, _) =>
        {
            waited += timer.Interval;
            if (!_ready && waited < 120_000 && !IsDisposed) return;
            timer.Stop();
            timer.Dispose();
            if (_ready) ComposeMailto(link);
        };
        timer.Start();
    }

    private void ComposeMailto(string link)
    {
        MailtoLink parsed;
        try { parsed = MailtoLink.Parse(link); }
        catch (Exception error) { ShowError("Could not open the mail link", error); return; }
        _ = ComposeAsync(new ComposeSeed("New Message", To: parsed.To, Cc: parsed.Cc, Bcc: parsed.Bcc,
            Subject: parsed.Subject, Body: parsed.Body));
    }
}
