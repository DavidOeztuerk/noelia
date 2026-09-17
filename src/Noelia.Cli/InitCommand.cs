using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Noelia.Cli;

/// <summary>
/// Sets a project up to use Noelia, and stops there.
/// </summary>
/// <remarks>
/// <para>It adds package references, writes the configuration sections those
/// packages read, and prints the composition root to paste. It does not edit
/// <c>Program.cs</c>. A tool that rewrites the file where a service decides
/// what it is gets one case wrong and costs more trust than the typing it
/// saved — and the snippet is eight lines, which is not the hard part of
/// adopting a library.</para>
///
/// <para>Nothing is overwritten. A key already present in configuration is left
/// exactly as it is and reported, because the second run of a setup command is
/// the one most likely to happen by accident.</para>
/// </remarks>
internal static class InitCommand
{
    private static readonly JsonSerializerOptions Write = new() { WriteIndented = true };

    internal static int Run(
        string directory,
        string serviceName,
        bool dryRun,
        TextWriter output)
    {
        var project = Directory.GetFiles(directory, "*.csproj", SearchOption.AllDirectories)
            .FirstOrDefault(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                    && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        if (project is null)
        {
            output.WriteLine($"No .csproj found under {directory}.");
            return 2;
        }

        output.WriteLine($"Project   {Path.GetRelativePath(directory, project)}");
        output.WriteLine($"Service   {serviceName}");

        if (dryRun)
        {
            output.WriteLine("Dry run — nothing is written.");
        }

        output.WriteLine();

        var changed = Packages(project, dryRun, output);
        changed |= Settings(Path.GetDirectoryName(project)!, serviceName, dryRun, output);

        output.WriteLine();
        output.WriteLine(changed
            ? "Add this to the composition root:"
            : "Nothing to change. The composition root looks like this:");
        output.WriteLine();
        output.WriteLine(Snippet(serviceName));

        return 0;
    }

    private static bool Packages(string project, bool dryRun, TextWriter output)
    {
        string[] wanted = ["Noelia.Infrastructure", "Noelia.Dashboard"];

        var document = XDocument.Load(project);

        var present = document.Descendants("PackageReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(id => id is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = wanted.Where(id => !present.Contains(id)).ToArray();

        foreach (var id in wanted.Where(present.Contains))
        {
            output.WriteLine($"  kept      {id} (already referenced)");
        }

        if (missing.Length == 0)
        {
            return false;
        }

        var group = document.Root!.Elements("ItemGroup")
            .FirstOrDefault(element => element.Elements("PackageReference").Any());

        if (group is null)
        {
            group = new XElement("ItemGroup");
            document.Root.Add(group);
        }

        foreach (var id in missing)
        {
            // No Version attribute. Central package management is the norm in a
            // repository that has a Directory.Packages.props, and writing a
            // version into the csproj there is an error rather than a default —
            // so the version is named in the printed instruction instead, where
            // a person decides what to do with it.
            group.Add(new XElement("PackageReference", new XAttribute("Include", id)));
            output.WriteLine($"  added     {id}");
        }

        if (!dryRun)
        {
            document.Save(project);
        }

        output.WriteLine();
        output.WriteLine("  Pin the version: dotnet add package Noelia.Infrastructure");

        return true;
    }

    private static bool Settings(
        string directory,
        string serviceName,
        bool dryRun,
        TextWriter output)
    {
        var path = Path.Combine(directory, "appsettings.json");

        JsonObject root;

        if (File.Exists(path))
        {
            root = JsonNode.Parse(
                File.ReadAllText(path),
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                }) as JsonObject ?? [];
        }
        else
        {
            root = [];
        }

        var wanted = Defaults(serviceName);
        var changed = false;

        foreach (var (section, contents) in wanted)
        {
            if (root.ContainsKey(section))
            {
                output.WriteLine($"  kept      {section} (already configured)");
                continue;
            }

            root[section] = contents;
            output.WriteLine($"  added     {section}");
            changed = true;
        }

        if (changed && !dryRun)
        {
            File.WriteAllText(path, root.ToJsonString(Write));
        }

        return changed;
    }

    /// <summary>
    /// The sections Noelia reads, with shapes rather than values.
    /// </summary>
    /// <remarks>
    /// Every value here is either a real default or an empty string. Never a
    /// plausible-looking secret: a generated placeholder that reads like a key
    /// is a key somebody ships, and the tool that wrote it will not be blamed.
    /// </remarks>
    private static IReadOnlyList<(string Section, JsonNode Contents)> Defaults(string serviceName) =>
    [
        ("Noelia", new JsonObject
        {
            ["ServiceName"] = serviceName
        }),
        ("Dashboard", new JsonObject
        {
            ["Fleet"] = string.Empty
        }),
        ("Jwt", new JsonObject
        {
            // Named, empty, and read from the environment. The variables are
            // NOELIA_JWT_SECRET, NOELIA_JWT_ISSUER and NOELIA_JWT_AUDIENCE.
            ["Issuer"] = string.Empty,
            ["Audience"] = string.Empty
        })
    ];

    private static string Snippet(string serviceName)
    {
        var builder = new StringBuilder();

        builder.AppendLine("var builder = WebApplication.CreateBuilder(args);")
            .AppendLine()
            .AppendLine("builder.Services.AddNoelia(")
            .AppendLine("    builder.Configuration,")
            .AppendLine("    builder.Environment,")
            .AppendLine($"    \"{serviceName}\",")
            .AppendLine("    noelia => noelia")
            .AppendLine("        .UseDefaults()")
            .AppendLine("        .UseDashboard(dashboard => dashboard.VisibleTo(")
            .AppendLine("            context => context.User.IsInRole(\"operator\")))")
            .AppendLine("        .AddSovereignPlatform(sovereign => sovereign")
            .AppendLine("            .AllowLoopback()")
            .AppendLine("            .AllowPrivateNetworks()));")
            .AppendLine()
            .AppendLine("var app = builder.Build();")
            .AppendLine()
            .AppendLine($"app.UseNoelia(app.Environment, \"{serviceName}\");")
            .AppendLine()
            .AppendLine("app.Run();");

        return builder.ToString();
    }
}
