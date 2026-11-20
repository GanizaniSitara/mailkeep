using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mailkeep.Infrastructure;
using Microsoft.Data.Sqlite;

internal static class BulkPerformanceSmoke
{
    public static int Run(string app, string output)
    {
        if (!Path.GetFullPath(app).StartsWith(@"C:\mk-smoke\app\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Background VM benchmark required.");
        return RunCore(output, 3827, 252222);
    }

    public static int Headless(string output, bool fullScale = false)
    {
        if (!Path.GetFullPath(output).Contains(Path.DirectorySeparatorChar + "scratch" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Disposable scratch output required.");
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
        return RunCore(output, fullScale ? 225692 : 42, fullScale ? 5000 : 200, fullScale);
    }

    private static int RunCore(string output, int count, int controls, bool fullScale = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "mailkeep-bulk-performance-" + Guid.NewGuid().ToString("N"));
        if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < (fullScale ? 160L : 8L) * 1024 * 1024 * 1024)
            throw new IOException("Insufficient guest space for disposable benchmark.");
        var services = new MailkeepServices(root); services.Database.Initialize();
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "fixture.json"), JsonSerializer.Serialize(new { root, count, controls, fullScale }));
        var setup = Stopwatch.StartNew();
        using (var c = new SqliteConnection($"Data Source={services.Paths.DatabasePath};Pooling=False"))
        {
            c.Open(); using var tx = c.BeginTransaction(); using var q = c.CreateCommand(); q.Transaction = tx;
            q.CommandText = """
                INSERT INTO messages(source,account,folder,source_key,subject,sender,date_utc,size,is_read,body_text,body_html,raw_path)
                VALUES('archive','Fixture',$folder,$key,$subject,'fixture@example.invalid','1970-01-01T00:00:00Z',$size,$read,$body,$html,$raw);
                """;
            foreach (var key in new[] { "key", "subject", "size", "read", "body", "html", "folder", "raw" }) q.Parameters.Add(new SqliteParameter("$" + key, DBNull.Value));
            q.Prepare();
            for (var i = 0; i < count; i++)
            {
                var body = $"performance-{i}\r\n" + (fullScale ? string.Concat(Enumerable.Repeat("synthetic searchable mail fixture body ", 110)) : new string('x', 43800));
                var raw = Encoding.ASCII.GetBytes($"From: fixture@example.invalid\r\nSubject: Performance {i}\r\nMessage-ID: <performance-{i}@example.invalid>\r\nContent-Type: text/plain; charset=us-ascii\r\n\r\n{body}\r\n");
                if (fullScale)
                {
                    const long totalBytes = 35398873693;
                    // Match measured count/total MIME volume, including one 69 MB message.
                    const int largest = 69385968;
                    var remaining = totalBytes - largest;
                    var size = i == 0 ? largest : checked((int)(remaining / (count - 1) + (i <= remaining % (count - 1) ? 1 : 0)));
                    var originalLength = raw.Length; Array.Resize(ref raw, size);
                    raw.AsSpan(originalLength).Fill((byte)'x');
                }
                q.Parameters["$key"].Value = "original:" + i; q.Parameters["$subject"].Value = "Performance " + i;
                q.Parameters["$size"].Value = raw.Length; q.Parameters["$read"].Value = i % 2;
                q.Parameters["$folder"].Value = fullScale ? "Performance/Folder " + i % 160 : "Performance";
                q.Parameters["$html"].Value = fullScale ? "<p>" + new string('h', 54000) + "</p>" : DBNull.Value;
                q.Parameters["$body"].Value = body; q.Parameters["$raw"].Value = services.Blobs.Put(raw); q.ExecuteNonQuery();
                if ((i + 1) % 10000 == 0) Console.WriteLine(JsonSerializer.Serialize(new { phase = "fixture", messages = i + 1, seconds = setup.Elapsed.TotalSeconds }));
            }
            q.Parameters.Clear();
            q.CommandText = """
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<$controls)
                INSERT INTO messages(source,account,folder,source_key,subject,sender,date_utc,body_text,body_html)
                SELECT 'fixture-control','control','elsewhere','control:'||i,'Control '||i,'control@example.invalid',
                    '1970-01-01T00:00:00Z','Unrelated control '||i,printf('%0512d',i) FROM n;
                INSERT INTO messages_fts(rowid,subject,sender,recipients,body_text) SELECT id,subject,sender,recipients_json,body_text FROM messages;
                """;
            q.Parameters.AddWithValue("$controls", controls); q.ExecuteNonQuery(); tx.Commit();
        }
        var migration = new BulkFolderMigration(services.Paths);
        var watch = Stopwatch.StartNew(); var plan = migration.Preview();
        Console.WriteLine(JsonSerializer.Serialize(new { phase = "fixture-ready", seconds = setup.Elapsed.TotalSeconds, plan.DatabaseBytes, plan.MimeBytes, plan.RequiredFreeBytes }));
        var stages = new List<BulkFolderMigration.Progress>();
        var state = migration.Run(plan.Confirmation, p => { stages.Add(p); Console.WriteLine(JsonSerializer.Serialize(p)); }, publish: true);
        var verifiedSeconds = stages.Single(s => s.Phase == "verified").ElapsedSeconds;
        var reopened = new MailkeepServices(root); reopened.Database.Initialize();
        if (reopened.Database.ListHeads("archive").Count != count) throw new InvalidDataException("Reopened count differs.");
        var completedSeconds = watch.Elapsed.TotalSeconds;
        var proof = new { pass = true, root, count, controls, fullScale, plan.DatabaseBytes, bytes = state.Outputs.Sum(o => o.Bytes), verifiedSeconds, completedSeconds, stages };
        Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "bulk-performance.json"), JsonSerializer.Serialize(proof));
        Console.WriteLine(JsonSerializer.Serialize(proof)); return 0;
    }
}
