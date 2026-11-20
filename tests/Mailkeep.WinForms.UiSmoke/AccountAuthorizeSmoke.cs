using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mailkeep.Core;
using Forms = System.Windows.Forms;

internal static class AccountAuthorizeSmoke
{
    public static int Run(string app, string? screenshots)
    {
        if (!Mailkeep.WinForms.UiSmokeAccess.IsInsidersFixture(app))
            throw new InvalidOperationException("VM required");
        var type = Assembly.LoadFrom(app.Replace(".exe", ".dll")).GetType("Mailkeep.WinForms.AccountsForm", true)!;
        var accounts = new[] {
            new MailAccount("local:first@example.invalid", "outlook", "first@example.invalid", "INBOX", "inbox", null, null, null, null, MailboxModes.LocalKeep),
            new MailAccount("local:dcx45@example.invalid", "outlook", "dcx45@example.invalid", "INBOX", "inbox", null, null, null, null, MailboxModes.LocalKeep),
            new MailAccount("connected:other@example.invalid", "outlook", "other@example.invalid", "INBOX", "Other", null, null, null, null, MailboxModes.Connected)
        };
        var proof = new List<object>();
        foreach (var connected in new[] { false, true })
        {
            using var dialog = (Forms.Form)Activator.CreateInstance(type, [accounts, Array.Empty<SendingIdentity>(), Array.Empty<FetchAttempt>()])!;
            using var timer = new Forms.Timer { Interval = 200 };
            Exception? failure = null;
            timer.Tick += (_, _) => { timer.Stop(); try { Click(); } catch (Exception exception) { failure = exception; dialog.Close(); } };
            timer.Start();
            var result = dialog.ShowDialog();
            if (failure is not null) throw failure;
            var selected = type.GetProperty("SelectedAccountName")!.GetValue(dialog) as string;
            var action = type.GetProperty("RequestedAction")!.GetValue(dialog)!.ToString();
            var expected = accounts[connected ? 2 : 1].Name;
            if (selected != expected || action != "Authorize" || result != Forms.DialogResult.OK)
                throw new InvalidOperationException($"Authorize did not return selected account: {selected}, {action}, {result}");
            proof.Add(new { connected, selected, action, unobscured = true });
            void Click()
            {
            var tabs = All(dialog).OfType<Forms.TabControl>().Single();
            tabs.SelectedIndex = connected ? 1 : 0; Forms.Application.DoEvents();
            var grid = All(dialog).OfType<Forms.DataGridView>().Single(g => g.AccessibleName == (connected ? "Connected account list" : "Local account list"));
            var index = connected ? 0 : 1;
            grid.CurrentCell = grid.Rows[index].Cells.Cast<Forms.DataGridViewCell>().First(c => c.Visible);
            grid.Rows[index].Selected = true;
            var button = All(dialog).OfType<Forms.Button>().Single(b => b.AccessibleName == (connected ? "Authorize connected account" : "Authorize local account"));
            Forms.Application.DoEvents();
            var point = button.PointToScreen(new System.Drawing.Point(button.Width / 2, button.Height / 2));
            var hit = WindowFromPoint(point);
            if (screenshots is not null) {
                Directory.CreateDirectory(screenshots);
                using var image = new System.Drawing.Bitmap(dialog.Width, dialog.Height);
                dialog.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, dialog.Width, dialog.Height));
                image.Save(Path.Combine(screenshots, connected ? "authorize-connected.png" : "authorize-local.png"));
            }
            if (hit != button.Handle) throw new InvalidOperationException($"Authorize button obscured: hit={hit}, button={button.Handle}");
            button.PerformClick(); Forms.Application.DoEvents();
            }
        }
        if (screenshots is not null) File.WriteAllText(Path.Combine(screenshots, "authorize-actions.json"), JsonSerializer.Serialize(proof));
        Console.WriteLine("PASS account-authorize: local selected row and connected selected row return Authorize; both buttons unobscured; no network or credentials");
        return 0;
    }

    private static IEnumerable<Forms.Control> All(Forms.Control root)
    {
        foreach (Forms.Control child in root.Controls) { yield return child; foreach (var nested in All(child)) yield return nested; }
    }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(System.Drawing.Point point);
}
