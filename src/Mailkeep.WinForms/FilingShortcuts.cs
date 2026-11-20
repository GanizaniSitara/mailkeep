using Mailkeep.Core;
using Mailkeep.Infrastructure;

namespace Mailkeep.WinForms;

/// <summary>Alt+1 to Alt+9 and Alt+0, each filing the selected mail into a folder chosen under Tools > Options.
/// Kept as settings, one per key, holding the folder's key — so a folder renamed or moved keeps its shortcut.</summary>
internal static class FilingShortcuts
{
    /// <summary>The keys in the order they sit on the keyboard, which is the order Options lists them in.</summary>
    public static readonly int[] Digits = [1, 2, 3, 4, 5, 6, 7, 8, 9, 0];

    private static string Setting(int digit) => "shortcut.file." + digit;

    public static string? Folder(MailDatabase database, int digit)
        => database.GetSetting(Setting(digit)) is { Length: > 0 } key ? key : null;

    public static void Save(MailDatabase database, int digit, string? folderKey)
        => database.SetSetting(Setting(digit), folderKey ?? "");

    /// <summary>The folders mail can be filed into, by their full path: not Drafts or the Outbox.</summary>
    public static IReadOnlyList<FindFolder> Destinations(IReadOnlyList<LocalFolder> folders)
    {
        var fileable = folders.Where(folder => folder.Kind is not (LocalFolderKinds.Drafts or LocalFolderKinds.Outbox))
            .Select(folder => folder.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return FindFolder.LocalChoices(folders).Where(folder => folder.Key is not null && fileable.Contains(folder.Key))
            .OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    /// <summary>One line of a folder drop-down drawn as the tree: the folder, how deep it sits, and its full path
    /// for when it is the one chosen.</summary>
    public sealed record TreeChoice(string? Key, string Name, string Leaf, int Depth, string Kind)
    {
        public override string ToString() => Name;
    }

    /// <summary>The fileable folders in the order the folder tree shows them — the standard ones first, each
    /// folder followed by what is inside it — rather than sorted as a list of paths.</summary>
    public static IReadOnlyList<TreeChoice> Tree(IReadOnlyList<LocalFolder> folders)
    {
        var paths = FindFolder.LocalChoices(folders).ToDictionary(folder => folder.Key!, folder => folder.Name, StringComparer.OrdinalIgnoreCase);
        var keys = folders.Select(folder => folder.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var children = folders.ToLookup(folder => folder.ParentKey is { } parent && keys.Contains(parent) ? parent : "");
        var ordered = new List<TreeChoice>();
        void Walk(string parent, int depth)
        {
            // In the order the store lists them, which is the order the folder tree itself is built in.
            foreach (var folder in children[parent])
            {
                if (folder.Kind is LocalFolderKinds.Drafts or LocalFolderKinds.Outbox) continue;
                ordered.Add(new TreeChoice(folder.Key, paths[folder.Key], folder.Name, depth, folder.Kind));
                Walk(folder.Key, depth + 1);
            }
        }
        Walk("", 0);
        return ordered;
    }

    /// <summary>Makes a drop-down draw its folders as the tree: indented by depth, each with its folder's icon,
    /// and the chosen one shown by its full path.</summary>
    public static void DrawAsTree(ComboBox box)
    {
        box.DrawMode = DrawMode.OwnerDrawFixed;
        box.ItemHeight = 18;
        var icons = new Dictionary<string, Image?>();
        Image? Icon(string kind) => icons.TryGetValue(kind, out var icon) ? icon : icons[kind] = OeIcons.At16(kind switch
        {
            LocalFolderKinds.Inbox => 114,
            LocalFolderKinds.Sent => 79,
            LocalFolderKinds.Deleted => 117,
            _ => 111
        });
        box.DrawItem += (_, e) =>
        {
            e.DrawBackground();
            if (e.Index < 0 || box.Items[e.Index] is not TreeChoice choice) return;
            // The closed box shows where mail will go in full; the open list shows the tree.
            var editing = (e.State & DrawItemState.ComboBoxEdit) != 0;
            var left = e.Bounds.Left + 2 + (editing ? 0 : choice.Depth * 16);
            if (choice.Key is not null && Icon(choice.Kind) is { } icon)
            {
                e.Graphics.DrawImage(icon, left, e.Bounds.Top + (e.Bounds.Height - 16) / 2, 16, 16);
                left += 20;
            }
            var colour = (e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText : SystemColors.WindowText;
            TextRenderer.DrawText(e.Graphics, editing ? choice.Name : choice.Leaf, box.Font,
                new Rectangle(left, e.Bounds.Top, Math.Max(0, e.Bounds.Right - left), e.Bounds.Height), colour,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        };
    }
}
