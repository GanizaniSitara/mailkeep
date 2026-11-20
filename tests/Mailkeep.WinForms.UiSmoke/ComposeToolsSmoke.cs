using System.Diagnostics;
using System.Reflection;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

internal static class ComposeToolsSmoke
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static IEnumerable<Forms.Control> Controls(Forms.Control parent)
    {
        foreach (Forms.Control control in parent.Controls) { yield return control; foreach (var child in Controls(control)) yield return child; }
    }
    internal static void Run(Assembly assembly, Forms.Form main, string root, MailkeepServices services, SendingIdentity identity, string? screenshots = null)
    {
        var type = assembly.GetType("Mailkeep.WinForms.ComposeForm", true)!;
        var seedType = assembly.GetType("Mailkeep.WinForms.ComposeSeed", true)!;
        var optionsType = assembly.GetType("Mailkeep.WinForms.OptionsForm", true)!;
        services.Database.SaveContact(new Contact(0, "Alice Example", "alice@example.invalid"));
        services.Database.SaveContact(new Contact(0, "Alice Other", "other@example.invalid"));
        services.Database.SaveContact(new Contact(0, "Unique Example", "unique@example.invalid"));
        // Cancel must not persist either compose preference; OK must survive a fresh Options dialog.
        foreach (var save in new[] { false, true })
        {
            using var options = (Forms.Form)Activator.CreateInstance(optionsType, [services, false, "Alphabetical", null])!;
            options.Show(main);
            var tabs = Controls(options).OfType<Forms.TabControl>().Single(); tabs.SelectedIndex = 2;
            var choice = Controls(options).OfType<Forms.ComboBox>().Single(control => control.AccessibleName == "Tab after To");
            Check(choice.SelectedIndex == 0 && choice.Text == "Subject field", "default To Tab is not Subject");
            choice.SelectedIndex = 1;
            Controls(options).OfType<Forms.TextBox>().Single(control => control.AccessibleName == "Signature").Text = "Fixture signature";
            if (save) OptionsSaveSmoke.Save(options);
            else { options.DialogResult = Forms.DialogResult.Cancel; options.Close(); }
            Check(services.Database.GetSetting("compose.tab_after_to") == (save ? "cc" : null), "Options Cancel saved preference");
            Check(services.Database.GetSetting("compose.signature") == (save ? "Fixture signature" : null), "Options Cancel saved signature");
        }
        using (var options = (Forms.Form)Activator.CreateInstance(optionsType, [services, false, "Alphabetical", null])!)
        {
            Check(Controls(options).OfType<Forms.ComboBox>().Single(control => control.AccessibleName == "Tab after To").SelectedIndex == 1, "preference not loaded");
            Check(Controls(options).OfType<Forms.TextBox>().Single(control => control.AccessibleName == "Signature").Text == "Fixture signature", "signature not loaded");
        }
        foreach (var html in new[] { false, true })
        {
            Console.WriteLine("CHECK compose-tools html=" + html);
            var seed = Activator.CreateInstance(seedType, ["New Message", identity.Id, "recipient@example.invalid", "", "", "Tools fixture", "mispellled", null, null, null, null, null]);
            using var compose = (Forms.Form)Activator.CreateInstance(type, [new[] { identity }, seed, html ? Path.Combine(root, "editor-tools") : null, services.Database])!;
            compose.Show(main);
            var toolbar = Controls(compose).OfType<Forms.ToolStrip>().Single(control => control.AccessibleName == "Compose toolbar");
            Forms.ToolStripButton Tool(string name) => toolbar.Items.OfType<Forms.ToolStripButton>().Single(button => button.AccessibleName == name);
            foreach (var name in new[] { "Send", "Cut", "Copy", "Paste", "Undo", "Check Names", "Spelling", "Attach", "Insert Signature" })
                Check(Tool(name).Image is { Width: 32, Height: 32 }, "compose action icon missing: " + name);
            var formatting = Controls(compose).OfType<Forms.ToolStrip>().Single(control => control.AccessibleName == "Formatting toolbar");
            Check(formatting.Items.OfType<Forms.ToolStripButton>().All(button => button.DisplayStyle == Forms.ToolStripItemDisplayStyle.Image && button.ToolTipText.Length > 0), "formatting buttons are not compact or lack tooltips");
            Check(formatting.Items.OfType<Forms.ToolStripComboBox>().Single(choice => choice.AccessibleName == "Font").Width >= 180
                && formatting.Items.OfType<Forms.ToolStripComboBox>().Single(choice => choice.AccessibleName == "Font size").Width <= 50,
                "font dropdowns do not match the compact OE layout");
            foreach (var label in new[] { "To:", "Cc:", "Bcc:" })
            {
                var button = Controls(compose).OfType<Forms.Button>().Single(control => control.AccessibleName == label + " Select Recipients");
                Check(button.FlatStyle == Forms.FlatStyle.Flat && button.FlatAppearance.BorderSize == 0, "recipient button has a border: " + label);
                Check(!button.Font.Bold && button.TextAlign == System.Drawing.ContentAlignment.MiddleCenter, "recipient caption differs from OE alignment: " + label);
                Modal("AddressBookForm", dialog => dialog.DialogResult = Forms.DialogResult.Cancel, button.PerformClick);
            }
            var to = (Forms.TextBox)Get(compose, "_to")!; var cc = (Forms.TextBox)Get(compose, "_cc")!;
            var plain = (Forms.TextBox)Get(compose, "_plain")!;
            var editor = (Forms.Control)Get(compose, "_editor")!;
            dynamic core = null!;
            Wait(() => html ? editor.GetType().GetProperty("CoreWebView2")!.GetValue(editor) is not null : plain.Visible, "compose tools editor not ready"); Pump(400);
            if (html) core = editor.GetType().GetProperty("CoreWebView2")!.GetValue(editor)!;
            foreach (var field in new[] { "_to", "_cc", "_bcc", "_subject" })
            {
                var input = (Forms.TextBox)Get(compose, field)!;
                var previous = input.Text;
                compose.Activate(); input.Focus(); input.SelectAll();
                Wait(() => GetForegroundWindow() == compose.Handle && input.Focused, "compose input focus failed: " + field);
                Forms.SendKeys.SendWait("typed"); Pump(100);
                Check(input.Text == "typed", "compose typing failed: " + field);
                Forms.Clipboard.SetText(" pasted"); Forms.SendKeys.SendWait("^v"); Pump(100);
                Check(input.Text == "typed pasted", "compose Ctrl+V failed: " + field);
                input.Text = previous;
            }
            compose.Activate();
            if (html)
            {
                editor.Focus();
                Await((Task)core.ExecuteScriptAsync("document.body.innerHTML='';document.body.focus();"));
            }
            else { plain.Focus(); plain.SelectAll(); }
            Forms.SendKeys.SendWait("typed"); Pump(150);
            var typedBody = html ? Await((Task<string>)editor.GetType().GetMethod("GetTextAsync")!.Invoke(editor, null)!) : plain.Text;
            Check(typedBody.Contains("typed"), "compose body typing failed html=" + html);
            Forms.Clipboard.SetText(" pasted"); Forms.SendKeys.SendWait("^v"); Pump(150);
            var pastedBody = html ? Await((Task<string>)editor.GetType().GetMethod("GetTextAsync")!.Invoke(editor, null)!) : plain.Text;
            // Chromium preserves a pasted leading space as &nbsp; in contenteditable.
            Check(pastedBody.Replace('\u00a0', ' ') == "typed pasted",
                "compose body Ctrl+V failed html=" + html + ": " + System.Text.Json.JsonSerializer.Serialize(pastedBody));
            plain.Text = "mispellled";
            Console.WriteLine("PASS compose typing and native Ctrl+V: To, CC, BCC, Subject, body html=" + html);
            if (!html && screenshots is not null)
            {
                Directory.CreateDirectory(screenshots);
                var originalBounds = compose.Bounds;
                foreach (var (name, bounds) in new[] {
                    ("landscape", new System.Drawing.Rectangle(0, 0, 1024, 760)),
                    ("portrait-secondary", new System.Drawing.Rectangle(1920, -386, 620, 800)) })
                {
                    compose.Bounds = bounds; Pump(100);
                    using var bitmap = new System.Drawing.Bitmap(compose.Width, compose.Height);
                    compose.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, compose.Size));
                    bitmap.Save(Path.Combine(screenshots, "compose-borderless-" + name + ".png"));
                }
                compose.Bounds = originalBounds;
            }
            if (html) Await((Task)core.ExecuteScriptAsync("document.body.innerHTML='<p><b>mispellled</b></p><p>Keep <i>formatting</i>.</p>';"));
            foreach (var destination in new[] { "subject", "body", "cc" })
            {
                services.Database.SetSetting("compose.tab_after_to", destination);
                foreach (var suggestion in new[] { false, true })
                {
                    compose.Activate(); to.Focus(); to.Text = suggestion ? "Alice E" : "recipient@example.invalid"; to.SelectionStart = to.Text.Length;
                    if (suggestion) type.GetMethod("Suggest", Flags)!.Invoke(compose, [to]);
                    Pump(60); Wait(() => { compose.Activate(); return GetForegroundWindow() == compose.Handle && to.Focused; }, "compose tools does not own keyboard focus");
                    Forms.SendKeys.SendWait("{TAB}"); Pump(100);
                    var subject = (Forms.TextBox)Get(compose, "_subject")!;
                    Wait(() => { compose.Activate(); return destination == "cc" ? cc.Focused : subject.Focused; }, "native To Tab failed: " + destination + "/html=" + html + "/suggestion=" + suggestion + "/text=" + to.Text + "/active=" + compose.ActiveControl?.AccessibleName);
                    if (suggestion) Check(to.Text.Contains("alice@example.invalid"), "Tab did not accept address suggestion: " + destination + "/html=" + html + "/text=" + to.Text);
                    if (destination != "cc")
                    {
                        Forms.SendKeys.SendWait("{TAB}"); Pump(100);
                        Wait(() => html ? editor.ContainsFocus : plain.Focused, "native Subject Tab failed html=" + html);
                    }
                }
            }
            to.Focus(); to.Text = "Alice E"; to.SelectionStart = to.Text.Length; type.GetMethod("Suggest", Flags)!.Invoke(compose, [to]);
            Forms.SendKeys.SendWait("+{TAB}"); Pump(80);
            Check(!to.Text.Contains("alice@example.invalid") && !to.Focused, "Shift+Tab accepted suggestion");
            Console.WriteLine("CHECK native tabs passed");
            to.Text = "Unique Example; recipient@example.invalid"; cc.Text = "";
            Tool("Check Names").PerformClick();
            Check(to.Text.Contains("unique@example.invalid") && to.Text.Contains("recipient@example.invalid"), "Check Names failed unique/contact plus existing email");
            to.Text = "Unique Example; Alice";
            Modal("ResolveNameForm", dialog => dialog.DialogResult = Forms.DialogResult.Cancel, () => Press(compose, Forms.Keys.Alt | Forms.Keys.K));
            Check(to.Text == "Unique Example; Alice", "cancel partially rewrote recipients");
            Modal("ResolveNameForm", dialog => { Controls(dialog).OfType<Forms.ListBox>().Single().SelectedIndex = 1; dialog.AcceptButton!.PerformClick(); }, () => Press(compose, Forms.Keys.Alt | Forms.Keys.K));
            Check(to.Text.Contains("unique@example.invalid") && to.Text.Contains("other@example.invalid"), "ambiguous recipient choice ignored");
            to.Text = "Nobody Here";
            Modal("ResolveNameForm", dialog => dialog.DialogResult = Forms.DialogResult.Cancel, () => Press(compose, Forms.Keys.Control | Forms.Keys.K));
            Check(to.Text == "Nobody Here", "unknown name invented a recipient");
            Console.WriteLine("CHECK names passed");
            if (html)
            {
                Press(compose, Forms.Keys.F4); Pump(100);
                using (var find = compose.OwnedForms.Single(form => form.Text == "Find Text"))
                {
                    Controls(find).OfType<Forms.TextBox>().Single().Text = "formatting";
                    Await((Task)find.GetType().GetMethod("FindNextAsync", Flags)!.Invoke(find, null)!);
                    Check(Await<string>((Task<string>)core.ExecuteScriptAsync("window.getSelection().toString()")) == "\"formatting\"", "HTML F4 find failed"); find.Close();
                }
                Await((Task)core.ExecuteScriptAsync("document.body.focus();const r=document.createRange();r.selectNodeContents(document.body);r.collapse(false);const s=window.getSelection();s.removeAllRanges();s.addRange(r);"));
            }
            else
            {
                plain.Text = "mispellled\r\nFind fixture Find";
                Press(compose, Forms.Keys.F4);
                using var find = compose.OwnedForms.Single(form => form.Text == "Find Text");
                Controls(find).OfType<Forms.TextBox>().Single().Text = "Find";
                find.GetType().GetMethod("FindNext", Flags)!.Invoke(find, null); var first = plain.SelectionStart;
                Check(plain.SelectedText == "Find", "plain F4 not selecting match");
                find.GetType().GetMethod("FindNext", Flags)!.Invoke(find, null); Check(plain.SelectionStart > first, "Find Next not advancing");
                find.GetType().GetMethod("FindNext", Flags)!.Invoke(find, null); Check(plain.SelectionStart == first, "Find Next not wrapping");
                find.Close(); plain.SelectionStart = plain.Text.Length; plain.SelectionLength = 0;
            }
            Tool("Insert Signature").PerformClick(); Pump(150);
            Console.WriteLine("CHECK find and signature passed");
            var before = html ? Await((Task<string>)editor.GetType().GetMethod("GetTextAsync")!.Invoke(editor, null)!) : plain.Text;
            Check(before.Contains("Fixture signature"), "signature not inserted");
            Modal("SpellingForm", dialog => dialog.DialogResult = Forms.DialogResult.Cancel, () => Tool("Spelling").PerformClick());
            var unchanged = html ? Await((Task<string>)editor.GetType().GetMethod("GetTextAsync")!.Invoke(editor, null)!) : plain.Text;
            Check(unchanged == before, "Cancel spelling changed body");
            Modal("SpellingForm", dialog =>
            {
                var word = Controls(dialog).OfType<Forms.Label>().Single(control => control.AccessibleName == "Spelling word");
                var replacement = Controls(dialog).OfType<Forms.TextBox>().Single(control => control.AccessibleName == "Spelling replacement");
                for (var i = 0; i < 100 && !Controls(dialog).OfType<Forms.Button>().Single(button => button.Text == "Finish").Enabled; i++)
                {
                    var change = word.Text.Contains("mispellled");
                    if (change) replacement.Text = "misspelled";
                    Controls(dialog).OfType<Forms.Button>().Single(button => button.Text == (change ? "Change" : "Ignore")).PerformClick();
                }
                Controls(dialog).OfType<Forms.Button>().Single(button => button.Text == "Finish").PerformClick();
            }, () => Press(compose, Forms.Keys.F7));
            string corrected = "";
            Wait(() => { corrected = html ? Await((Task<string>)editor.GetType().GetMethod("GetTextAsync")!.Invoke(editor, null)!) : plain.Text; return corrected.Contains("misspelled") && !corrected.Contains("mispellled"); }, "F7 correction did not finish: " + before);
            Check(corrected.Contains("misspelled") && !corrected.Contains("mispellled"), "F7 correction not applied: before=" + before + "; corrected=" + corrected);
            if (html)
            {
                var markup = Await((Task<string>)editor.GetType().GetMethod("GetHtmlAsync")!.Invoke(editor, null)!);
                Check(markup.Contains("<b>misspelled</b>") && markup.Contains("<i>formatting</i>"), "spelling lost rich formatting");
            }
            Press(compose, Forms.Keys.F1);
            using (var help = compose.OwnedForms.Single(form => form.Text == "Mailkeep Help"))
            { Check(Controls(help).OfType<Forms.TextBox>().Any(control => control.Text.Contains("Alt+S")), "F1 help lacks topics"); help.Close(); }
            type.GetField("_discard", Flags)!.SetValue(compose, true); compose.Close();
        }
        services.Database.SetSetting("compose.tab_after_to", "subject");
        Console.WriteLine("PASS compose-tools native-to-tab=subject,legacy-body,cc,suggestions subject-tab=body shift-tab=backwards options=save,cancel,persist find=f4,next,wrap,html names=unique,ambiguous,cancel signature=plain,html spelling=f7,cancel,format-preserved help=f1");
    }
    private static object? Get(object value, string field) => value.GetType().GetField(field, Flags)!.GetValue(value);
    private static void Press(Forms.Form form, Forms.Keys keys)
    { var message = Forms.Message.Create(form.Handle, 0x100, (IntPtr)(int)(keys & Forms.Keys.KeyCode), IntPtr.Zero); Check((bool)form.GetType().GetMethod("ProcessCmdKey", Flags)!.Invoke(form, [message, keys])!, "key not handled " + keys); }
    private static void Modal(string type, Action<Forms.Form> respond, Action trigger)
    {
        using var timer = new Forms.Timer { Interval = 40 }; var finished = false; Exception? failure = null;
        timer.Tick += (_, _) =>
        {
            var dialog = Forms.Application.OpenForms.Cast<Forms.Form>().FirstOrDefault(form => form.GetType().Name == type);
            if (dialog is null) return; timer.Stop();
            try { respond(dialog); finished = true; } catch (Exception error) { failure = error; dialog.DialogResult = Forms.DialogResult.Cancel; }
        };
        timer.Start(); trigger(); Wait(() => finished || failure is not null, "dialog not opened " + type); if (failure is not null) throw failure;
    }
    private static void Await(Task task) { Wait(() => task.IsCompleted, "async tool timed out"); task.GetAwaiter().GetResult(); }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Pump(int ms) { var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < ms) { Forms.Application.DoEvents(); Thread.Sleep(10); } }
    private static void Wait(Func<bool> done, string error) { var clock = Stopwatch.StartNew(); while (!done()) { if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(error); Pump(10); } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
