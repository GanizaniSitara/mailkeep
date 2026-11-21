namespace Mailkeep.Cli;

internal static class PasswordInput
{
    public static async Task<string?> ReadAsync(string[] args, TextReader input)
    {
        var stdin = args.Contains("--password-stdin", StringComparer.OrdinalIgnoreCase);
        var index = Array.FindIndex(args, value => value.Equals("--password", StringComparison.OrdinalIgnoreCase));
        if (stdin && index >= 0) throw new ArgumentException("Use only one of --password-stdin or --password.");
        if (!stdin) return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        var password = await input.ReadLineAsync();
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("No password received on standard input.");
        return password;
    }
}
