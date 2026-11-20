using Mailkeep.Infrastructure;

namespace Mailkeep.WinForms;

internal sealed record SendFailureNotice(string Message, string LogPath, bool LogWritten)
{
    internal static async Task<SendFailureNotice> CreateAsync(string root, IReadOnlyList<SendResult> failures)
    {
        var logPath = Path.Combine(root, "errors.log");
        var details = string.Join(Environment.NewLine, failures.Select(result => $"{result.Id}: {result.Error}"));
        var written = false;
        try
        {
            await File.AppendAllTextAsync(logPath,
                $"{DateTimeOffset.Now:O} Sending queued messages: {failures.Count} failed{Environment.NewLine}{details}{Environment.NewLine}{Environment.NewLine}")
                .ConfigureAwait(false);
            written = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

        var message = failures.Count == 1
            ? "1 message could not be sent.\n\nThe message is still in Outbox."
            : $"{failures.Count} messages could not be sent.\n\nThey are still in Outbox.";
        message += "\nTry Send/Receive again later.\n\n" + (written
            ? "Technical details have been saved to:\n" + logPath
            : "Technical details could not be written to the error log.");
        return new(message, logPath, written);
    }
}
