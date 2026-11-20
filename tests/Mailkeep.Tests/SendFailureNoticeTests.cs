using Mailkeep.Infrastructure;
using Mailkeep.WinForms;

namespace Mailkeep.Tests;

public sealed class SendFailureNoticeTests
{
    [Fact]
    public async Task MultipleFailuresKeepRawTechnicalDetailsInLogOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "mailkeep-send-notice-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var raw = "An existing connection was forcibly closed by the remote host.\nSSL/TLS certificate checklist";
            var notice = await SendFailureNotice.CreateAsync(root,
                [new SendResult("first", false, raw), new SendResult("second", false, raw)]);
            Assert.Contains("2 messages could not be sent", notice.Message);
            Assert.Contains("still in Outbox", notice.Message);
            Assert.Contains("Send/Receive", notice.Message);
            Assert.DoesNotContain(raw, notice.Message);
            Assert.DoesNotContain("certificate", notice.Message);
            Assert.True(notice.LogWritten);
            Assert.Contains(notice.LogPath, notice.Message);
            var log = await File.ReadAllTextAsync(notice.LogPath);
            Assert.Contains("first: " + raw, log);
            Assert.Contains("second: " + raw, log);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UnwritableLogDoesNotHideSendFailureOrClaimDetailsWereSaved()
    {
        var root = Path.GetTempFileName();
        try
        {
            var notice = await SendFailureNotice.CreateAsync(root, [new SendResult("first", false, "535 Authentication failed")]);
            Assert.False(notice.LogWritten);
            Assert.Contains("1 message could not be sent", notice.Message);
            Assert.Contains("still in Outbox", notice.Message);
            Assert.Contains("could not be written", notice.Message);
            Assert.DoesNotContain("have been saved", notice.Message);
            Assert.DoesNotContain("535", notice.Message);
        }
        finally { File.Delete(root); }
    }
}
