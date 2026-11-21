using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Mailkeep.TestFixtures;

/// <summary>Disposable POP/IMAP receive peer. Invented MIME, loopback only, no sending or server deletion.</summary>
public sealed class ReceiveFixture : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public bool HoldDownload;
    public bool RejectLogin;
    public bool HoldLogin;
    public TaskCompletionSource ReleaseLogin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Downloads;
    public uint[] Uids = [1, 2];
    public HashSet<uint> SeenUids { get; } = [];
    public uint UidValidity = 1;
    public Dictionary<uint, string> ImapMime { get; } = [];
    public bool RejectSearch;
    public bool RejectFlags;
    public bool RejectDownload;
    public uint? RejectDownloadUid;
    public bool HoldFlags;
    public TaskCompletionSource FlagsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseFlags { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string> Commands { get; } = [];
    public List<string> UidCommands { get; } = [];
    public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseDownload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ReceiveFixture(bool pop)
    {
        _listener.Start();
        _worker = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync(pop ? "+OK receive fixture" : "* OK [CAPABILITY IMAP4rev1 AUTH=PLAIN SASL-IR] receive fixture");
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    var parts = line.Split(' ', 3);
                    var tag = parts[0];
                    var command = (pop ? parts[0] : parts[1]).ToUpperInvariant();
                    lock (Commands) Commands.Add(command); // Never retain login arguments.
                    if (!pop && command == "UID") lock (UidCommands) UidCommands.Add(parts[2].Split(' ')[0]);
                    if (pop)
                    {
                        switch (command)
                        {
                            case "CAPA": await writer.WriteAsync("+OK\r\nUIDL\r\nUSER\r\n.\r\n"); break;
                            case "USER": await writer.WriteLineAsync("+OK"); break;
                            case "PASS":
                                if (HoldLogin) await ReleaseLogin.Task.WaitAsync(_stop.Token);
                                await writer.WriteLineAsync(RejectLogin ? "-ERR fixture login refused" : "+OK"); break;
                            case "STAT": await writer.WriteLineAsync("+OK 2 1000"); break;
                            case "UIDL": await writer.WriteAsync("+OK\r\n1 receive-1\r\n2 receive-2\r\n.\r\n"); break;
                            case "RETR":
                                await HoldAsync();
                                Interlocked.Increment(ref Downloads);
                                var mime = Message(int.Parse(parts[1]));
                                await writer.WriteAsync($"+OK {mime.Length}\r\n{mime}.\r\n"); break;
                            case "QUIT": await writer.WriteLineAsync("+OK"); goto NextClient;
                            default: await writer.WriteLineAsync("-ERR unsupported fixture command"); break;
                        }
                        continue;
                    }
                    if (command == "CAPABILITY") await writer.WriteLineAsync("* CAPABILITY IMAP4rev1 AUTH=PLAIN SASL-IR");
                    else if (command is "AUTHENTICATE" or "LOGIN")
                    {
                        if (HoldLogin) await ReleaseLogin.Task.WaitAsync(_stop.Token);
                        if (RejectLogin) { await writer.WriteLineAsync(tag + " NO fixture login refused"); continue; }
                    }
                    else if (command == "LIST") await writer.WriteLineAsync("* LIST (\\HasNoChildren) \"/\" \"INBOX\"");
                    else if (command is "EXAMINE" or "SELECT")
                    {
                        await writer.WriteAsync($"* FLAGS (\\Seen)\r\n* {Uids.Length} EXISTS\r\n* OK [UIDVALIDITY {UidValidity}] valid\r\n* OK [UIDNEXT {(Uids.Length == 0 ? 1 : Uids.Max() + 1)}] next\r\n");
                    }
                    else if (command == "UID" && parts[2].StartsWith("SEARCH"))
                    {
                        if (RejectSearch) { await writer.WriteLineAsync(tag + " NO fixture search failed"); continue; }
                        var range = Regex.Match(parts[2], @"UID (\d+):\*");
                        var start = range.Success ? int.Parse(range.Groups[1].Value) : 1;
                        await writer.WriteLineAsync("* SEARCH" + string.Concat(Uids.Where(uid => uid >= start).Select(uid => " " + uid)));
                    }
                    else if (command == "UID" && parts[2].StartsWith("FETCH"))
                    {
                        var set = parts[2].Split(' ')[1].Split(',').Select(segment => segment.Split(':').Select(uint.Parse).ToArray()).ToArray();
                        var selected = Uids.Where(uid => set.Any(range => uid >= range[0] && uid <= range[^1])).ToArray();
                        if (parts[2].Contains("BODY"))
                        {
                            if (RejectDownload || (RejectDownloadUid is { } rejected && selected.Contains(rejected)))
                            { await writer.WriteLineAsync(tag + " NO fixture download failed"); continue; }
                            await HoldAsync();
                            Interlocked.Increment(ref Downloads);
                            foreach (var uid in selected)
                            {
                                var mime = ImapMime.GetValueOrDefault(uid) ?? Message((int)uid);
                                await writer.WriteAsync($"* {Array.IndexOf(Uids, uid) + 1} FETCH (UID {uid} BODY[] {{{mime.Length}}}\r\n{mime})\r\n");
                            }
                        }
                        else
                        {
                            FlagsStarted.TrySetResult();
                            if (HoldFlags) await ReleaseFlags.Task.WaitAsync(_stop.Token);
                            if (RejectFlags) { await writer.WriteLineAsync(tag + " NO fixture flags failed"); continue; }
                            foreach (var uid in selected)
                                await writer.WriteLineAsync($"* {Array.IndexOf(Uids, uid) + 1} FETCH (UID {uid} FLAGS ({(SeenUids.Contains(uid) ? "\\Seen" : "")}))");
                        }
                    }
                    else if (command == "LOGOUT")
                    {
                        await writer.WriteLineAsync("* BYE finished");
                        await writer.WriteLineAsync(tag + " OK finished"); goto NextClient;
                    }
                    else throw new InvalidOperationException("Unexpected receive fixture command: " + command);
                    await writer.WriteLineAsync(tag + (command == "EXAMINE" ? " OK [READ-ONLY] selected"
                        : command == "SELECT" ? " OK [READ-WRITE] selected" : " OK done"));
                }
                NextClient:;
            }
        });
    }

    private async Task HoldAsync()
    {
        DownloadStarted.TrySetResult();
        if (HoldDownload) await ReleaseDownload.Task.WaitAsync(_stop.Token);
    }
    public static string Message(int number) => $"From: fixture@example.invalid\r\nTo: local@example.invalid\r\nSubject: Download fixture {number}\r\nMessage-Id: <receive-{number}@example.invalid>\r\nDate: Wed, 11 Nov 2026 10:00:00 +0000\r\n\r\nInvented receive body {number}.\r\n";
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop();
        try { await _worker; } catch (OperationCanceledException) { } catch (IOException) when (_stop.IsCancellationRequested) { }
        _stop.Dispose();
    }
}
