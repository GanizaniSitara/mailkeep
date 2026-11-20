namespace Mailkeep.Infrastructure;

/// <summary>The archive's relative paths as actual parent/child nodes, including implied containers.</summary>
public static class ArchiveTree
{
    public sealed record Node(string Name, string Path, ArchiveStore.Folder? Folder, Node[] Children);

    public static Node[] Build(IEnumerable<ArchiveStore.Folder> folders)
    {
        var entries = folders.ToArray();
        Node[] Children(string parent)
        {
            var prefix = parent.Length == 0 ? "" : parent + "/";
            return entries.Where(folder => folder.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(folder => folder.Label[prefix.Length..].Split('/')[0]).Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
                .Select(name =>
                {
                    var path = prefix + name;
                    return new Node(name, path, entries.SingleOrDefault(folder => folder.Label.Equals(path, StringComparison.OrdinalIgnoreCase)), Children(path));
                }).ToArray();
        }
        return Children("");
    }
}
