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

        // Whether this repository pins versions centrally decides whether a
        // version belongs in the csproj. Writing one where Directory.Packages.props
        // manages them is NU1008 and the build stops; leaving one out where it
        // does not is NU1604 and the restore stops. Guessing either way breaks
        // somebody, so this looks.
        var central = ManagesVersionsCentrally(project);

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

        var version = Version();

        foreach (var id in missing)
        {
            var reference = new XElement("PackageReference", new XAttribute("Include", id));

            if (!central)
            {
                reference.Add(new XAttribute("Version", version));
            }

            group.Add(reference);
            output.WriteLine($"  added     {id}{(central ? string.Empty : " " + version)}");
        }

        if (!dryRun)
        {
            Save(document, project);
        }

        output.WriteLine();
        output.WriteLine(central
            ? $"  Versions are managed centrally here. Add <PackageVersion Include=\"Noelia.*\" "
              + $"Version=\"{version}\" /> to Directory.Packages.props."
            : $"  Pinned to {version}, the version of this tool. All Noelia packages ship "
              + "under one version; mixing them is untested.");

        return true;
    }

    /// <summary>
    /// Writes the project file back without adding anything to it.
    /// </summary>
    /// <remarks>
    /// <c>XDocument.Save(path)</c> writes a UTF-8 byte order mark and an XML
    /// declaration that a csproj written by the SDK does not have. Neither
    /// breaks anything, and both turn "added two package references" into a
    /// whole-file diff — which is how a setup command earns a reputation for
    /// touching more than it said it would.
    /// </remarks>
    private static void Save(XDocument document, string path)
    {
        var settings = new System.Xml.XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            OmitXmlDeclaration = document.Declaration is null,
            Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };

        using var writer = System.Xml.XmlWriter.Create(path, settings);
        document.Save(writer);
    }

    /// <summary>
    /// Whether a Directory.Packages.props above this project manages versions.
    /// </summary>
    /// <remarks>
    /// Walks upward, because that is how MSBuild finds the file, and stops at
    /// the filesystem root rather than at a repository boundary this tool has no
    /// reliable way to recognise.
    /// </remarks>
    private static bool ManagesVersionsCentrally(string project)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(project))!);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Directory.Packages.props");

            if (File.Exists(candidate))
            {
                try
                {
                    return XDocument.Load(candidate)
                        .Descendants("ManagePackageVersionsCentrally")
                        .Any(element => element.Value.Trim()
                            .Equals("true", StringComparison.OrdinalIgnoreCase));
                }
                catch (System.Xml.XmlException)
                {
                    // Unparseable: treat it as absent rather than guessing. The
                    // printed instruction covers whichever it turns out to be.
                    return false;
                }
            }

            directory = directory.Parent;
        }

        return false;
    }

    /// <summary>
    /// The version to pin to: this tool's own.
    /// </summary>
    /// <remarks>
    /// All fourteen packages ship under one version, and the tool is one of
    /// them — so the version a user just installed is the version whose
    /// packages it should wire up. Anything else would have it recommend a
    /// combination nobody tested together.
    /// </remarks>
    private static string Version()
    {
        var informational = typeof(InitCommand).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        // "6.4.0+abc123" — the build metadata is not part of a NuGet version.
        return informational?.Split('+')[0]
            ?? typeof(InitCommand).Assembly.GetName().Version?.ToString(3)
            ?? "6.4.0";
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

    /// <summary>
    /// The composition root, with its using directives.
    /// </summary>
    /// <remarks>
    /// The usings are not decoration. Every call below lives in a different
    /// namespace — <c>AddNoelia</c> and <c>UseNoelia</c> in
    /// <c>Infrastructure.Extensions</c>, <c>AddSovereignPlatform</c> in
    /// <c>Infrastructure.Builder</c>, <c>UseDashboard</c> in
    /// <c>Noelia.Dashboard</c> — and a snippet that left one out compiles to
    /// "no such extension method", which reads to a newcomer as "this package
    /// does not have that". It is a documented failure here: the same omission
    /// in the README cost a patch release.
    /// </remarks>
    private static string Snippet(string serviceName)
    {
        var builder = new StringBuilder();

        builder.AppendLine("using Noelia.Dashboard;")
            .AppendLine("using Noelia.Infrastructure.Builder;")
            .AppendLine("using Noelia.Infrastructure.Builder.Modules;   // the pipeline steps")
            .AppendLine("using Noelia.Infrastructure.Extensions;")
            .AppendLine()
            .AppendLine("var builder = WebApplication.CreateBuilder(args);")
            .AppendLine()
            .AppendLine("builder.Services.AddNoelia(")
            .AppendLine("    builder.Configuration,")
            .AppendLine("    builder.Environment,")
            .AppendLine($"    \"{serviceName}\",")
            .AppendLine("    noelia =>")
            .AppendLine("    {")
            .AppendLine("        noelia.UseDefaults();")
            .AppendLine()
            .AppendLine("        // In Production the dashboard refuses to compose until the")
            .AppendLine("        // exposure is an explicit decision with a reason — so it is")
            .AppendLine("        // left out here rather than crashing on your first run.")
            .AppendLine("        // To expose it there, drop this condition and add")
            .AppendLine("        // .InProduction(\"why it is reachable and to whom\").")
            .AppendLine("        if (!builder.Environment.IsProduction())")
            .AppendLine("        {")
            .AppendLine("            noelia.UseDashboard(dashboard => dashboard")
            .AppendLine("                .At(\"/noelia\")")
            .AppendLine("                .VisibleTo(context => context.User.IsInRole(\"operator\")));")
            .AppendLine("        }")
            .AppendLine()
            .AppendLine("        // Loopback and private networks are allowed already; name")
            .AppendLine("        // every public host this service may reach. Declared")
            .AppendLine("        // dependencies appear in the destination register.")
            .AppendLine("        noelia.AddSovereignPlatform(sovereign => sovereign")
            .AppendLine("            .DeclareDependency(")
            .AppendLine("                \"Database\",")
            .AppendLine("                builder.Configuration.GetConnectionString(\"Default\")));")
            .AppendLine("    });")
            .AppendLine()
            .AppendLine("var app = builder.Build();")
            .AppendLine()
            .AppendLine("// The default chain includes UseAuth(), which needs an")
            .AppendLine("// authentication scheme — UseJwt(...) while configuring Noelia, or")
            .AppendLine("// your own AddAuthentication(...). A new service usually has neither")
            .AppendLine("// yet, so the steps are named here and UseAuth() is added once there")
            .AppendLine("// is something for it to read. The order below carries security")
            .AppendLine("// weight; the reasons are in the comments on UseNoelia.")
            .AppendLine($"app.UseNoelia(app.Environment, \"{serviceName}\", pipeline => pipeline")
            .AppendLine("    .UseExceptionHandling()")
            .AppendLine("    .UseCorrelationId()")
            .AppendLine("    .UseSecurityHeaders()")
            .AppendLine("    .UseHealthCheckEndpoints());")
            .AppendLine()
            .AppendLine("app.Run();");

        return builder.ToString();
    }
}
