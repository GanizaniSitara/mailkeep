using Mailkeep.Infrastructure;

namespace Mailkeep.Tests;

public sealed class AttachmentArgumentsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mailkeep-attach-args-" + Guid.NewGuid().ToString("N"));

    public AttachmentArgumentsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void EachRepeatedAttachOptionYieldsItsFullPathInOrder()
    {
        var first = Path.Combine(_root, "first.txt");
        var second = Path.Combine(_root, "second.pdf");
        File.WriteAllText(first, "one");
        File.WriteAllText(second, "two");

        var paths = AttachmentArguments.Parse(["prepare", "--attach", first, "--subject", "S", "--ATTACH", second, "--execute"]);

        Assert.Equal([first, second], paths);
    }

    [Fact]
    public void NoAttachOptionYieldsNoAttachments()
        => Assert.Empty(AttachmentArguments.Parse(["prepare", "--subject", "S"]));

    [Fact]
    public void MissingFileFailsNamingThePath()
    {
        var missing = Path.Combine(_root, "absent.txt");
        var error = Assert.Throws<FileNotFoundException>(() => AttachmentArguments.Parse(["prepare", "--attach", missing]));
        Assert.Contains(missing, error.Message);
    }

    [Fact]
    public void AttachWithoutAPathFails()
        => Assert.Throws<ArgumentException>(() => AttachmentArguments.Parse(["prepare", "--attach"]));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
