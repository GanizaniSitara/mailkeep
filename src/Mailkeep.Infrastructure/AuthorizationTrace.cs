using System.Text.Json;

namespace Mailkeep.Infrastructure;

/// <summary>Stage-only authorization evidence. Never accepts credentials, callback URLs or provider errors.</summary>
public sealed class AuthorizationTrace(AppPaths paths, string account)
{
    private readonly string _id = Guid.NewGuid().ToString("N");
    public void Mark(string stage) => Write(stage, null);
    public void Fail(Exception error) => Write("failed", error.GetType().FullName);

    private void Write(string stage, string? errorType)
    {
        try
        {
            Directory.CreateDirectory(paths.Root);
            File.AppendAllText(Path.Combine(paths.Root, "authorization-trace.jsonl"), JsonSerializer.Serialize(new
            {
                utc = DateTimeOffset.UtcNow, operation = _id, account, stage, errorType,
                pid = Environment.ProcessId, executable = Environment.ProcessPath,
                root = paths.Root, receivingFile = paths.AccountsPath,
                receivingModifiedUtc = File.Exists(paths.AccountsPath) ? File.GetLastWriteTimeUtc(paths.AccountsPath) : (DateTime?)null
            }) + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
