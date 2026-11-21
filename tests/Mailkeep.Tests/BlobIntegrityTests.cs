using System.Text;
using Mailkeep.Infrastructure;

namespace Mailkeep.Tests;

public sealed class BlobIntegrityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mailkeep-blob-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CorruptCacheIsRefusedOnEveryReadAndRepairedByPut()
    {
        var paths = new AppPaths(root);
        var blobs = new RawBlobStore(paths);
        var bytes = Encoding.UTF8.GetBytes("From: fixture@example.invalid\r\n\r\nExact MIME");
        var relative = blobs.Put(bytes);
        var file = blobs.FullPath(relative);
        Assert.Equal(bytes, blobs.ReadBytes(relative));
        File.WriteAllText(file, "corrupt");
        Assert.Throws<InvalidDataException>(() => blobs.OpenRead(relative));
        Assert.Throws<InvalidDataException>(() => blobs.ReadBytes(relative));
        Assert.Throws<InvalidDataException>(() => blobs.FullPath(relative));
        Assert.False(blobs.IsAvailable(relative));
        Assert.Equal(relative, blobs.Put(bytes));
        Assert.Equal(bytes, blobs.ReadBytes(relative));
        Assert.Empty(Directory.EnumerateFiles(paths.BlobDirectory, "*.tmp", SearchOption.AllDirectories));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
