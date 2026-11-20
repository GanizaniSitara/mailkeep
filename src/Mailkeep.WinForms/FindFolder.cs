using Mailkeep.Core;

namespace Mailkeep.WinForms;

internal sealed record FindFolder(string Name, string? Key, IReadOnlyList<string>? LocalKeys = null)
{
    public static IEnumerable<FindFolder> LocalChoices(IReadOnlyList<LocalFolder> folders)
    {
        var byKey = folders.ToDictionary(folder => folder.Key, StringComparer.OrdinalIgnoreCase);
        string Path(LocalFolder folder) => folder.ParentKey is { } parent && byKey.TryGetValue(parent, out var ancestor)
            ? Path(ancestor) + "/" + folder.Name : folder.Name;
        IEnumerable<string> Subtree(string key)
        {
            yield return key;
            foreach (var child in folders.Where(folder => folder.ParentKey == key))
                foreach (var descendant in Subtree(child.Key)) yield return descendant;
        }
        return folders.Select(folder => new FindFolder(Path(folder), folder.Key, Subtree(folder.Key).ToArray()));
    }
}
