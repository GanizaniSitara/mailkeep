using Mailkeep.Infrastructure;

namespace Mailkeep.Tests;

public sealed class AuthorizationTraceTests
{
    [Fact]
    public void RecordsStagesAndFailureTypeWithoutExceptionContents()
    {
        var root = Path.Combine(Path.GetTempPath(), "mailkeep-auth-trace-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(root);
            var trace = new AuthorizationTrace(new AppPaths(root), "local:fixture@example.invalid");
            trace.Mark("receiving_saved");
            trace.Fail(new InvalidOperationException("secret-token-and-code"));
            var text = File.ReadAllText(Path.Combine(root, "authorization-trace.jsonl"));
            Assert.Contains("receiving_saved", text);
            Assert.Contains("InvalidOperationException", text);
            Assert.DoesNotContain("secret-token-and-code", text);
            Assert.Contains("accounts.v2.dpapi", text);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
