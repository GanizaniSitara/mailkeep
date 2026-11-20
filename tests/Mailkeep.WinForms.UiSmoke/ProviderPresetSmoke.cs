using System.Reflection;
using Mailkeep.Core;
using Forms = System.Windows.Forms;

internal static class ProviderPresetSmoke
{
    public static int Run(string app, string? screenshots)
    {
        if (!Mailkeep.WinForms.UiSmokeAccess.IsInsidersFixture(app)) throw new InvalidOperationException("VM required");
        var assembly = Assembly.LoadFrom(Path.ChangeExtension(app, ".dll"));
        var chooserType = assembly.GetType("Mailkeep.WinForms.ProviderChoiceForm", true)!;
        var formType = assembly.GetType("Mailkeep.WinForms.ServerAccountForm", true)!;
        var providers = new[] {
            (Key: "yahoo", Label: "Yahoo", Pop: "pop.mail.yahoo.com", Imap: "imap.mail.yahoo.com", Smtp: "smtp.mail.yahoo.com", Port: 465),
            (Key: "aol", Label: "AOL", Pop: "pop.aol.com", Imap: "imap.aol.com", Smtp: "smtp.aol.com", Port: 465),
            (Key: "icloud", Label: "iCloud", Pop: "", Imap: "imap.mail.me.com", Smtp: "smtp.mail.me.com", Port: 587),
            (Key: "fastmail", Label: "Fastmail", Pop: "pop.fastmail.com", Imap: "imap.fastmail.com", Smtp: "smtp.fastmail.com", Port: 465)
        };
        foreach (var mode in new[] { MailboxModes.LocalKeep, MailboxModes.Connected })
        {
            foreach (var provider in providers)
            {
                using (var chooser = (Forms.Form)Activator.CreateInstance(chooserType, [mode])!)
                {
                    chooser.Show(); Forms.Application.DoEvents();
                    var contact = ComposeToolsSmoke.Controls(chooser).OfType<Forms.LinkLabel>().Single(c => c.AccessibleName == "Request another email provider");
                    Check(contact.Visible && (string)contact.Links[0].LinkData! == "https://github.com/GanizaniSitara/mailkeep/issues/new", "Provider request link missing or misrouted");
                    var button = ComposeToolsSmoke.Controls(chooser).OfType<Forms.Button>().Single(b => b.AccessibleName == "Add " + provider.Label + " account");
                    Check(button.Visible, "Provider choice missing: " + provider.Label);
                    Shot(chooser, "providers-" + mode);
                    button.PerformClick();
                    Check((string)chooserType.GetProperty("Choice")!.GetValue(chooser)! == provider.Key, "Wrong provider routing");
                }
                using var form = (Forms.Form)Activator.CreateInstance(formType, [mode, provider.Key])!;
                form.Show(); Forms.Application.DoEvents();
                var controls = ComposeToolsSmoke.Controls(form).ToArray();
                Forms.Control Named(string name) => controls.Single(c => c.AccessibleName == name);
                var protocol = (Forms.ComboBox)Named("Incoming protocol");
                Named("Email address").Text = "fixture@example.invalid";
                Named("Password").Text = "fake-app-password";
                var imap = mode == MailboxModes.Connected || provider.Key == "icloud";
                Check(protocol.SelectedIndex == (imap ? 1 : 0), "Wrong default protocol for " + provider.Label);
                Check(protocol.Enabled == (mode == MailboxModes.LocalKeep && provider.Key != "icloud"), "Invalid protocol choices enabled");
                Check(Named("Sign in").Text.Contains("password", StringComparison.OrdinalIgnoreCase), "App-password guidance missing");
                var account = (MailAccount)formType.GetProperty("Account")!.GetValue(form)!;
                Check(account.Incoming!.Host == (imap ? provider.Imap : provider.Pop) && account.Incoming.Port == (imap ? 993 : 995)
                    && account.Incoming.Security == MailSecurityModes.SslOnConnect, "Incoming preset incorrect: " + provider.Label);
                if (mode == MailboxModes.LocalKeep)
                {
                    var identity = (SendingIdentity)formType.GetProperty("Identity")!.GetValue(form)!;
                    Check(identity.Smtp.Host == provider.Smtp && identity.Smtp.Port == provider.Port
                        && identity.Smtp.Security == (provider.Port == 465 ? MailSecurityModes.SslOnConnect : MailSecurityModes.StartTls), "SMTP preset incorrect");
                    Check(!identity.IsReady, "Preset falsely marked account authenticated");
                    if (provider.Key == "icloud")
                    {
                        Named("Username").Text = "fixture";
                        identity = (SendingIdentity)formType.GetProperty("Identity")!.GetValue(form)!;
                        Check(identity.EffectiveUsername == "fixture@example.invalid", "iCloud SMTP did not use full address");
                    }
                    else
                    {
                        protocol.SelectedIndex = 1;
                        Check(((MailAccount)formType.GetProperty("Account")!.GetValue(form)!).Incoming!.Host == provider.Imap, "IMAP preset switch failed");
                        protocol.SelectedIndex = 0;
                    }
                }
                else Check(formType.GetProperty("Identity")!.GetValue(form) is null, "Connected preset unexpectedly created sender");
                Shot(form, provider.Key + "-" + mode);
                var presets = (Forms.ComboBox)Named("Preset");
                presets.SelectedIndex = 0;
                Check(Named("Password").Text.Length == 0, "Provider switch retained another provider's password");
                Check(!presets.Items.Cast<string>().Any(s => s.Contains("Proton")), "Deferred Proton exposed as supported");
                form.Close();
            }
        }
        Console.WriteLine("PASS provider-presets Yahoo AOL iCloud Fastmail local+connected SSL/STARTTLS ports IMAP-switch iCloud-no-POP full-SMTP-login password-guidance untested-status password-clear no-network");
        return 0;

        void Shot(Forms.Form form, string name)
        {
            foreach (var (label, area) in new[] {
                ("landscape", new System.Drawing.Rectangle(0, 0, 1280, 960)),
                ("portrait-offset", new System.Drawing.Rectangle(820, 0, 768, 1024)) })
            {
                form.Location = new System.Drawing.Point(area.Left + 16, area.Top + 16); Forms.Application.DoEvents();
                Check(area.Contains(form.Bounds), "Preset dialog exceeds simulated work area");
                Check(ComposeToolsSmoke.Controls(form).OfType<Forms.Button>().Where(b => b.Visible)
                    .All(b => form.ClientRectangle.Contains(form.RectangleToClient(b.RectangleToScreen(b.ClientRectangle)))), "Preset button clipped");
                Check(ComposeToolsSmoke.Controls(form).OfType<Forms.LinkLabel>().Where(b => b.Visible)
                    .All(b => form.ClientRectangle.Contains(form.RectangleToClient(b.RectangleToScreen(b.ClientRectangle)))), "Provider request link clipped");
                if (screenshots is null) continue;
                Directory.CreateDirectory(screenshots);
                using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                bitmap.Save(Path.Combine(screenshots, name + "-" + label + ".png"));
            }
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
