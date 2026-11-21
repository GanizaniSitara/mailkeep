using System.Security.Cryptography;
using System.Text;

namespace Mailkeep.Infrastructure;

internal static class EncryptedStoreFile
{
    public static T Locked<T>(string path, Func<T> operation, TimeSpan? timeout = null)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
        using var mutex = new Mutex(false, "Global\\Mailkeep.Store." + key);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(timeout ?? TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Timed out waiting for the credential store writer.");
            return operation();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    public static void Write(string path, byte[] payload)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(payload);
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static byte[] Read(string path)
    {
        // Atomic replacement leaves this complete old snapshot readable without blocking a writer or UI reader.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var bytes = new MemoryStream(); file.CopyTo(bytes); return bytes.ToArray();
    }
}
