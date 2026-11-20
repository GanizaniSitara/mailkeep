using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

/// <summary>Message-scoped native picture controls never include another message's saved sites; no browser or network is used.</summary>
internal static class ImageSiteScopeSmoke
{
    internal static void Run(string app, MailkeepServices services, string? screenshots)
    {
        const string other = "https://unrelated.example.invalid";
        const string signature = "https://signature.example.invalid";
        const string logo = "https://logo.example.invalid";
        services.Database.SetSetting("images.site_policies", JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [other] = "Always allow", [signature] = "Always allow", [logo] = "Ask"
        }));
        Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
        Forms.Application.EnableVisualStyles();
        Forms.Application.SetDefaultFont(new System.Drawing.Font("Tahoma", 8.25f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point));
        Forms.Application.SetCompatibleTextRenderingDefault(false);
        Exception? failure = null;
        var thread = new Thread(() => { try { RunUi(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(30)), "image-site scope timed out");
        if (failure is not null) throw new InvalidOperationException("image-site scope failed", failure);
        Console.WriteLine("PASS image-site-scope message=only-current-sites global=all-saved empty=no-saved-leak bulk=message-only save=merge cancel=no-write");

        void RunUi()
        {
            Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
            var directory = Path.GetDirectoryName(Path.GetFullPath(app))!;
            var assembly = Assembly.LoadFrom(Path.Combine(directory, "Mailkeep.WinForms.dll"));
            var type = assembly.GetType("Mailkeep.WinForms.ImageSitesForm", true)!;
            var permissionType = assembly.GetType("Mailkeep.WinForms.RemoteImagePermissions", true)!;
            var suggestions = assembly.GetType("Mailkeep.WinForms.TrackerSuggestions", true)!;
            string? Reason(string url) => (string?)suggestions.GetMethod("UrlReason")!.Invoke(null, [url]);
            foreach (var url in new[] { "https://images.example.invalid/open?recipient_id=42", "https://images.example.invalid/image?x=0123456789abcdef0123456789abcdef",
                "https://images.example.invalid/550e8400-e29b-41d4-a716-446655440000.png", "https://images.example.invalid/mail-sig/AIorK4ypgWupgWW4sTUIbi6tOZDX0FvuV7tZbjZAZztCUH8P29hspIVKB" })
                Check(Reason(url) is not null, "unique URL identifier missed: " + url);
            foreach (var url in new[] { "https://images.example.invalid/logo.png", "https://images.example.invalid/image?w=96&h=27", "https://images.example.invalid/social_icons/32px/linkedin.png", "https://images.example.invalid/really-long-descriptive-signature-logo.png" })
                Check(Reason(url) is null, "ordinary image falsely identified: " + url);
            var permissions = Activator.CreateInstance(permissionType, [services.Database])!;
            string Policy(string site) => (string)permissionType.GetMethod("Policy")!.Invoke(permissions, [site])!;
            Forms.Form Create(string[]? sites) => (Forms.Form)Activator.CreateInstance(type, [permissions, sites])!;
            using (var empty = Create([])) Check(!Controls(empty).OfType<Forms.RadioButton>().Any(), "empty message leaked saved sites");
            using (var global = Create(null))
            {
                Check(Sites(global).SetEquals([other, signature, logo]), "global editor lost saved sites");
                Check(Scope(global).StartsWith("All saved image domains"), "global scope label unclear");
            }
            const string listed = "https://doubleclick.net";
            const string personalised = "https://personalised.example.invalid";
            const string ordinary = "https://ordinary.example.invalid";
            var html = "<img src='" + listed + "/logo.png'><img src='" + personalised + "/open?recipient_id=42'><img src='" + ordinary + "/logo.png'><img src='" + signature + "/open?token=secret" + new string('A', 150) + "'>";
            var optionsType = assembly.GetType("Mailkeep.WinForms.OptionsForm", true)!;
            Forms.Form Options() => (Forms.Form)Activator.CreateInstance(optionsType, [services, false, "Alphabetical", null])!;
            using (var options = Options())
            {
                options.Show();
                var checkbox = Controls(options).OfType<Forms.CheckBox>().Single(control => control.AccessibleName == "Use DNS block-list recommendations");
                Check(checkbox.Checked, "DNS recommendations are not on by default");
                checkbox.Checked = false; options.DialogResult = Forms.DialogResult.Cancel; options.Close();
            }
            Check(services.Database.GetSetting("images.dns_suggestions") != "false", "Options Cancel changed DNS setting");
            using (var options = Options())
            {
                options.Show();
                Controls(options).OfType<Forms.CheckBox>().Single(control => control.AccessibleName == "Use DNS block-list recommendations").Checked = false;
                OptionsSaveSmoke.Save(options);
            }
            Check(services.Database.GetSetting("images.dns_suggestions") == "false", "Options did not persist disabled DNS recommendations");
            using (var disabled = (Forms.Form)Activator.CreateInstance(type, [permissions, new[] { listed, personalised }, html])!)
            {
                Check(Controls(disabled).OfType<Forms.RadioButton>().Single(button => button.Checked && button.AccessibleName!.EndsWith(" pictures from " + listed)).Text == "Ask", "disabled DNS list still suggests block");
                Check(Controls(disabled).OfType<Forms.RadioButton>().Single(button => button.Checked && button.AccessibleName!.EndsWith(" pictures from " + personalised)).Text == "Never allow", "disabled DNS list disabled identifier suggestions");
                Check(Scope(disabled).Contains("disabled in Options"), "disabled DNS status not visible");
            }
            services.Database.SetSetting("images.dns_suggestions", "true");
            services.Database.SetSetting("images.site_policies", JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [other] = "Always allow", [signature] = "Always allow", [logo] = "Ask", [listed] = "Never allow"
            }));
            const string assets = "https://assets.example.invalid";
            const string jobs = "https://jobs.example.invalid";
            const string pixel = "https://pixel.example.invalid";
            var signed = jobs + "/company-logo.png?e=1792627200&v=beta&t=AbCdEfGhIjKlMnOpQrStUvWxYz0123456789";
            var contextualHtml = $"<img alt='Network logo' width='101' height='37' src='{assets}/brand.png'>"
                + $"<a href='https://jobs.example.invalid/job/42'><img alt='Example company' width='48' height='48' src='{signed}'></a>"
                + $"<img alt='' width='1' height='1' src='{pixel}/open.gif'>";
            using (var contextDialog = (Forms.Form)Activator.CreateInstance(type, [permissions, new[] { assets, jobs, pixel }, contextualHtml])!)
            {
                contextDialog.Show();
                string Selected(string site) => Controls(contextDialog).OfType<Forms.RadioButton>().Single(button => button.Checked && button.AccessibleName!.EndsWith(" pictures from " + site)).Text;
                string Recommendation(string site) => Controls(contextDialog).Single(control => control.AccessibleName == "Blocking recommendation for " + site).Text;
                Check(Selected(assets) == "Ask" && Selected(jobs) == "Ask", "visible signed asset treated as a proven recipient identifier");
                Check(Selected(pixel) == "Never allow" && Recommendation(pixel).Contains("1×1"), "tracking pixel dimensions missed without an opaque URL");
                Check(Recommendation(jobs).Contains("recipient tracking is not established"), "signed asset uncertainty not explained");
                Check(Controls(contextDialog).Single(control => control.AccessibleName == "Image context for " + jobs).Text.Contains("Example company"), "visible job image context missing");
                Click(contextDialog, "Use tracker suggestions");
                Check(Selected(jobs) == "Ask" && Selected(pixel) == "Never allow", "bulk suggestions block the visible signed image");
                if (screenshots is not null)
                {
                    Directory.CreateDirectory(screenshots);
                    foreach (var (name, area) in new[] {
                        ("landscape", new System.Drawing.Rectangle(0, 0, 1920, 1160)),
                        ("portrait-secondary", new System.Drawing.Rectangle(1920, -386, 1200, 1880)) })
                    {
                        type.GetMethod("FitToWorkArea")!.Invoke(contextDialog, [area, area]);
                        var target = contextDialog.Bounds;
                        target.Width = (int)Math.Round(area.Width * 0.85);
                        Check(SetWindowPos(contextDialog.Handle, IntPtr.Zero, target.X, target.Y, target.Width, target.Height, 0x14), "context layout resize failed");
                        Forms.Application.DoEvents();
                        using var bitmap = new System.Drawing.Bitmap(contextDialog.Width, contextDialog.Height);
                        contextDialog.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
                        bitmap.Save(Path.Combine(screenshots, "image-context-" + name + ".png"));
                    }
                }
            }
            foreach (var source in new[] {
                $"<img width='48' height='48' src='{signed}&recipient_id=42'>",
                $"<img width='48' height='48' style='display:none' src='{signed}'>",
                $"<img width='48' height='48' style='width:1px;height:1px' src='{signed}'>",
                $"<img width='48' height='48' src='{signed}'><img width='1' height='1' src='{jobs}/pixel.gif'>" })
            {
                using var suspicious = (Forms.Form)Activator.CreateInstance(type, [permissions, new[] { jobs }, source])!;
                Check(Controls(suspicious).OfType<Forms.RadioButton>().Single(button => button.Checked).Text == "Never allow", "signed asset exception hid recipient, hidden, CSS pixel or mixed-domain evidence");
            }
            using (var suggestionsDialog = (Forms.Form)Activator.CreateInstance(type, [permissions, new[] { listed, personalised, ordinary, signature }, html])!)
            {
                suggestionsDialog.Show();
                Check(suggestionsDialog.Height <= Forms.Screen.FromControl(suggestionsDialog).WorkingArea.Height * 0.7 + 2, "dialog exceeds monitor height limit");
                Check(suggestionsDialog.Width <= Forms.Screen.FromControl(suggestionsDialog).WorkingArea.Width * 0.85 + 2, "dialog exceeds monitor width limit");
                string Selected(string site) => Controls(suggestionsDialog).OfType<Forms.RadioButton>().Single(button => button.Checked && button.AccessibleName!.EndsWith(" pictures from " + site)).Text;
                string Label(string site) => Controls(suggestionsDialog).Single(control => control.AccessibleName == "Classification for " + site).Text;
                Forms.Control Recommendation(string site) => Controls(suggestionsDialog).Single(control => control.AccessibleName == "Blocking recommendation for " + site);
                Check(Selected(listed) == "Never allow" && Recommendation(listed).Text.Contains("Recommended DNS block list: doubleclick.net"), "DNS suggestion source/preselection missing");
                Check(Selected(personalised) == "Never allow" && Recommendation(personalised).Text.Contains("Possible unique identifier") && Recommendation(personalised).Text.Contains("recipient_id"), "URL suggestion reason/preselection missing");
                Check(Selected(ordinary) == "Ask" && Recommendation(ordinary).Text.Contains("safety is unknown"), "unlisted ordinary URL should remain Ask");
                Check(Selected(signature) == "Always allow" && Label(signature) == "Current permission: Always allow (saved).\r\nImages load automatically.", "saved allow state unclear or overridden");
                Check(Label(listed) == "Current permission: Never allow (saved).\r\nImages are blocked.", "previously blocked state unclear");
                Check(Label(personalised) == "Current permission: Ask (default).\r\nNo saved permission for this domain." && Label(ordinary) == Label(personalised), "new domains confused with saved permissions");
                var previous = Controls(suggestionsDialog).Single(control => control.AccessibleName == "Classification for " + signature);
                var domain = Controls(suggestionsDialog).Single(control => control.AccessibleName == "Image domain " + signature);
                Check(previous.Parent!.Parent == domain && Controls(domain).Any(control => control.AccessibleName == "Always allow pictures from " + signature), "domain, current state and choices are not grouped together");
                Check(Controls(suggestionsDialog).Single(control => control.AccessibleName == "Permission change for " + signature).Text == "No change to saved permission.", "unchanged permission unclear");
                Check(Controls(suggestionsDialog).Single(control => control.AccessibleName == "Permission change for " + personalised).Text == "Save will set: Never allow (new permission).", "new suggested permission not labelled as pending Save");
                Check(previous.ForeColor == System.Drawing.Color.DarkGreen
                    && Controls(suggestionsDialog).Single(control => control.AccessibleName == "Classification for " + listed).ForeColor == System.Drawing.Color.Firebrick
                    && Controls(suggestionsDialog).Single(control => control.AccessibleName == "Classification for " + ordinary).ForeColor == System.Drawing.Color.FromArgb(128, 85, 0), "current permissions are not colour coded");
                Check(Controls(suggestionsDialog).Single(control => control.AccessibleName == "Permission change for " + signature).ForeColor == System.Drawing.Color.DimGray
                    && Controls(suggestionsDialog).Single(control => control.AccessibleName == "Permission change for " + personalised).ForeColor == System.Drawing.Color.RoyalBlue, "pending and unchanged permissions are not distinct");
                Check(Controls(suggestionsDialog).Any(control => control.AccessibleName == "Image permission colour legend"), "permission colour legend missing");
                foreach (var radio in Controls(suggestionsDialog).OfType<Forms.RadioButton>())
                    Check(radio.ForeColor == (radio.Text == "Always allow" ? System.Drawing.Color.DarkGreen : radio.Text == "Never allow" ? System.Drawing.Color.Firebrick : System.Drawing.Color.FromArgb(128, 85, 0)), "radio permission colour incorrect");
                Check(!Recommendation(personalised).Text.Contains("42") && !Recommendation(signature).Text.Contains("secret"), "identifier values leaked into reasons");
                var personalisedDomain = Controls(suggestionsDialog).Single(control => control.AccessibleName == "Image domain " + personalised);
                Check(Recommendation(personalised).Parent!.Parent == personalisedDomain && personalisedDomain is Forms.TableLayoutPanel personalisedRow && personalisedRow.GetColumn(Recommendation(personalised).Parent) == 1, "recommendation is not on the right of its domain's choices");
                Check(Recommendation(personalised).ForeColor == System.Drawing.Color.Firebrick && Recommendation(ordinary).ForeColor == System.Drawing.Color.FromArgb(128, 85, 0), "recommendation colours missing");
                Check(Controls(suggestionsDialog).OfType<Forms.Label>().Single(control => control.AccessibleName == ordinary).Font.Bold, "domain is not distinct from URL");
                var fullUrls = Controls(suggestionsDialog).OfType<Forms.TextBox>().Single(box => box.AccessibleName == "Full image URLs from " + personalised);
                Check(Controls(suggestionsDialog).OfType<Forms.TextBox>().All(box => !box.Visible), "full URLs are not collapsed by default");
                var collapsedHeight = suggestionsDialog.Height;
                ToggleUrls(suggestionsDialog, personalised);
                Check(fullUrls.Visible && Controls(suggestionsDialog).OfType<Forms.TextBox>().Where(box => box != fullUrls).All(box => !box.Visible), "URL expansion affected other domains");
                Check(Selected(personalised) == "Never allow" && Selected(signature) == "Always allow", "URL expansion changed permissions");
                ToggleUrls(suggestionsDialog, personalised);
                Check(!fullUrls.Visible && suggestionsDialog.Height <= collapsedHeight + 2, "URL collapse did not restore compact layout");
                Check(fullUrls.ReadOnly && fullUrls.Text == personalised + "/open?recipient_id=42", "full URL missing or truncated");
                Check(!fullUrls.Font.Bold && !Controls(suggestionsDialog).Single(control => control.AccessibleName == "Classification for " + ordinary).Font.Bold, "domain emphasis spread to URLs or classification");
                var viewport = Controls(suggestionsDialog).Single(control => control.AccessibleName == "Image site list viewport");
                Check(suggestionsDialog.Font.Equals(Forms.Control.DefaultFont) && viewport.BackColor == System.Drawing.SystemColors.Control, "original small font and grey background changed");
                Check(suggestionsDialog.Font.Name == "Tahoma" && Math.Abs(suggestionsDialog.Font.SizeInPoints - 8.25f) < 0.01, "fixture does not use Mailkeep's actual startup font");
                Check(fullUrls.BorderStyle == Forms.BorderStyle.None && fullUrls.ScrollBars == Forms.ScrollBars.None && fullUrls.BackColor == System.Drawing.SystemColors.Control, "URL should be plain selectable grey text without its own scrollbar");
                if (screenshots is not null)
                {
                    Directory.CreateDirectory(screenshots);
                    using var physicalLayout = new System.Drawing.Bitmap(suggestionsDialog.Width, suggestionsDialog.Height);
                    suggestionsDialog.DrawToBitmap(physicalLayout, new System.Drawing.Rectangle(0, 0, physicalLayout.Width, physicalLayout.Height));
                    physicalLayout.Save(Path.Combine(screenshots, "image-sites-vm-display.png"));
                }
                Check(!Controls(suggestionsDialog).OfType<Forms.VScrollBar>().Single().Visible, "short list has an unnecessary scrollbar; form=" + suggestionsDialog.Height + " viewport=" + viewport.Height + " rows=" + Controls(suggestionsDialog).Single(control => control.AccessibleName == "Image sites").Height);
                Check(fullUrls.Height < 60, "single URL row is not compact");
                Check(Recommendation(signature).PointToScreen(new System.Drawing.Point(0, Recommendation(signature).Height)).Y <= viewport.PointToScreen(new System.Drawing.Point(0, viewport.Height)).Y, "four sources do not fit for quick review");
                if (screenshots is not null)
                {
                    Directory.CreateDirectory(screenshots);
                    using var bitmap = new System.Drawing.Bitmap(suggestionsDialog.Width, suggestionsDialog.Height);
                    suggestionsDialog.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    bitmap.Save(Path.Combine(screenshots, "image-blocking-suggestion-reasons.png"));
                    foreach (var (name, area) in new[] {
                        ("landscape", new System.Drawing.Rectangle(0, 0, 1920, 1160)),
                        ("portrait-secondary", new System.Drawing.Rectangle(1920, -386, 1200, 1880)),
                        ("landscape-negative-offset", new System.Drawing.Rectangle(-1920, 80, 1920, 1040)) })
                    {
                        var anchor = new System.Drawing.Rectangle(area.Right - 300, area.Bottom - 300, 600, 600);
                        type.GetMethod("FitToWorkArea")!.Invoke(suggestionsDialog, [area, anchor]);
                        var target = suggestionsDialog.Bounds;
                        target.Width = (int)Math.Round(area.Width * 0.85);
                        // Form.SetBounds caps synthetic sizes at the VM's physical MaxWindowTrackSize.
                        // Render the calculated monitor layout at its exact size without changing the VM display.
                        Check(SetWindowPos(suggestionsDialog.Handle, IntPtr.Zero, target.X, target.Y, target.Width, target.Height, 0x14), "native layout resize failed");
                        Forms.Application.DoEvents();
                        Check(area.Contains(suggestionsDialog.Bounds), "dialog escaped " + name + " work area");
                        Check(suggestionsDialog.Width <= area.Width * 0.85 + 2 && suggestionsDialog.Height <= area.Height * 0.7 + 2, "dialog exceeds monitor limits on " + name);
                        Check(Math.Abs(suggestionsDialog.Width - Math.Round(area.Width * 0.85)) <= 2, "dialog did not get wider on " + name);
                        Check(suggestionsDialog.Height < 700, "four sources leave an oversized empty window on " + name);
                        var table = Controls(suggestionsDialog).Single(control => control.AccessibleName == "Image sites");
                        Check(table.Width <= viewport.Width + 1, "horizontal overflow on " + name);
                        foreach (var radio in Controls(suggestionsDialog).OfType<Forms.RadioButton>())
                            Check(radio.Right <= radio.Parent!.ClientSize.Width, "clipped radio on " + name);
                        using var layout = new System.Drawing.Bitmap(suggestionsDialog.Width, suggestionsDialog.Height);
                        suggestionsDialog.DrawToBitmap(layout, new System.Drawing.Rectangle(0, 0, layout.Width, layout.Height));
                        layout.Save(Path.Combine(screenshots, "image-sites-" + name + ".png"));
                        ToggleUrls(suggestionsDialog, signature);
                        Check(SetWindowPos(suggestionsDialog.Handle, IntPtr.Zero, suggestionsDialog.Left, suggestionsDialog.Top, target.Width, suggestionsDialog.Height, 0x14), "expanded native layout resize failed");
                        Forms.Application.DoEvents();
                        var expanded = Controls(suggestionsDialog).OfType<Forms.TextBox>().Single(box => box.AccessibleName == "Full image URLs from " + signature);
                        Check(expanded.Visible && expanded.Height >= (expanded.GetLineFromCharIndex(expanded.TextLength - 1) + 1) * expanded.Font.Height, "expanded URL clipped on " + name);
                        Check(area.Contains(suggestionsDialog.Bounds), "expanded dialog escaped " + name + " work area");
                        using var expandedLayout = new System.Drawing.Bitmap(suggestionsDialog.Width, suggestionsDialog.Height);
                        suggestionsDialog.DrawToBitmap(expandedLayout, new System.Drawing.Rectangle(0, 0, expandedLayout.Width, expandedLayout.Height));
                        expandedLayout.Save(Path.Combine(screenshots, "image-sites-" + name + "-expanded.png"));
                        ToggleUrls(suggestionsDialog, signature);
                    }
                }
                Click(suggestionsDialog, "Use tracker suggestions");
                Check(Selected(signature) == "Never allow" && Selected(ordinary) == "Ask", "apply suggestions changed ordinary site or missed saved matching site");
                Check(Label(signature) == "Current permission: Always allow (saved).\r\nImages load automatically." && Label(listed) == "Current permission: Never allow (saved).\r\nImages are blocked.", "changing pending choices rewrote previous saved state");
                Check(Controls(suggestionsDialog).Single(control => control.AccessibleName == "Permission change for " + signature).Text == "Save will change: Always allow → Never allow.", "pending change does not explain before and after");
                Check(previous.ForeColor == System.Drawing.Color.DarkGreen && Controls(suggestionsDialog).Single(control => control.AccessibleName == "Permission change for " + signature).ForeColor == System.Drawing.Color.RoyalBlue, "changing a choice recoloured saved state or missed pending colour");
            }
            var many = Enumerable.Range(0, 20).Select(index => "https://site" + index.ToString("D2") + ".example.invalid").ToArray();
            using (var crowded = Create(many))
            {
                crowded.Show();
                var scrollbar = Controls(crowded).OfType<Forms.VScrollBar>().Single();
                var table = Controls(crowded).Single(control => control.AccessibleName == "Image sites");
                Check(scrollbar.Visible && scrollbar.Enabled && scrollbar.Width >= 26, "prominent list scrollbar missing");
                scrollbar.Value = scrollbar.Maximum - scrollbar.LargeChange + 1;
                Check(table.Top == -scrollbar.Value && table.Top < 0, "scrollbar does not move rows");
                scrollbar.Value = 0;
                Check(table.Top == 0, "scrollbar cannot return to first source");
            }
            using var owner = new Forms.Form { Text = "Image site scope fixture" }; owner.Show();
            Edit(dialog =>
            {
                Check(Sites(dialog).SetEquals([signature, logo]), "message editor includes unrelated sites");
                Check(Scope(dialog).StartsWith("Only image domains in this message."), "message scope label unclear");
                Check(Scope(dialog).Contains("Permissions apply to all mail."), "saved policy scope not explained");
                Check(Scope(dialog).Contains("saved for future emails") && Scope(dialog).Contains("Load pictures once"), "persistent and one-time permission distinction missing");
                Check(Controls(dialog).Single(control => control.AccessibleName == "Classification for " + logo).Text == "Current permission: Ask (saved).\r\nImages wait for Load pictures once.", "previous Ask permission unclear");
                Check(Controls(dialog).OfType<Forms.Button>().Single(button => button.AccessibleName == "Permit all image sites").Text == "Always allow all", "bulk allow label implies a one-time load");
                Check(Controls(dialog).OfType<Forms.Button>().Single(button => button.AccessibleName == "Save image site permissions").Text == "Save permissions", "Save scope unclear");
                Click(dialog, "Reject all image sites");
                Check(Controls(dialog).OfType<Forms.RadioButton>().Where(button => button.Checked).All(button => button.Text == "Never allow"), "message Reject all failed");
                if (screenshots is not null)
                {
                    Directory.CreateDirectory(screenshots);
                    using var bitmap = new System.Drawing.Bitmap(dialog.Width, dialog.Height);
                    dialog.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, dialog.Width, dialog.Height));
                    bitmap.Save(Path.Combine(screenshots, "image-sites-message-only.png"));
                }
            }, save: false);
            Check(Policy(signature) == "Always allow" && Policy(logo) == "Ask" && Policy(other) == "Always allow", "Cancel wrote message choices");
            Edit(dialog => Click(dialog, "Reject all image sites"), save: true);
            Check(Policy(signature) == "Never allow" && Policy(logo) == "Never allow" && Policy(other) == "Always allow", "message bulk Save changed unrelated site or lost choices");
            Edit(dialog => Click(dialog, "Permit all image sites"), save: true);
            Check(Policy(signature) == "Always allow" && Policy(logo) == "Always allow" && Policy(other) == "Always allow", "message Permit all failed");
            owner.Close();

            void Edit(Action<Forms.Form> choose, bool save)
            {
                using var timer = new Forms.Timer { Interval = 50 }; Exception? error = null;
                timer.Tick += (_, _) =>
                {
                    var dialog = Forms.Application.OpenForms.Cast<Forms.Form>().SingleOrDefault(form => form.GetType() == type);
                    if (dialog is null) return; timer.Stop();
                    try { choose(dialog); Controls(dialog).OfType<Forms.Button>().Single(button => save ? button.AccessibleName == "Save image site permissions" : button.Text == "Cancel").PerformClick(); }
                    catch (Exception failure) { error = failure; dialog.DialogResult = Forms.DialogResult.Cancel; }
                };
                timer.Start();
                Check((bool)type.GetMethod("Edit", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, [owner, permissions, new[] { signature, logo }])! == save, "editor Save/Cancel result incorrect");
                if (error is not null) throw error;
            }
        }
        HashSet<string> Sites(Forms.Form form) => Controls(form).Where(control => control.AccessibleName?.StartsWith("Pictures from ") == true)
            .Select(control => control.AccessibleName!["Pictures from ".Length..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string Scope(Forms.Form form) => Controls(form).Single(control => control.AccessibleName == "Image site scope").Text;
        void Click(Forms.Form form, string name) => Controls(form).OfType<Forms.Button>().Single(button => button.AccessibleName == name).PerformClick();
    }
    private static IEnumerable<Forms.Control> Controls(Forms.Control parent) => ComposeToolsSmoke.Controls(parent);
    private static void ToggleUrls(Forms.Form form, string site)
    {
        var link = Controls(form).OfType<Forms.LinkLabel>().Single(control => control.AccessibleName == "Toggle image URLs from " + site);
        typeof(Forms.LinkLabel).GetMethod("OnLinkClicked", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(link,
            [new Forms.LinkLabelLinkClickedEventArgs(link.Links[0])]);
        Forms.Application.DoEvents();
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
