namespace Mailkeep.WinForms;

internal sealed class RemoteImageCache(long maxBytes = 64 * 1024 * 1024, int maxEntries = 128)
{
    private sealed record Entry(string Url, string Data, string[] Origins, long Bytes);
    private readonly Dictionary<string, LinkedListNode<Entry>> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> recent = new();
    private long bytes;

    public string? Get(string url, IReadOnlySet<string> allowed)
    {
        lock (entries)
        {
            if (!entries.TryGetValue(url, out var node) || !node.Value.Origins.All(allowed.Contains)) return null;
            recent.Remove(node); recent.AddFirst(node);
            return node.Value.Data;
        }
    }

    public void Add(string url, string data, string[] origins)
    {
        var size = (url.Length + (long)data.Length + origins.Sum(origin => (long)origin.Length)) * sizeof(char);
        lock (entries)
        {
            if (entries.Remove(url, out var old)) { recent.Remove(old); bytes -= old.Value.Bytes; }
            if (size > maxBytes || maxEntries <= 0) return;
            var node = recent.AddFirst(new Entry(url, data, origins, size));
            entries[url] = node; bytes += size;
            while (bytes > maxBytes || entries.Count > maxEntries)
            {
                var last = recent.Last!;
                entries.Remove(last.Value.Url); recent.RemoveLast(); bytes -= last.Value.Bytes;
            }
        }
    }

    public static async Task<byte[]?> ReadBoundedAsync(Stream stream, long limit, CancellationToken token = default)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token);
            if (count == 0) return output.ToArray();
            if (output.Length + count > limit) return null;
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }
}
