using System.Security.Cryptography;

namespace Mailkeep.Infrastructure;

public sealed class RawBlobStore
{
    private readonly AppPaths _paths;

    public RawBlobStore(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<string> PutAsync(Stream source, CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        using var memory = new MemoryStream();
        await source.CopyToAsync(memory, cancellationToken);
        return Put(memory.ToArray());
    }

    public string Put(ReadOnlySpan<byte> content)
    {
        _paths.EnsureDirectories();
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var relative = Path.Combine(hash[..2], hash + ".eml");
        var destination = Path.Combine(_paths.BlobDirectory, relative);
        if (File.Exists(destination) && Matches(destination, hash))
        {
            return relative;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temporary, content.ToArray());
        try
        {
            File.Move(temporary, destination, overwrite: true);
        }
        catch (IOException) when (File.Exists(destination) && Matches(destination, hash))
        {
            File.Delete(temporary);
        }
        return relative;
    }

    public string FullPath(string relativePath)
    {
        var file = CachePath(relativePath);
        var relative = Path.GetRelativePath(_paths.BlobDirectory, file);
        // Folder files are primary, even when a disposable .eml already exists. Materialize only
        // the requested MIME for callers that require a filename (headers, attachments, export).
        if (new PortableMimeReader(_paths).Read(relative) is { } raw) Put(raw);
        return file;
    }

    public Stream OpenRead(string relativePath)
    {
        var file = CachePath(relativePath);
        var relative = Path.GetRelativePath(_paths.BlobDirectory, file);
        return new PortableMimeReader(_paths).Read(relative) is { } raw
            ? new MemoryStream(raw, writable: false) : File.OpenRead(file);
    }

    public byte[] ReadBytes(string relativePath)
    {
        var file = CachePath(relativePath);
        return new PortableMimeReader(_paths).Read(Path.GetRelativePath(_paths.BlobDirectory, file))
            ?? File.ReadAllBytes(file);
    }

    public bool IsAvailable(string relativePath)
    {
        try { using var stream = OpenRead(relativePath); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private string CachePath(string relativePath)
    {
        var root = Path.GetFullPath(_paths.BlobDirectory) + Path.DirectorySeparatorChar;
        var file = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("MIME cache path escaped its root.");
        return file;
    }

    internal static string RelativePath(string sha) => Path.Combine(sha[..2], sha + ".eml");

    private static bool Matches(string file, string sha)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(sha, StringComparison.OrdinalIgnoreCase);
    }
}
