using System.Text.RegularExpressions;

namespace Noelia.Infrastructure.Tests.Architecture;

/// <summary>
/// Keeps the demo under <c>demo/</c> a consumer of the packages rather than of
/// the source tree.
/// </summary>
/// <remarks>
/// The release gate proves something only while the demo installs Noelia the
/// way anyone else would: "leerer Paketcache, exakte Kandidatenversion, keine
/// Projektverweise". While the demo lived in its own directory that held by
/// itself. In the same repository a <c>ProjectReference</c> is one line away,
/// and with it the gate would be testing the working tree it was built from —
/// a green run that proves the packages restore, resolve and compose would
/// prove none of those things.
/// <para>
/// The guard is a test and not a sentence in a readme for the same reason as
/// the dependency budgets: nothing about a project reference announces what it
/// destroyed.
/// </para>
/// </remarks>
[Trait("Category", "Unit")]
public sealed class DemoStaysAForeignConsumerTests
{
    [Fact]
    public void No_demo_project_references_a_Noelia_project()
    {
        var offenders = DemoProjects()
            .Select(project => (Project: project, References: ProjectReferences(project)))
            .Where(entry => entry.References.Count > 0)
            .Select(entry =>
                $"{Path.GetFileName(entry.Project)} → {string.Join(", ", entry.References)}")
            .ToArray();

        offenders.Should().BeEmpty(
            "the demo must install Noelia as packages; a project reference would make "
            + "the release gate prove only that the working tree compiles against itself");
    }

    [Fact]
    public void Every_demo_project_that_uses_Noelia_takes_it_as_a_package()
    {
        var consumers = DemoProjects()
            .Where(project => File.ReadAllText(project)
                .Contains("PackageReference Include=\"Noelia.", StringComparison.Ordinal))
            .ToArray();

        consumers.Should().NotBeEmpty(
            "a demo that references no Noelia package is not exercising anything");
    }

    [Fact]
    public void The_solution_does_not_build_the_demo()
    {
        var solution = File.ReadAllText(Path.Combine(RepositoryRoot(), "Noelia.slnx"));

        solution.Should().NotContain("demo/",
            "the demo consumes published packages, so building it from Noelia.slnx would "
            + "restore a version that does not exist yet");
    }

    private static IReadOnlyList<string> DemoProjects()
    {
        var demo = Path.Combine(RepositoryRoot(), "demo");

        Directory.Exists(demo).Should().BeTrue(
            "this guard is meaningless if the directory it guards has moved");

        return Directory.GetFiles(demo, "*.csproj", SearchOption.AllDirectories);
    }

    /// <summary>
    /// The referenced paths that leave <c>demo/</c>. A reference between two
    /// demo projects is ordinary application layering and none of this test's
    /// business.
    /// </summary>
    private static IReadOnlyList<string> ProjectReferences(string project)
    {
        var directory = Path.GetDirectoryName(project)!;
        var demo = Path.Combine(RepositoryRoot(), "demo");

        return Regex.Matches(
                File.ReadAllText(project),
                "<ProjectReference\\s+Include=\"(?<path>[^\"]+)\"",
                RegexOptions.IgnoreCase)
            .Select(match => match.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar))
            .Select(path => Path.GetFullPath(Path.Combine(directory, path)))
            .Where(path => !path.StartsWith(demo + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToArray();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Noelia.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Noelia.slnx not found above {AppContext.BaseDirectory}");
    }
}
