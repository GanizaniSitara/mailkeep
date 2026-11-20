namespace Mailkeep.Infrastructure;

/// <summary>The files named by each repeatable --attach option, as full paths. Every one is checked before any
/// draft is touched, so a mistyped path fails the command instead of saving a draft without it.</summary>
public static class AttachmentArguments
{
    public const string Option = "--attach";

    public static IReadOnlyList<string> Parse(IReadOnlyList<string> args)
    {
        var paths = new List<string>();
        for (var index = 0; index < args.Count; index++)
        {
            if (!args[index].Equals(Option, StringComparison.OrdinalIgnoreCase)) continue;
            if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
                throw new ArgumentException($"{Option} requires a file path.");
            var path = Path.GetFullPath(args[++index]);
            if (!File.Exists(path)) throw new FileNotFoundException($"Attachment not found: {path}", path);
            paths.Add(path);
        }
        return paths;
    }
}
