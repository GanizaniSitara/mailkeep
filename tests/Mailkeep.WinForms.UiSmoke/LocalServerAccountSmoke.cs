using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

internal static class LocalServerAccountSmoke
{
    public static int Run(string app, string? screenshots)
    {
        if (!Mailkeep.WinForms.UiSmokeAccess.IsInsidersFixture(app)) throw new InvalidOperationException("VM required");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var pop = new TcpListener(IPAddress.Loopback, 0);
        using var smtp = new TcpListener(IPAddress.Loopback, 0);
        pop.Start(); smtp.Start();
        var root = Path.Combine(Path.GetTempPath(), "mailkeep-local-servers-" + Guid.NewGuid().ToString("N"));
        var popTask = Serve(pop, false);
        var smtpTask = Serve(smtp, true);
        try
        {
            var services = new MailkeepServices(root); services.Database.Initialize();
            var type = Assembly.LoadFrom(Path.ChangeExtension(app, ".dll")).GetType("Mailkeep.WinForms.ServerAccountForm", true)!;
            using var dialog = (Forms.Form)Activator.CreateInstance(type, [MailboxModes.LocalKeep])!;
            dialog.Show(); Forms.Application.DoEvents();
            var controls = All(dialog).ToArray();
            Forms.Control Named(string name) => controls.Single(c => c.AccessibleName == name);
            void Text(string name, string value) => Named(name).Text = value;
            Check(((Forms.ComboBox)Named("Incoming protocol")).Text == "POP3", "Local account did not default to POP3");
            Check(((Forms.NumericUpDown)Named("Outgoing port")).Value == 587, "SMTP default port wrong");
            Text("Email address", "fixture@example.invalid"); Text("Account name", "local-server-fixture");
            Text("Username", "pop-user"); Text("Password", "pop-secret");
            Text("Incoming server", "127.0.0.1");
            ((Forms.NumericUpDown)Named("Incoming port")).Value = ((IPEndPoint)pop.LocalEndpoint).Port;
            ((Forms.ComboBox)Named("Incoming security")).SelectedIndex = 3;
            Text("From name", "Local Fixture"); Text("Outgoing server", "127.0.0.1");
            ((Forms.NumericUpDown)Named("Outgoing port")).Value = ((IPEndPoint)smtp.LocalEndpoint).Port;
            ((Forms.ComboBox)Named("Outgoing security")).SelectedIndex = 3;
            Text("Outgoing username", "smtp-user"); Text("Outgoing password", "smtp-secret");
            if (screenshots is not null)
            {
                Directory.CreateDirectory(screenshots);
                LayoutShots(dialog, "local-pop-smtp-setup");
            }
            var add = (Forms.Button)Named("Add account");
            Check(add.Visible && add.Top >= 0 && add.PointToScreen(System.Drawing.Point.Empty).Y + add.Height <= Forms.Screen.FromControl(dialog).WorkingArea.Bottom,
                "Add account button clipped");
            add.PerformClick(); Forms.Application.DoEvents();
            Check(dialog.DialogResult == Forms.DialogResult.OK, "Valid combined setup did not save");
            var account = (MailAccount)type.GetProperty("Account")!.GetValue(dialog)!;
            var identity = (SendingIdentity)type.GetProperty("Identity")!.GetValue(dialog)!;
            Check(!account.IsConnected && account.EffectiveProtocol == MailProtocols.Pop3, "Wrong incoming mode");
            Check(identity.Id == account.Name && !identity.IsReady, "Sender must be paired and untested");
            var propertiesType = type.Assembly.GetType("Mailkeep.WinForms.AccountPropertiesForm", true)!;
            using (var properties = (Forms.Form)Activator.CreateInstance(propertiesType, [account, identity])!)
            {
                var preserved = (SendingIdentity)propertiesType.GetProperty("Identity")!.GetValue(properties)!;
                Check(preserved.EffectiveUsername == "smtp-user" && preserved.Password == "smtp-secret", "Properties changed separate SMTP credentials");
                properties.Show(); Forms.Application.DoEvents();
                if (screenshots is not null) LayoutShots(properties, "local-pop-smtp-properties");
                properties.Close();
            }
            using (var defaults = (Forms.Form)Activator.CreateInstance(type, [MailboxModes.LocalKeep])!)
            {
                var fields = All(defaults).ToArray();
                void Set(string name, string value) => fields.Single(c => c.AccessibleName == name).Text = value;
                Set("Email address", "fallback@example.invalid"); Set("Username", "incoming-login"); Set("Password", "incoming-secret");
                Check(type.GetProperty("Identity")!.GetValue(defaults) is null, "Receive-only setup created an SMTP identity");
                Set("Outgoing server", "smtp.example.invalid");
                var fallback = (SendingIdentity)type.GetProperty("Identity")!.GetValue(defaults)!;
                Check(fallback.EffectiveUsername == "incoming-login" && fallback.Password == "incoming-secret", "SMTP login fallback incorrect");
            }
            services.Accounts.Upsert(account); services.Identities.Upsert(identity);
            var inspection = Task.Run(() => services.Sync.InspectPopAsync(services.Accounts.Load().Single(), timeout.Token)).GetAwaiter().GetResult();
            Check(inspection.MessageCount == 2 && inspection.NewMessageCount == 2 && inspection.TotalBytes == 30, "POP count inspection incorrect");
            Check(Task.Run(() => services.Send.TestAsync(identity.Id, timeout.Token)).GetAwaiter().GetResult().Ok, "SMTP authentication failed");
            Check(services.Identities.Load().Single().IsReady, "Successful SMTP test did not enable sender");
            Task.WhenAll(popTask, smtpTask).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Check(services.Database.ListOutgoing(OutgoingStates.Outbox).Count == 0, "Setup queued mail");
            using var connected = (Forms.Form)Activator.CreateInstance(type, [MailboxModes.Connected])!;
            Check(!All(connected).Any(c => c.AccessibleName == "Outgoing server"), "Connected add form changed");
            Console.WriteLine("PASS local-pop-smtp combined-fields distinct-credentials encrypted-store-reload POP-count-only=2 SMTP-auth-only ready-after-test no-RETR no-DELE no-MAIL no-DATA connected-preserved");
            return 0;

            void LayoutShots(Forms.Form form, string name)
            {
                // Simulated landscape/portrait work areas on the single-screen guest.
                foreach (var (label, area) in new[] {
                    ("landscape", new System.Drawing.Rectangle(0, 0, 1280, 960)),
                    ("portrait-offset", new System.Drawing.Rectangle(820, 0, 768, 1024)) })
                {
                    form.Location = new System.Drawing.Point(area.Left + 16, area.Top + 16);
                    Forms.Application.DoEvents();
                    Check(area.Contains(form.Bounds), "Account dialog exceeds simulated work area " + label);
                    Check(All(form).OfType<Forms.Button>().Where(b => b.Visible).All(b => form.ClientRectangle.Contains(form.RectangleToClient(b.RectangleToScreen(b.ClientRectangle)))),
                        "Account dialog clips a button " + label);
                    using var image = new System.Drawing.Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                    image.Save(Path.Combine(screenshots!, name + "-" + label + ".png"));
                }
            }
        }
        finally
        {
            timeout.Cancel(); pop.Stop(); smtp.Stop();
            try { Task.WhenAll(popTask, smtpTask).Wait(TimeSpan.FromSeconds(3)); } catch { }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        async Task Serve(TcpListener listener, bool sending)
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };
            await writer.WriteLineAsync(sending ? "220 localhost fixture" : "+OK localhost fixture");
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (sending)
                {
                    if (line.StartsWith("EHLO")) await writer.WriteAsync("250-localhost\r\n250 AUTH PLAIN\r\n");
                    else if (line.StartsWith("AUTH PLAIN "))
                    {
                        Check(Encoding.UTF8.GetString(Convert.FromBase64String(line[11..])) == "\0smtp-user\0smtp-secret", "Wrong SMTP credential");
                        await writer.WriteLineAsync("235 authenticated");
                    }
                    else if (line == "QUIT") { await writer.WriteLineAsync("221 bye"); return; }
                    else throw new InvalidOperationException("Unexpected SMTP command; setup must not send mail");
                }
                else
                {
                    if (line == "CAPA") await writer.WriteAsync("+OK\r\nUSER\r\nUIDL\r\n.\r\n");
                    else if (line == "USER pop-user" || line == "PASS pop-secret") await writer.WriteLineAsync("+OK");
                    else if (line == "STAT") await writer.WriteLineAsync("+OK 2 30");
                    else if (line == "UIDL") await writer.WriteAsync("+OK\r\n1 fixture-a\r\n2 fixture-b\r\n.\r\n");
                    else if (line == "LIST") await writer.WriteAsync("+OK\r\n1 12\r\n2 18\r\n.\r\n");
                    else if (line == "QUIT") { await writer.WriteLineAsync("+OK"); return; }
                    else throw new InvalidOperationException("Unexpected POP command; setup must not retrieve/delete mail");
                }
            }
        }
    }
    private static IEnumerable<Forms.Control> All(Forms.Control parent)
    { foreach (Forms.Control child in parent.Controls) { yield return child; foreach (var nested in All(child)) yield return nested; } }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
