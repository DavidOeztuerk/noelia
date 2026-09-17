using System.Text.Json;

namespace Noelia.Infrastructure.Tests.Architecture;

/// <summary>
/// Makes the complete NuGet cost of every shipped package visible and reviewable.
/// </summary>
[Trait("Category", "Unit")]
public class PackageDependencyBudgetTests
{
    private static readonly IReadOnlyDictionary<string, int> Expected =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Noelia.Abstractions"] = 8,
            ["Noelia.Application"] = 4,

            // The one package here that is a tool rather than a library. Nothing
            // a consumer ships carries any of this: it is installed with
            // `dotnet tool install -g Noelia.Cli` and run, never referenced. So
            // its budget is pinned for the same reason as the others — growth
            // should be a decision — but the number itself is allowed to be
            // large where a library's is argued over.
            ["Noelia.Cli"] = 32,
            ["Noelia.Contracts"] = 0,
            ["Noelia.Core"] = 0,
            ["Noelia.Data.EntityFrameworkCore"] = 19,
            ["Noelia.Dashboard"] = 0,
            ["Noelia.Http"] = 0,
            ["Noelia.InMemory"] = 10,
            ["Noelia.Infrastructure"] = 44,
            ["Noelia.Messaging.MassTransit"] = 4,
            ["Noelia.Passwords.Argon2"] = 10,
            ["Noelia.Passwords.BCrypt"] = 9,
            ["Noelia.Redis"] = 15
        };

    [Fact]
    public void Every_shipped_package_has_its_full_dependency_load_pinned()
    {
        var source = Path.Combine(RepositoryRoot(), "src");
        var projects = Directory.GetFiles(source, "*.csproj", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetFileNameWithoutExtension(path)!,
                StringComparer.Ordinal);

        projects.Keys.Order().Should().Equal(Expected.Keys.Order(),
            "a new package needs an explicit dependency budget before it ships");

        foreach (var (package, expectedCount) in Expected)
        {
            var projectDirectory = Path.GetDirectoryName(projects[package])!;
            var assetsFile = Path.Combine(projectDirectory, "obj", "project.assets.json");
            File.Exists(assetsFile).Should().BeTrue(
                $"{package} must have been restored before its dependency load is measured");

            using var assets = JsonDocument.Parse(File.ReadAllText(assetsFile));
            var actualCount = assets.RootElement.GetProperty("libraries")
                .EnumerateObject()
                .Count(library => library.Value.GetProperty("type").GetString() == "package");

            actualCount.Should().Be(expectedCount,
                $"the README promises {expectedCount} restored NuGet packages for {package}; "
                + "dependency growth requires an explicit review and README update");
        }
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
