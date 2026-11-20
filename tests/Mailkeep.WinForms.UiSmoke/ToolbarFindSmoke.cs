using System.Diagnostics;
using System.Reflection;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

internal static class ToolbarFindSmoke
{
    public static void Run(string app, string root, MailkeepServices services, string? screenshots)
    {
        services.Database.SetSetting("sync.auto", "off");
        Exception? failure = null;
        var thread = new Thread(() => { try { RunUi(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(60)), "Toolbar/Find test timed out");
        if (failure is not null) throw new InvalidOperationException("Toolbar/Find test failed", failure);
        Console.WriteLine("PASS toolbar-find-layout default-Find=middle custom-order=restart-persisted columns=resize/reorder/reopen/restart attachment=visible reset=default");

        void RunUi()
        {
            Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
            Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
            var type = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(app)!, "Mailkeep.WinForms.dll"))
                .GetType("Mailkeep.WinForms.MainForm", true)!;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Forms.Form Open()
            {
                var main = (Forms.Form)Activator.CreateInstance(type, [root])!;
                main.Show();
                Pump(() => (bool)type.GetField("_ready", flags)!.GetValue(main)!);
                return main;
            }
            Forms.Form Find(Forms.Form main)
            {
                var find = (Forms.Form)type.GetMethod("FindForm", flags)!.Invoke(main, null)!;
                find.Show();
                Forms.Application.DoEvents();
                return find;
            }
            Forms.ListView Results(Forms.Form find) => (Forms.ListView)find.GetType().GetField("_results", flags)!.GetValue(find)!;
            using (var main = Open())
            {
                var toolbar = (Forms.ToolStrip)type.GetField("_mainToolbar", flags)!.GetValue(main)!;
                var findButton = toolbar.Items.Cast<Forms.ToolStripItem>().Single(item => item.Text == "Find");
                Check(findButton.Alignment == Forms.ToolStripItemAlignment.Left && toolbar.Items.IndexOf(findButton) < toolbar.Items.Count / 2 + 2,
                    "Find is not a normal button near the middle");
                Check(toolbar.AllowItemReorder, "Native toolbar rearrangement disabled");
                toolbar.Items.Remove(findButton);
                toolbar.Items.Insert(1, findButton);
                Pump(() => services.Database.GetSetting("toolbar.order")?.StartsWith("[\"New Mail\",\"Find\"") == true);
                using var find = Find(main);
                var list = Results(find);
                Check(list.AllowColumnReorder && list.Columns[0].Width >= 32, "Attachment/reorder defaults wrong");
                list.Columns[1].Width = 211;
                list.Columns[2].Width = 287;
                list.Columns[3].Width = 177;
                list.Columns[4].Width = 199;
                list.Columns[4].DisplayIndex = 1;
                find.Close();
                find.Show();
                find.Width += 100;
                Forms.Application.DoEvents();
                Check(list.Columns[2].Width == 287 && list.Columns[4].DisplayIndex == 1, "Reopen/resize lost user columns");
                Check(services.Database.RecentFetches().Count == 0, "toolbar/Find invoked collection");
                main.Close();
            }
            using (var main = Open())
            {
                var toolbar = (Forms.ToolStrip)type.GetField("_mainToolbar", flags)!.GetValue(main)!;
                Check(toolbar.Items[1].Text == "Find", "Toolbar order did not survive restart");
                using var find = Find(main);
                var list = Results(find);
                Check(list.Columns[1].Width == 211 && list.Columns[2].Width == 287 && list.Columns[3].Width == 177
                    && list.Columns[4].Width == 199 && list.Columns[4].DisplayIndex == 1, "Column layout did not survive restart");
                list.Visible = true;
                find.ClientSize = new System.Drawing.Size(1000, 600);
                list.Items.Add(new Forms.ListViewItem(new[] { "", "Fixture", "Attachment example", "29/10/2026", "Inbox" }) { ImageKey = "clip" });
                Forms.Application.DoEvents();
                Check(list.ClientSize.Height > 100 && list.Columns[0].Width >= list.SmallImageList!.ImageSize.Width + 12,
                    "Attachment results are clipped");
                if (screenshots is not null)
                {
                    Directory.CreateDirectory(screenshots);
                    using var bitmap = new System.Drawing.Bitmap(find.Width, find.Height);
                    find.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, find.Width, find.Height));
                    bitmap.Save(Path.Combine(screenshots, "find-columns-restored.png"));
                }
                ((Forms.ToolStripMenuItem)toolbar.ContextMenuStrip!.Items[2]).PerformClick();
                Check(toolbar.Items[1].Text == "Reply", "Reset did not restore default order");
                Check(services.Database.RecentFetches().Count == 0, "toolbar/Find invoked collection");
                main.Close();
            }
        }
    }
    private static void Pump(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done()) { Check(clock.ElapsedMilliseconds < 15000, "UI did not settle"); Forms.Application.DoEvents(); Thread.Sleep(10); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
