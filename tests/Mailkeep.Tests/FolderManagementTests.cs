using Mailkeep.Core;
using Mailkeep.Infrastructure;

namespace Mailkeep.Tests;

/// <summary>Folders the user makes and moves themselves. Local folders are rows, so an empty one is fine;
/// an imported folder is only the path its messages carry, so moving one is a rewrite of those paths.</summary>
public sealed class FolderManagementTests
{
    [Fact]
    public void FilingASelectionRollsBackIfOneMessageConflictsAtTheDestination()
    {
        using var temp = new TempDir();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        var target = services.Database.CreateLocalFolder("Filed", null);
        long Add(string folder, string key) => services.Database.Upsert(new MessageInput(MailContract.LiveSource, "local",
            folder, key, null, 0, "Message " + key, "s@example.invalid", "Sender", ["r@example.invalid"],
            DateTimeOffset.UnixEpoch, 10, true, 0, "Body", null, null));
        var first = Add(LocalFolderKinds.Inbox, "first");
        var second = Add(LocalFolderKinds.Inbox, "second");
        Add(target.Key, "second");

        Assert.Throws<InvalidOperationException>(() => services.Database.MoveMessages([first, second], target.Key));
        Assert.Equal(LocalFolderKinds.Inbox, services.Database.GetMessage(first)!.Summary.Folder);
        Assert.Equal(LocalFolderKinds.Inbox, services.Database.GetMessage(second)!.Summary.Folder);
        Assert.Equal(3, services.Database.ListMessages().Count);
        var empty = services.Database.CreateLocalFolder("Empty", null);
        Assert.Equal(2, services.Database.MoveMessages([first, first, second], empty.Key));
        Assert.Equal(0, services.Database.MoveMessages([first, second], empty.Key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nested")]
    public void SiblingNamesAreUniqueForCreationRenameAndMove(string? nesting)
    {
        using var temp = new TempDir();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        var parent = nesting is null ? null : services.Database.CreateLocalFolder(nesting, null).Key;
        var original = services.Database.CreateLocalFolder("Résumé", parent);
        var renamed = services.Database.CreateLocalFolder("Other", parent);
        var elsewhere = services.Database.CreateLocalFolder("Elsewhere", null);
        var moving = services.Database.CreateLocalFolder("RÉSUMÉ", elsewhere.Key);
        services.Database.Upsert(new MessageInput(MailContract.LiveSource, "local", moving.Key, "fixture", null, 0,
            "Retained mail", "s@example.invalid", "Sender", ["r@example.invalid"], DateTimeOffset.UnixEpoch,
            10, true, 0, "Body", null, null));

        Assert.Throws<InvalidOperationException>(() => services.Database.CreateLocalFolder(" résumé ", parent));
        Assert.Throws<InvalidOperationException>(() => services.Database.RenameLocalFolder(renamed.Key, "RÉSUMÉ"));
        Assert.Throws<InvalidOperationException>(() => services.Database.MoveLocalFolder(moving.Key, parent));

        Assert.Equal(1, services.Database.RenameLocalFolder(original.Key, "RÉSUMÉ"));
        var folders = services.Database.ListLocalFolders();
        Assert.Equal("Other", folders.Single(folder => folder.Key == renamed.Key).Name);
        Assert.Equal(elsewhere.Key, folders.Single(folder => folder.Key == moving.Key).ParentKey);
        Assert.Equal(moving.Key, Assert.Single(services.Database.ListMessages()).Folder);
        Assert.Equal(1, services.Database.MoveLocalFolder(original.Key, parent));
    }

    [Fact]
    public void LocalFolderMovesRefuseDescendantsAndMissingParentsWithoutChangingTheTree()
    {
        using var temp = new TempDir();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        var parent = services.Database.CreateLocalFolder("Parent", null);
        var child = services.Database.CreateLocalFolder("Child", parent.Key);
        var grandchild = services.Database.CreateLocalFolder("Grandchild", child.Key);

        Assert.Throws<InvalidOperationException>(() => services.Database.MoveLocalFolder(parent.Key, grandchild.Key));
        Assert.Throws<KeyNotFoundException>(() => services.Database.MoveLocalFolder(child.Key, "missing-parent"));
        var folders = services.Database.ListLocalFolders();
        Assert.Null(folders.Single(folder => folder.Key == parent.Key).ParentKey);
        Assert.Equal(parent.Key, folders.Single(folder => folder.Key == child.Key).ParentKey);
        Assert.Equal(child.Key, folders.Single(folder => folder.Key == grandchild.Key).ParentKey);
    }

    [Fact]
    public void AFolderOfOnesOwnCanBeCreatedEmptyNestedRenamedAndRemoved()
    {
        using var temp = new TempDir();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();

        var parent = services.Database.CreateLocalFolder("Work", null);
        var child = services.Database.CreateLocalFolder("2015", parent.Key);

        var folders = services.Database.ListLocalFolders();
        Assert.Contains(folders, folder => folder.Key == parent.Key && folder.ParentKey is null);
        Assert.Contains(folders, folder => folder.Key == child.Key && folder.ParentKey == parent.Key);

        services.Database.RenameLocalFolder(child.Key, "2015-2016");
        Assert.Equal("2015-2016", services.Database.ListLocalFolders().Single(f => f.Key == child.Key).Name);

        // Out to the top level, then away entirely.
        services.Database.MoveLocalFolder(child.Key, null);
        Assert.Null(services.Database.ListLocalFolders().Single(f => f.Key == child.Key).ParentKey);
        services.Database.DeleteLocalFolder(child.Key);
        Assert.DoesNotContain(services.Database.ListLocalFolders(), f => f.Key == child.Key);
    }

    [Fact]
    public void AFolderThatStillHoldsSomethingIsNotDeleted()
    {
        using var temp = new TempDir();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();

        var keep = services.Database.CreateLocalFolder("Keep", null);
        var inside = services.Database.CreateLocalFolder("Inside", keep.Key);
        Assert.Throws<InvalidOperationException>(() => services.Database.DeleteLocalFolder(keep.Key));

        services.Database.DeleteLocalFolder(inside.Key);
        var holding = services.Database.CreateLocalFolder("Holding", null);
        services.Database.Upsert(new MessageInput(MailContract.LiveSource, "local", holding.Key, "k1", null, 0,
            "Subject", "s@example.invalid", "S", ["r@example.invalid"], DateTimeOffset.UnixEpoch, 10, true, 0, "body", null, null));
        Assert.Throws<InvalidOperationException>(() => services.Database.DeleteLocalFolder(holding.Key));

        // The standard folders are not the user's to remove.
        var inbox = services.Database.ListLocalFolders().First(f => f.Kind == LocalFolderKinds.Inbox);
        Assert.Throws<InvalidOperationException>(() => services.Database.DeleteLocalFolder(inbox.Key));
    }

    [Fact]
    public void MovingAnImportedFolderTakesEverythingBeneathItAlong()
    {
        using var temp = new TempDir();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        const string account = "Work Archive";
        Archive(services, account, "ToMe", "a");
        Archive(services, account, "ToMe/2015", "b");
        Archive(services, account, "ToMeAlso", "c");   // shares a prefix but is a different folder
        Archive(services, account, "CcMe", "d");

        var moved = services.Database.MoveArchiveFolder(MailContract.ArchiveSource, account, "ToMe", "Work/ToMe");

        Assert.Equal(2, moved);
        var folders = Folders(services, account);
        Assert.Contains("Work/ToMe", folders);
        Assert.Contains("Work/ToMe/2015", folders);
        // A folder whose name merely starts the same must not be dragged along with it.
        Assert.Contains("ToMeAlso", folders);
        Assert.Contains("CcMe", folders);
    }

    [Fact]
    public void AnImportedFolderCannotBeMovedInsideItself()
    {
        using var temp = new TempDir();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        Archive(services, "Work Archive", "Projects", "a");

        Assert.Throws<InvalidOperationException>(() =>
            services.Database.MoveArchiveFolder(MailContract.ArchiveSource, "Work Archive", "Projects", "Projects/Sub"));
    }

    [Fact]
    public void MovingImportedFoldersLeavesOtherAccountsAlone()
    {
        using var temp = new TempDir();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        Archive(services, "One", "ToMe", "a");
        Archive(services, "Two", "ToMe", "b");

        services.Database.MoveArchiveFolder(MailContract.ArchiveSource, "One", "ToMe", "Work/ToMe");

        Assert.Contains("Work/ToMe", Folders(services, "One"));
        Assert.Contains("ToMe", Folders(services, "Two"));
    }


    [Fact]
    public void AnImportedFolderCanBeMadeBeforeThereIsAnythingToPutInIt()
    {
        using var temp = new TempDir();
        var services = new MailkeepServices(temp.Path);
        services.Database.Initialize();
        const string account = "Work Archive";
        Archive(services, account, "ToMe", "a");

        services.Database.CreateArchiveFolder(MailContract.ArchiveSource, account, "ToMe/2015");

        // It survives a rebuild of the tree despite holding nothing.
        Assert.Contains("ToMe/2015", Folders(services, account));

        // And it travels with its parent rather than being stranded at the old path.
        services.Database.MoveArchiveFolder(MailContract.ArchiveSource, account, "ToMe", "Work/ToMe");
        var folders = Folders(services, account);
        Assert.Contains("Work/ToMe/2015", folders);
        Assert.DoesNotContain("ToMe/2015", folders);

        services.Database.DeleteArchiveFolder(MailContract.ArchiveSource, account, "Work/ToMe/2015");
        Assert.DoesNotContain("Work/ToMe/2015", Folders(services, account));
        // The one that holds mail is not the user's to delete out from under it.
        Assert.Throws<InvalidOperationException>(() =>
            services.Database.DeleteArchiveFolder(MailContract.ArchiveSource, account, "Work/ToMe"));
    }

    private static void Archive(MailkeepServices services, string account, string folder, string key)
        => services.Database.Upsert(new MessageInput(MailContract.ArchiveSource, account, folder, key, null, 0,
            "Subject " + key, "s@example.invalid", "S", ["r@example.invalid"], DateTimeOffset.UnixEpoch, 10, true, 0, "body", null, null));

    private static IReadOnlyList<string> Folders(MailkeepServices services, string account)
        => services.Database.FolderCounts(MailContract.ArchiveSource)
            .Where(folder => folder.Account == account).Select(folder => folder.Folder).ToArray();

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mailkeep-folders-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
