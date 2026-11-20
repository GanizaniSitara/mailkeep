using System.Reflection;
using Mailkeep.Infrastructure;

namespace Mailkeep.Tests;

/// <summary>The migration toolkit exists to be thrown away once a migration is done, so the product must
/// never depend on it. It now lives in its own repository, and these fail the build the moment it creeps
/// back in — by assembly reference, or by a project reference reaching outside this one.</summary>
public sealed class ProductBoundaryTests
{
    /// <summary>The only Mailkeep assemblies the product may consist of. Stated as an allowlist rather than
    /// a list of banned names so that any future tool is caught by construction, without this test having to
    /// know it exists.</summary>
    private static readonly string[] ProductAssemblies = ["Mailkeep.Core", "Mailkeep.Infrastructure"];

    [Fact]
    public void ShippingAssembliesConsistOnlyOfTheProductLibraries()
    {
        foreach (var assembly in new[] { typeof(MailkeepServices).Assembly, typeof(Core.MailContract).Assembly })
        {
            var offenders = assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name ?? "")
                .Where(name => name.StartsWith("Mailkeep", StringComparison.OrdinalIgnoreCase))
                .Where(name => !ProductAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            Assert.True(offenders.Length == 0, $"{assembly.GetName().Name} references {string.Join(", ", offenders)}");
        }
    }

    [Fact]
    public void NoProjectReachesOutsideThisRepository()
    {
        var root = Repository();
        var offenders = new List<string>();
        foreach (var project in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            if (project.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || project.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var text = File.ReadAllText(project);
            // A reference climbing above the repository root, or naming the toolkit, means the split has leaked.
            if (text.Contains(@"..\..\..\", StringComparison.Ordinal)
                || text.Contains("MailkeepSrc", StringComparison.Ordinal)
                || text.Contains(@"tools\", StringComparison.OrdinalIgnoreCase)
                || text.Contains("tools/", StringComparison.OrdinalIgnoreCase))
                offenders.Add(Path.GetFileName(project));
        }
        Assert.True(offenders.Count == 0, $"projects reach outside the product repository: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheMigrationToolkitIsNotInThisRepository()
    {
        var root = Repository();
        Assert.False(Directory.Exists(Path.Combine(root, "tools")),
            "tools/ is back in the product repository; the migration toolkit belongs in mailkeep-migration.");
    }

    /// <summary>Finds the repository root by the solution file, which is the one thing that stays put.</summary>
    private static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mailkeep.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
