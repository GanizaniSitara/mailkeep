using System.Net;
using System.Net.Sockets;
namespace Mailkeep.TestFixtures;

public sealed class ImapFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public List<string> Stores { get; } = [];
        public List<string> Commands { get; } = [];
        public List<string> Moves { get; } = [];
        public List<string> Expunges { get; } = [];
        public volatile bool Offline;
        public int MoveDelayMs;
        public TaskCompletionSource MoveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HashSet<uint> MovedUids { get; } = [];
        public bool DropNextMoveAcknowledgement;
        public ImapFixture(bool rejectStore, uint validity, bool move = false, bool rejectSecond = false, bool rejectExpunge = false)
        {
            _listener.Start();
            _worker = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream);
                    using var writer = new StreamWriter(stream) { NewLine = "\r\n", AutoFlush = true };
                    var capabilities = "IMAP4rev1 AUTH=PLAIN SASL-IR" + (move ? " MOVE UIDPLUS" : "");
                    await writer.WriteLineAsync($"* OK [CAPABILITY {capabilities}] fixture ready");
                    while (await reader.ReadLineAsync(_stop.Token) is { } line)
                    {
                        var parts = line.Split(' ', 3);
                        var tag = parts[0];
                        var command = parts[1].ToUpperInvariant();
                        Commands.Add(command);
                        if (command == "CAPABILITY") await writer.WriteLineAsync("* CAPABILITY " + capabilities);
                        else if (command == "LIST")
                        {
                            if (line.Contains("Deleted")) await writer.WriteLineAsync("* LIST (\\HasNoChildren) \"/\" \"Deleted\"");
                            else if (line.Contains("Filed")) await writer.WriteLineAsync("* LIST (\\HasNoChildren) \"/\" \"Filed\"");
                            else
                            {
                                await writer.WriteLineAsync("* LIST (\\HasNoChildren) \"/\" \"INBOX\"");
                                if (move && line.Contains('*')) await writer.WriteLineAsync("* LIST (\\HasNoChildren) \"/\" \"Filed\"");
                            }
                        }
                        else if (command == "STATUS") await writer.WriteLineAsync("* STATUS INBOX (MESSAGES 2)");
                        else if (command == "SELECT")
                        {
                            await writer.WriteLineAsync("* FLAGS (\\Seen \\Deleted)");
                            await writer.WriteLineAsync("* 2 EXISTS");
                            await writer.WriteLineAsync($"* OK [UIDVALIDITY {validity}] valid");
                            await writer.WriteLineAsync("* OK [PERMANENTFLAGS (\\Seen \\Deleted)] flags");
                        }
                        else if (command == "UID" && parts[2].StartsWith("STORE ", StringComparison.Ordinal))
                        {
                            Stores.Add(line);
                            if (rejectStore) { await writer.WriteLineAsync(tag + " NO refused"); continue; }
                        }
                        else if (command == "UID" && parts[2].StartsWith("MOVE ", StringComparison.Ordinal))
                        {
                            Moves.Add(line);
                            MoveStarted.TrySetResult();
                            if (MoveDelayMs > 0) await Task.Delay(MoveDelayMs, _stop.Token);
                            if (Offline) { await writer.WriteLineAsync(tag + " NO fixture offline"); continue; }
                            if (rejectSecond && Moves.Count == 2) { await writer.WriteLineAsync(tag + " NO refused"); continue; }
                            foreach (var segment in parts[2].Split(' ')[1].Split(','))
                            {
                                var range = segment.Split(':').Select(uint.Parse).ToArray();
                                for (var uid = range[0]; uid <= range[^1]; uid++) MovedUids.Add(uid);
                            }
                            if (DropNextMoveAcknowledgement) { DropNextMoveAcknowledgement = false; break; }
                        }
                        else if (command == "UID" && parts[2].StartsWith("EXPUNGE ", StringComparison.Ordinal))
                        {
                            Expunges.Add(line);
                            if (rejectExpunge) { await writer.WriteLineAsync(tag + " NO refused"); continue; }
                        }
                        else if (command == "LOGOUT")
                        {
                            await writer.WriteLineAsync("* BYE finished");
                            await writer.WriteLineAsync(tag + " OK finished");
                            break;
                        }
                        else if (command is not ("AUTHENTICATE" or "LOGIN"))
                            throw new InvalidOperationException("Unexpected fixture command: " + line);
                        await writer.WriteLineAsync(tag + (command == "SELECT" ? " OK [READ-WRITE] selected" : " OK done"));
                    }
                }
            });
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _worker; } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }
