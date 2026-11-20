using System.Diagnostics;
using System.Reflection;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using MimeKit;
using Forms = System.Windows.Forms;

/// <summary>Converted isolated OE drafts must appear once, open in compose and never enter Outbox.</summary>
internal static class OeDraftConversionSmoke
{
    internal static async Task Run(string app, string root, MailkeepServices services)
    {
        foreach (var folder in new[] { "Drafts", "Outbox" })
        {
            var builder = new BodyBuilder { TextBody = "Converted editable body", HtmlBody = "<p>Converted editable body</p>" };
            builder.Attachments.Add("retained.txt", System.Text.Encoding.UTF8.GetBytes("retained attachment"));
            using var message = new MimeMessage { Subject = "Converted " + folder, Body = builder.ToMessageBody() };
            message.From.Add(MailboxAddress.Parse("three@example.invalid"));
            message.To.Add(MailboxAddress.Parse("recipient@example.invalid"));
            using var raw = new MemoryStream(); message.WriteTo(raw); raw.Position = 0;
            await services.Database.UpsertMimeMessageAsync("archive", "OE Classic", folder, folder, null, 0, message, raw, false);
        }
        new OeDraftConversion(new AppPaths(root)).Apply(new OeDraftConversionReport(new AppPaths(root)).Run());
        services.Database.SetSetting("sync.auto", "off");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
                Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
                SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
                var directory = Path.GetDirectoryName(Path.GetFullPath(app))!;
                var loader = Path.Combine(directory, "WebView2Loader.dll");
                if (!File.Exists(loader)) loader = Path.Combine(directory, "runtimes", "win-x64", "native", "WebView2Loader.dll");
                System.Runtime.InteropServices.NativeLibrary.Load(loader);
                var assembly = Assembly.LoadFrom(Path.Combine(directory, "Mailkeep.WinForms.dll"));
                var type = assembly.GetType("Mailkeep.WinForms.MainForm", true)!;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                using var main = (Forms.Form)Activator.CreateInstance(type, [root])!;
                main.Show();
                Until(() => (bool)type.GetField("_ready", flags)!.GetValue(main)!);
                var tree = (Forms.TreeView)type.GetField("_localTree", flags)!.GetValue(main)!;
                var list = (Forms.ListView)type.GetField("_messages", flags)!.GetValue(main)!;
                Forms.TreeNode Node(string key) => Walk(tree.Nodes).First(node => node.Name == "LocalFolder:" + key);
                tree.SelectedNode = Node(LocalFolderKinds.Drafts);
                Until(() => list.VirtualListSize == 2);
                list.Items[0].Selected = true;
                _ = (Task)type.GetMethod("OpenSelectedMessageAsync", flags)!.Invoke(main, null)!;
                Until(() => Forms.Application.OpenForms.Cast<Forms.Form>().Any(form => form.GetType().Name == "ComposeForm"));
                var compose = Forms.Application.OpenForms.Cast<Forms.Form>().Single(form => form.GetType().Name == "ComposeForm");
                var subject = (Forms.TextBox)compose.GetType().GetField("_subject", flags)!.GetValue(compose)!;
                Check(subject.Text.StartsWith("Converted ") && !subject.ReadOnly, "converted draft is not editable");
                var attachments = (Forms.ListView)compose.GetType().GetField("_attachments", flags)!.GetValue(compose)!;
                Check(attachments.Items.Count == 1, "converted attachment missing");
                compose.Dispose();
                tree.SelectedNode = Node(LocalFolderKinds.Outbox);
                Until(() => list.VirtualListSize == 0);
                Check(services.Database.ListOutgoing(OutgoingStates.Outbox).Count == 0 && services.Database.ClaimNextOutgoing() is null, "converted mail was queued");
                Console.WriteLine("PASS oe-draft-conversion drafts=2 originals=retained compose=editable attachment=retained outbox=empty sends=0");
                void Until(Func<bool> condition)
                {
                    var clock = Stopwatch.StartNew();
                    while (!condition()) { Check(clock.ElapsedMilliseconds < 20000, "OE conversion UI fixture timed out"); Forms.Application.DoEvents(); Thread.Sleep(10); }
                }
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(60)), "OE conversion fixture timed out");
        if (failure is not null) throw new InvalidOperationException("OE conversion UI failed", failure);
    }
    private static IEnumerable<Forms.TreeNode> Walk(Forms.TreeNodeCollection nodes)
    {
        foreach (Forms.TreeNode node in nodes) { yield return node; foreach (var child in Walk(node.Nodes)) yield return child; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
