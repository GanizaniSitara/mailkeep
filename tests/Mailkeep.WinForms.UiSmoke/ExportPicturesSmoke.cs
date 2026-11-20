using System.Diagnostics;
using System.Reflection;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

/// <summary>Export pictures, in process on a throwaway store: Options offers it, and the export window copies the
/// Inbox's pictures into the chosen folder and says what it did.</summary>
internal static class ExportPicturesSmoke
{
    public static void Run(string app, string root, MailkeepServices services, string? screenshots)
    {
        var photo = Enumerable.Range(0, 30_000).Select(index => (byte)(index % 253)).ToArray();
        var builder = new MimeKit.BodyBuilder { TextBody = "Fixture body" };
        builder.Attachments.Add("holiday.jpg", photo, new MimeKit.ContentType("image", "jpeg"));
        using (var message = new MimeKit.MimeMessage { Body = builder.ToMessageBody() })
        using (var bytes = new MemoryStream())
        {
            message.WriteTo(bytes);
            services.Database.Upsert(new MessageInput(MailContract.LiveSource, "local-fixture", LocalFolderKinds.Inbox, "picture-1", null, 0,
                "Picture fixture", "sender@example.invalid", "Sender", [], new DateTimeOffset(2026, 9, 14, 10, 0, 0, TimeSpan.Zero),
                photo.Length, true, 1, "Fixture body", null, services.Blobs.Put(bytes.ToArray())));
        }
        var destination = Path.Combine(root, "exported pictures");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
                Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
                SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
                var assembly = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(app))!, "Mailkeep.WinForms.dll"));
                var asked = false;
                var options = (Forms.Form)Activator.CreateInstance(assembly.GetType("Mailkeep.WinForms.OptionsForm", true)!,
                    [services, false, "New", (Action<Forms.IWin32Window>)(_ => asked = true)])!;
                options.Show();
                var button = All(options).OfType<Forms.Button>().Single(control => control.AccessibleName == "Export pictures from mail");
                button.PerformClick();
                if (!asked) throw new InvalidOperationException("Options' Export pictures button did nothing");
                options.Close();

                var findFolder = assembly.GetType("Mailkeep.WinForms.FindFolder", true)!;
                var choices = (System.Collections.IEnumerable)findFolder.GetMethod("LocalChoices")!.Invoke(null, [services.Database.ListLocalFolders()])!;
                var listType = typeof(List<>).MakeGenericType(findFolder);
                var folders = (System.Collections.IList)Activator.CreateInstance(listType)!;
                foreach (var choice in choices) folders.Add(choice);
                var form = (Forms.Form)Activator.CreateInstance(assembly.GetType("Mailkeep.WinForms.ExportPicturesForm", true)!, [services.Database, folders])!;
                form.Show();
                var lookIn = All(form).OfType<Forms.ComboBox>().Single();
                lookIn.SelectedItem = lookIn.Items.Cast<object>().First(item => (string)item.GetType().GetProperty("Name")!.GetValue(item)! == "Inbox");
                All(form).OfType<Forms.TextBox>().Single().Text = destination;
                All(form).OfType<Forms.Button>().Single(control => control.AccessibleName == "Export pictures").PerformClick();
                var status = All(form).OfType<Forms.Label>().Single(control => control.AccessibleName == "Export status");
                var clock = Stopwatch.StartNew();
                while (!status.Text.EndsWith('.') || status.Text.StartsWith("Finding"))
                {
                    if (clock.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("export did not finish: " + status.Text);
                    Forms.Application.DoEvents();
                    Thread.Sleep(20);
                }
                if (!status.Text.StartsWith("1 picture saved from 1 message")) throw new InvalidOperationException("export said: " + status.Text);
                var file = Directory.GetFiles(destination).Single();
                if (!File.ReadAllBytes(file).SequenceEqual(photo) || !Path.GetFileName(file).EndsWith(" holiday.jpg"))
                    throw new InvalidOperationException("the exported picture is wrong: " + file);
                if (screenshots is not null)
                {
                    Directory.CreateDirectory(screenshots);
                    using var image = new System.Drawing.Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                    image.Save(Path.Combine(screenshots, "export-pictures.png"));
                }
                form.Close();
                Console.WriteLine($"PASS export-pictures options-button=opens inbox=1 picture name={Path.GetFileName(file)} status=\"{status.Text}\"");
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60))) throw new TimeoutException("export-pictures did not finish");
        if (failure is not null) throw new InvalidOperationException("export-pictures failed", failure);
    }

    private static IEnumerable<Forms.Control> All(Forms.Control parent)
    {
        foreach (Forms.Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in All(child)) yield return nested;
        }
    }
}
