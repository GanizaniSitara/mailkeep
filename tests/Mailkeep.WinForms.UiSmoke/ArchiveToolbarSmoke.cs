using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Mailkeep.Infrastructure;

// Checks Options persistence and renders the actual dialog without displaying a fixture window.
internal static class ArchiveToolbarSmoke
{
    public static int Run(string app, string root)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var services = new MailkeepServices(root);
                services.Database.Initialize();
                var assembly = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(app))!, "Mailkeep.WinForms.dll"));
                var type = assembly.GetType("Mailkeep.WinForms.OptionsForm", true)!;
                Form Options() => (Form)Activator.CreateInstance(type,
                    [services, false, "New", (Action<IWin32Window>)(_ => { })])!;
                IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>()
                    .SelectMany(child => new[] { child }.Concat(Descendants(child)));
                CheckBox Choice(Form form) => Descendants(form).OfType<CheckBox>()
                    .Single(control => control.AccessibleName == "Show Archive button on the toolbar");
                void Close(Form form, DialogResult result)
                {
                    if (result == DialogResult.OK) { OptionsSaveSmoke.Save(form); return; }
                    form.DialogResult = result;
                    typeof(Form).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(form, [new FormClosingEventArgs(CloseReason.UserClosing, false)]);
                }
                using (var form = Options())
                {
                    if (!Choice(form).Checked) throw new Exception("Archive toolbar should be enabled by default.");
                    // Mark managed visibility only after creating an invisible native window. This lets
                    // DrawToBitmap paint child controls without ever calling Show/ShowWindow.
                    _ = form.Handle;
                    var setState = typeof(Control).GetMethod("SetState", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var visible = Enum.Parse(setState.GetParameters()[0].ParameterType, "Visible");
                    setState.Invoke(form, [visible, true]);
                    foreach (var child in Descendants(form)) child.CreateControl();
                    form.PerformLayout();
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, form.ClientRectangle with { Width = form.Width, Height = form.Height });
                    bitmap.Save(Path.Combine(root, "options-archive-toolbar.png"));
                    foreach (var (name, area) in new[] {
                        ("landscape", new Rectangle(0, 0, 1280, 720)),
                        ("portrait-negative-offset", new Rectangle(-800, 0, 800, 1280)),
                        ("secondary-offset", new Rectangle(1280, -200, 1920, 1080)) })
                    {
                        var bounds = new Rectangle(area.Left + (area.Width - form.Width) / 2,
                            area.Top + (area.Height - form.Height) / 2, form.Width, form.Height);
                        if (!area.Contains(bounds)) throw new Exception("Options extends outside " + name);
                        using var screen = new Bitmap(area.Width, area.Height);
                        using var graphics = Graphics.FromImage(screen);
                        graphics.Clear(SystemColors.AppWorkspace);
                        graphics.DrawImageUnscaled(bitmap, bounds.Left - area.Left, bounds.Top - area.Top);
                        screen.Save(Path.Combine(root, "options-" + name + ".png"));
                    }
                    setState.Invoke(form, [visible, false]);
                    Choice(form).Checked = false;
                    Close(form, DialogResult.Cancel);
                    if (services.Database.GetSetting("toolbar.show_archive") is not null) throw new Exception("Cancel saved a preference.");
                }
                using (var form = Options()) { Choice(form).Checked = false; Close(form, DialogResult.OK); }
                using (var form = Options())
                {
                    if (Choice(form).Checked) throw new Exception("Disabled preference was not loaded.");
                    Choice(form).Checked = true;
                    Close(form, DialogResult.OK);
                }
                using (var form = Options())
                    if (!Choice(form).Checked) throw new Exception("Enabled preference was not loaded.");
                Console.WriteLine("PASS archive-toolbar checked-default cancel-preserves OK-persists off/on rendered-without-showing-window");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
        return 0;
    }
}
