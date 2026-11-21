using Mailkeep.Cli;
using Mailkeep.Infrastructure;
using Mailkeep.WinForms;

namespace Mailkeep.Tests;

public sealed class ReadingBoundaryTests
{
    [Fact]
    public void ResourceHintsAndPrefetchOverridesAreRemoved()
    {
        var input = "<link rel=dns-prefetch href='//tracker.invalid'><LINK rel=preconnect href='https://tracker.invalid'>"
            + "<link title='a > b' rel=stylesheet href='https://tracker.invalid/style.css'>"
            + "<meta content=on http-equiv=x-dns-prefetch-control><script>bad()</script><p>Readable</p>";
        var safe = MessageHtmlSafety.StripActiveContent(input);
        Assert.DoesNotContain("<link", safe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tracker.invalid", safe);
        Assert.DoesNotContain("<script", safe);
        Assert.StartsWith(MessageHtmlSafety.NoDnsPrefetch, safe);
        Assert.Equal(safe, MessageHtmlSafety.StripActiveContent(safe));
    }

    [Fact]
    public void MissingPreferredBrowserUsesShellAndPathsRemainOneArgument()
    {
        const string target = @"C:\fixture folder\mail.html";
        Assert.True(BrowserLauncher.StartInfo(target, null).UseShellExecute);
        Assert.Equal(target, BrowserLauncher.StartInfo(target, null).FileName);
        var file = Path.GetTempFileName();
        try
        {
            var info = BrowserLauncher.StartInfo(target, file);
            Assert.False(info.UseShellExecute);
            Assert.Equal(target, Assert.Single(info.ArgumentList));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void RemoteImageCacheIsBoundedLruAndChecksAllRedirectOrigins()
    {
        var cache = new RemoteImageCache(100, 2);
        var allowed = new HashSet<string> { "a", "b" };
        cache.Add("one", "111", ["a"]);
        cache.Add("two", "222", ["a", "b"]);
        Assert.Null(cache.Get("two", new HashSet<string> { "a" }));
        Assert.Equal("111", cache.Get("one", allowed));
        cache.Add("three", "333", ["a"]);
        Assert.Null(cache.Get("two", allowed));
        Assert.Equal("111", cache.Get("one", allowed));
        cache.Add("huge", new string('x', 100), ["a"]);
        Assert.Null(cache.Get("huge", allowed));
    }

    [Fact]
    public async Task BodiesWithoutContentLengthStopAtTheSizeBound()
    {
        using var input = new MemoryStream(new byte[100_000]);
        Assert.Null(await RemoteImageCache.ReadBoundedAsync(input, 1024));
        Assert.True(input.Position <= 16 * 1024);
        using var small = new MemoryStream([1, 2, 3]);
        Assert.Equal(new byte[] { 1, 2, 3 }, await RemoteImageCache.ReadBoundedAsync(small, 3));
    }

    [Fact]
    public async Task PasswordStdinPreservesSpacesAndRejectsConflictingInputs()
    {
        Assert.Equal(" synthetic password ", await PasswordInput.ReadAsync(["--password-stdin"], new StringReader(" synthetic password \r\n")));
        await Assert.ThrowsAsync<ArgumentException>(() => PasswordInput.ReadAsync(["--password-stdin", "--password", "fake"], new StringReader("ignored")));
        await Assert.ThrowsAsync<ArgumentException>(() => PasswordInput.ReadAsync(["--password-stdin"], new StringReader("")));
        Assert.Null(await PasswordInput.ReadAsync([], new StringReader("not consumed")));
    }
}
