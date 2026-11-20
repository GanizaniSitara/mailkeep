using System.Diagnostics;
using System.Reflection;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;
using Forms = System.Windows.Forms;

/// <summary>How long Find takes to appear, from the command to the window painted and ready for typing, on a
/// store whose import is the size of the real one. First opening and later ones are timed separately.</summary>
internal static class FindLatencySmoke
{
    public static int Run(string app, string root, MailkeepServices services, int limitMs)
    {
        Seed(services);
        var results = new List<long>();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
                Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
                SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
                var assembly = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(app))!, "Mailkeep.WinForms.dll"));
                var type = assembly.GetType("Mailkeep.WinForms.MainForm", true)!;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var form = (Forms.Form)Activator.CreateInstance(type, [root])!;
                form.Show();
                var clock = Stopwatch.StartNew();
                // Ready, then a few seconds for whatever start-up does in the background.
                while (!(bool)type.GetField("_ready", flags)!.GetValue(form)! || clock.Elapsed < TimeSpan.FromSeconds(5)) Pump();
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    var timer = Stopwatch.StartNew();
                    type.GetMethod("ShowSearchPromptAsync", flags)!.Invoke(form, []);
                    var find = Forms.Application.OpenForms.Cast<Forms.Form>().First(open => open.Text == "Find Message");
                    // Painted: the window has drawn itself once the queue holds nothing more for it.
                    find.Update();
                    timer.Stop();
                    results.Add(timer.ElapsedMilliseconds);
                    if (!find.Visible) throw new InvalidOperationException("Find did not open");
                    // Put away with Escape, as the reader would, from whichever field has the focus.
                    var focused = find.ActiveControl ?? find;
                    PostMessage(focused.Handle, 0x0100, new IntPtr(0x1B), IntPtr.Zero); // WM_KEYDOWN VK_ESCAPE
                    for (var pass = 0; pass < 30; pass++) Pump();
                    if (find.Visible) throw new InvalidOperationException("Escape did not close Find");
                }
                form.Close();
                form.Dispose();
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(120))) throw new TimeoutException("find-latency did not finish");
        if (failure is not null) throw failure;
        var ok = results.All(ms => ms <= limitMs);
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} find-latency escape=closes limit_ms={limitMs} first_ms={results[0]} again_ms={string.Join(",", results.Skip(1))}");
        return ok ? 0 : 1;

        static void Pump() { Forms.Application.DoEvents(); Thread.Sleep(10); }
    }

    /// <summary>A quarter of a million imported messages over two hundred folders, as the real import has.</summary>
    private static void Seed(MailkeepServices services)
    {
        using var connection = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO messages(source,account,folder,source_key,subject,sender,sender_name,date_utc,size,is_read,body_text)
            VALUES($source,'Fixture archive',$folder,$key,$subject,'bulk@example.invalid','Bulk',$date,1024,$read,'')
            """;
        insert.Parameters.AddWithValue("$source", MailContract.ArchiveSource);
        var folder = insert.Parameters.Add("$folder", SqliteType.Text);
        var key = insert.Parameters.Add("$key", SqliteType.Text);
        var subject = insert.Parameters.Add("$subject", SqliteType.Text);
        var date = insert.Parameters.Add("$date", SqliteType.Text);
        var read = insert.Parameters.Add("$read", SqliteType.Integer);
        var origin = new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 250_000; index++)
        {
            folder.Value = $"Bulk/Folder {index % 200:000}";
            key.Value = "bulk-" + index;
            subject.Value = "Bulk archived message " + index;
            date.Value = origin.AddMinutes(index).UtcDateTime.ToString("O");
            read.Value = index % 3 == 0 ? 1 : 0;
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr word, IntPtr value);
}
