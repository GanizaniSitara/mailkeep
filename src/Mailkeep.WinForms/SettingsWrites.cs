using System.Runtime.CompilerServices;
using Mailkeep.Infrastructure;

namespace Mailkeep.WinForms;

/// <summary>Persist UI preference snapshots in order without SQLite writes in resize/drag events.
/// The main window drains pending writes before saving its final layout at close.</summary>
internal static class SettingsWrites
{
    private sealed class Pending
    {
        internal Task Tail = Task.CompletedTask;
        internal List<Exception> Errors = [];
        internal Dictionary<string, string> Latest = [];
        internal bool Running;
    }
    private static readonly ConditionalWeakTable<MailDatabase, Pending> Queues = new();

    internal static Task Save(MailDatabase database, string key, string value)
    {
        var pending = Queues.GetValue(database, _ => new Pending());
        lock (pending)
        {
            pending.Latest[key] = value;
            if (!pending.Running)
            {
                pending.Running = true;
                pending.Tail = Task.Run(() => Persist(database, pending));
            }
            return pending.Tail;
        }
    }

    private static void Persist(MailDatabase database, Pending pending)
    {
        while (true)
        {
            KeyValuePair<string, string>[] values;
            lock (pending)
            {
                if (pending.Latest.Count == 0) { pending.Running = false; return; }
                values = pending.Latest.ToArray();
                pending.Latest.Clear();
            }
            foreach (var (key, value) in values)
            {
                try { database.SetSetting(key, value); }
                catch (Exception error) { lock (pending) pending.Errors.Add(error); }
            }
        }
    }

    internal static async Task DrainAsync(MailDatabase database)
    {
        var pending = Queues.GetValue(database, _ => new Pending());
        Task tail;
        lock (pending) tail = pending.Tail;
        await tail.ConfigureAwait(false);
        lock (pending)
        {
            if (pending.Errors.Count == 0) return;
            var errors = pending.Errors.ToArray(); pending.Errors.Clear();
            throw new AggregateException("Some window preferences could not be saved.", errors);
        }
    }
}
