using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Mailkeep.Core;
using Mailkeep.Infrastructure;

internal static class CliSendSmoke
{
    public static async Task Run(string app, string root, MailkeepServices services)
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var serving = Task.Run(async () =>
        {
            try { while (true) (await server.AcceptTcpClientAsync()).Dispose(); }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        });
        try
        {
            var identity = services.Identities.Load().Single(value => value.Id == "fixture-3");
            services.Identities.Save([identity with { Smtp = new OutgoingServerSettings("127.0.0.1", ((IPEndPoint)server.LocalEndpoint).Port, MailSecurityModes.None) }]);
            var target = services.Send.SaveDraft(identity.Id, ["target@example.invalid"], "Target fixture", "Fixture body");
            var unrelated = services.Send.SaveDraft(identity.Id, ["other@example.invalid"], "Unrelated fixture", "Fixture body");
            await services.Send.QueueAsync(target.Id);
            await services.Send.QueueAsync(unrelated.Id);
            await Fails("send", target.Id);
            Check(Attempts(target.Id) == 0 && Attempts(unrelated.Id) == 0, "Missing --execute changed Outbox.");
            await Fails("send", "missing-id", "--execute");
            Check(Attempts(target.Id) == 0 && Attempts(unrelated.Id) == 0, "Unknown target sent unrelated mail.");
            await Fails("send", target.Id, "--unexpected", "--execute");
            Check(Attempts(target.Id) == 0 && Attempts(unrelated.Id) == 0, "Invalid arguments sent mail.");
            await Fails("send", target.Id, "--execute");
            Check(Attempts(target.Id) == 1 && Attempts(unrelated.Id) == 0, "Targeted send attempted unrelated mail.");
            await Fails("send-self", "--identity", identity.Id, "--subject", "Self fixture", "--body", "Fixture body", "--execute");
            Check(Attempts(target.Id) == 1 && Attempts(unrelated.Id) == 0, "Self-send attempted existing Outbox mail.");
            var self = services.Database.ListOutgoing().Single(value => value.Subject == "Self fixture");
            Check(self.AttemptCount == 1, "Self-send did not attempt its own message.");
            Console.WriteLine("PASS cli-send execute=required invalid-target-and-args=no-attempts targeted=isolated self=isolated smtp=loopback-failure-only");

            int Attempts(string id) => services.Database.GetOutgoing(id)!.AttemptCount;

            async Task Fails(params string[] arguments)
            {
                var start = new ProcessStartInfo(Path.GetFullPath(app)) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                start.ArgumentList.Add("--root");
                start.ArgumentList.Add(root);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI did not start.");
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch { process.Kill(true); throw; }
                Check(process.ExitCode != 0, "Expected a safe failure: " + await output + await errors);
            }
        }
        finally
        {
            server.Stop();
            await serving;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
