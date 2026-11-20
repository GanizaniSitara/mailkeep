using System.Diagnostics;
using System.Reflection;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

/// <summary>Mixed draft/date sorting and search-reader navigation on invented mail in Beta only.</summary>
internal static class SearchWorkflowSmoke
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    internal static void Run(string app, string root, MailkeepServices services, string scenario, string? shots)
    {
        if (!Mailkeep.WinForms.UiSmokeAccess.IsInsidersFixture(app)) throw new InvalidOperationException("VM required");
        services.Accounts.Save(services.Accounts.Load().Select(account => account with { KeepAlive = new KeepAliveSettings() }).ToArray());
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
                using var main = (Forms.Form)Activator.CreateInstance(type, [root])!;
                if (scenario == "mixed-sort") { CheckSort(main, services); return; }
                main.Show();
                Wait(() => (bool)Get(main, "_ready")!, "main ready");
                CheckReader(main, services, shots);
                main.Close();
                Pump(200);
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(80)), scenario + " timed out");
        if (failure is not null) throw new InvalidOperationException(scenario + " failed", failure);
        Console.WriteLine("PASS " + scenario);
    }

    private static void CheckSort(Forms.Form main, MailkeepServices services)
    {
        var now = DateTimeOffset.UtcNow;
        var received = services.Database.ListMessages().First() with { Date = now, Subject = "A received", SenderName = "A sender" };
        var draft = services.Send.SaveDraft("fixture-3", ["z@example.invalid"], "Z draft", "Draft bytes")
            with { UpdatedAt = now.AddDays(-3) };
        var before = System.Text.Json.JsonSerializer.Serialize(draft);
        foreach (var rows in new object[][] { [draft, received], [received, draft] })
        {
            foreach (var column in new[] { 0, 1, 3, 4 })
            {
                main.GetType().GetField("_sortColumn", Flags)!.SetValue(main, column);
                foreach (var descending in new[] { false, true })
                {
                    main.GetType().GetField("_sortDescending", Flags)!.SetValue(main, descending);
                    var sorted = (IReadOnlyList<object>)Call(main, "Sorted", [rows])!;
                    Check(sorted.Count == 2 && sorted.Contains(draft) && sorted.Contains(received), "mixed sort lost a row");
                    var receivedFirst = column is 0 or 1 ? !descending : descending;
                    Check(ReferenceEquals(sorted[0], receivedFirst ? received : draft), "wrong mixed sort order column=" + column);
                }
            }
        }
        Check(System.Text.Json.JsonSerializer.Serialize(draft) == before, "sort changed draft bytes");
        Console.WriteLine("mixed-sort both input orders; both directions; sender/subject/date/size; no dropped or changed draft");
    }

    private static void CheckReader(Forms.Form main, MailkeepServices services, string? shots)
    {
        using var find = (Forms.Form)Call(main, "FindForm", [])!;
        var hits = services.Database.ListMessages().Where(message => message.Subject is "Automation fixture" or "Alpha 0" or "Zeta unread 0")
            .OrderByDescending(message => message.Date).ToArray();
        Check(hits.Length == 3 && hits.Any(hit => services.Database.GetMessage(hit.Id)!.BodyHtml is not null), "missing HTML fixture");
        find.GetType().GetField("_hits", Flags)!.SetValue(find, hits);
        find.GetType().GetField("_sortColumn", Flags)!.SetValue(find, 2); // Subject, deliberately different from date order.
        find.GetType().GetField("_descending", Flags)!.SetValue(find, false);
        Call(find, "ShowHits", []);
        var list = (Forms.ListView)Get(find, "_results")!;
        list.Visible = true;
        find.Show();
        foreach (var (name, area) in new[] {
            ("landscape", new System.Drawing.Rectangle(0, 0, 1280, 960)),
            ("portrait-offset", new System.Drawing.Rectangle(820, 0, 768, 1024)) })
        {
            find.Bounds = new System.Drawing.Rectangle(area.Left + 16, area.Top + 16, name == "landscape" ? 900 : 600, 460);
            Pump(100);
            Check(area.Contains(find.Bounds), "Find outside simulated work area");
            var filters = Controls(find).Single(control => control.AccessibleName == "Find filters");
            Check(filters.Bottom < list.Bottom && list.Top >= filters.Bottom, "Find filters overlap results");
            foreach (Forms.Control control in filters.Controls)
                if (control.Visible) Check(filters.ClientRectangle.Contains(control.Bounds), "clipped Find filter " + control.AccessibleName);
            Shot(find, shots, "find-" + name);
        }
        find.Location = new System.Drawing.Point(30, 40);
        var order = list.Items.Cast<Forms.ListViewItem>().Select(item => ((MessageSummary)item.Tag!).Id).ToArray();
        list.Items[0].Selected = true;
        Call(find, "OpenSelected", []);
        Wait(() => main.OwnedForms.Any(form => form.GetType().Name == "MessageWindow"), "reader open");
        using var reader = main.OwnedForms.Single(form => form.GetType().Name == "MessageWindow");
        var toolbar = Controls(reader).OfType<Forms.ToolStrip>().Single();
        var previous = toolbar.Items.OfType<Forms.ToolStripButton>().Single(button => button.AccessibleName == "Previous");
        var next = toolbar.Items.OfType<Forms.ToolStripButton>().Single(button => button.AccessibleName == "Next");
        MessageContent Content() => (MessageContent)reader.GetType().GetProperty("CurrentContent", Flags)!.GetValue(reader)!;
        Check(Content().Summary.Id == order[0] && !previous.Enabled && next.Enabled, "first boundary");
        // Sorting Find again must not change the reader's captured result sequence.
        find.GetType().GetField("_descending", Flags)!.SetValue(find, true);
        Call(find, "ShowHits", []);
        next.PerformClick();
        Wait(() => Content().Summary.Id == order[1] && next.Enabled, "next HTML message");
        var view = Controls(reader).Single(control => control.GetType().Name == "HtmlView");
        Wait(() => (bool)view.GetType().GetProperty("DocumentLoaded")!.GetValue(view)!, "HTML navigation rendered");
        Check(Content().BodyHtml is not null && Content().Attachments.Count(file => !file.Inline) == 2
            && Content().Attachments.Any(file => file.FileName == "fixture.txt")
            && Content().Attachments.Any(file => file.FileName == "second.txt"), "HTML/attachments did not follow message");
        dynamic core = view.GetType().GetProperty("CoreWebView2")!.GetValue(view)!;
        var document = (Task<string>)core.ExecuteScriptAsync("document.body.innerText");
        Await(document);
        Check(document.Result.Contains("HTML reading fixture"), "reader HTML DOM has stale/empty body");
        var clip = Controls(reader).OfType<Forms.Button>().Single(button => button.AccessibleName == "Message attachments");
        Check(clip.Visible && clip.Parent!.Width > 0 && reader.ClientRectangle.Contains(reader.RectangleToClient(clip.RectangleToScreen(clip.ClientRectangle))),
            "attachment control hidden/clipped after navigation");
        // Accessible description is populated from the current message's actual attachment menu names.
        Check(clip.AccessibleDescription!.Contains("fixture.txt") && clip.AccessibleDescription.Contains("second.txt"), "attachment menu names stale");
        reader.Bounds = new System.Drawing.Rectangle(40, 70, 820, 640);
        Shot(reader, shots, "search-reader-landscape");
        next.PerformClick();
        Wait(() => Content().Summary.Id == order[2] && previous.Enabled, "next plain message");
        Check(!next.Enabled && Controls(reader).OfType<Forms.RichTextBox>().Single().Visible && !view.Visible, "last/plain boundary");
        reader.Bounds = new System.Drawing.Rectangle(836, 16, 520, 640);
        Pump(100);
        Check(previous.Bounds.Right <= next.Bounds.Left && toolbar.ClientRectangle.Contains(next.Bounds)
            && toolbar.ClientRectangle.Contains(previous.Bounds), "navigation clipped/incorrect order");
        Shot(reader, shots, "search-reader-portrait-offset");
        previous.PerformClick();
        Wait(() => Content().Summary.Id == order[1] && next.Enabled, "previous HTML message");
        previous.PerformClick();
        Wait(() => Content().Summary.Id == order[0] && next.Enabled, "previous first message");
        Check(!previous.Enabled && Content().BodyHtml is null, "previous boundary/plain content");

        // A deliberately delayed navigation must not block UI work or allow two concurrent loads.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Func<int, Task> delayed = async _ => { calls++; await release.Task; };
        // Separate reader to test callbacks without opening real compose/print UI.
        var readerType = reader.GetType();
        long actedOn = 0;
        Func<MessageContent, bool, Task> reply = (content, _) => { actedOn = content.Summary.Id; return Task.CompletedTask; };
        using var held = (Forms.Form)Activator.CreateInstance(readerType, [Content(), reply,
            (Func<MessageContent, Task>)(_ => Task.CompletedTask), (Action<MessageContent>)(content => actedOn = content.Summary.Id),
            (Action<MessageContent>)(_ => { }), Path.Combine(services.Paths.Root, "held-reader"), (Func<long, int, string>)((_, _) => ""), null, null])!;
        held.Show();
        Call(held, "SetNavigation", [delayed, false, true]);
        var heldNext = Controls(held).OfType<Forms.ToolStrip>().Single().Items.OfType<Forms.ToolStripButton>().Single(button => button.Text == "Next");
        heldNext.PerformClick(); heldNext.PerformClick();
        var ticked = false;
        held.BeginInvoke(() => ticked = true);
        Wait(() => ticked, "navigation blocked UI");
        Check(calls == 1 && !heldNext.Enabled && held.Text.Length > 0, "duplicate navigation/current content lost");
        release.SetResult();
        Wait(() => heldNext.Enabled, "navigation did not recover");
        var replacement = services.Database.GetMessage(order[2])!;
        Await((Task)Call(held, "SetContentAsync", [replacement])!);
        Controls(held).OfType<Forms.ToolStrip>().Single().Items.OfType<Forms.ToolStripButton>().Single(button => button.Text == "Reply").PerformClick();
        Check(actedOn == order[2], "Reply still targets initial content");
        held.Close(); reader.Close(); find.Close();
        Console.WriteLine("search-reader captured subject order; boundaries; HTML/plain/attachments; current Reply; delayed load UI responsive; layouts simulated 96 DPI");
    }

    private static object? Get(object target, string field) => target.GetType().GetField(field, Flags)!.GetValue(target);
    private static object? Call(object target, string method, object?[] arguments) => target.GetType().GetMethod(method, Flags)!.Invoke(target, arguments);
    private static IEnumerable<Forms.Control> Controls(Forms.Control parent) => ComposeToolsSmoke.Controls(parent);
    private static void Shot(Forms.Form form, string? directory, string name)
    {
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        form.Update();
        using var image = new System.Drawing.Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, new System.Drawing.Rectangle(System.Drawing.Point.Empty, image.Size));
        image.Save(Path.Combine(directory, name + ".png"));
        if (name == "search-reader-landscape")
        {
            using var actual = new System.Drawing.Bitmap(form.Width, form.Height);
            using (var graphics = System.Drawing.Graphics.FromImage(actual)) graphics.CopyFromScreen(form.Location, System.Drawing.Point.Empty, actual.Size);
            actual.Save(Path.Combine(directory, name + "-native.png"));
        }
    }
    private static void Await(Task task) { Wait(() => task.IsCompleted, "async reader"); task.GetAwaiter().GetResult(); }
    private static void Wait(Func<bool> done, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!done()) { if (clock.ElapsedMilliseconds > 18000) throw new TimeoutException(what); Pump(10); }
    }
    private static void Pump(int ms)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ms) { Forms.Application.DoEvents(); Thread.Sleep(10); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
