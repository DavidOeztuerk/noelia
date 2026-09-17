using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Noelia.Cli;

/// <summary>
/// Reads a project directory and reports what its Noelia wiring looks like.
/// </summary>
/// <remarks>
/// <para><strong>What this can and cannot see.</strong> It reads source text,
/// project files and configuration files. It therefore sees a literal
/// <c>noelia.UseRedisCache("x")</c> and does not see
/// <c>noelia.Use(configuration["Module"])</c>, a call behind a helper method,
/// or anything decided at runtime. Every result says <c>static</c> so that a
/// reader knows which kind of reading they have — and <c>analyze --url</c>
/// reads the composition that actually happened, which is the authoritative
/// answer whenever a service is running.</para>
///
/// <para>Text rather than Roslyn on purpose. A semantic model would need the
/// target project's own SDK resolved through MSBuildLocator, would fail on a
/// project that has not been restored, and would still miss everything
/// config-driven. It would look far more authoritative than it is, and this
/// tool's usefulness rests on being believed about its own limits.</para>
/// </remarks>
internal static class ProjectScan
{
    private static readonly string[] SecretishKeys =
    [
        "password", "secret", "key", "token", "connectionstring", "apikey", "pwd"
    ];

    internal static AnalysisResult Run(string directory)
    {
        var findings = new List<Finding>();
        var observations = new List<string>();

        var projects = Directory.GetFiles(directory, "*.csproj", SearchOption.AllDirectories)
            .Where(NotUnderBuildOutput)
            .ToArray();

        var sources = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(NotUnderBuildOutput)
            .ToArray();

        var settings = Directory.GetFiles(directory, "appsettings*.json", SearchOption.AllDirectories)
            .Where(NotUnderBuildOutput)
            .ToArray();

        observations.Add($"{projects.Length} project file(s), {sources.Length} source file(s), "
                         + $"{settings.Length} configuration file(s).");

        var references = Packages(projects, findings, observations);
        var text = string.Join('\n', sources.Select(SafeRead));

        Composition(text, references, findings, observations);
        EagerProviders(sources, findings);
        Egress(text, references, findings, observations);
        Configuration(settings, findings, observations);

        return new AnalysisResult(
            directory,
            "static",
            [.. findings.OrderByDescending(finding => finding.Severity)
                        .ThenBy(finding => finding.Id, StringComparer.Ordinal)],
            observations);
    }

    /// <summary>
    /// Ignores anything under <c>bin</c> or <c>obj</c>.
    /// </summary>
    /// <remarks>
    /// A copied appsettings.json under bin is the same file twice, and a
    /// generated .cs under obj is not something anybody wrote — reporting
    /// either as a finding sends the reader to a file they cannot fix.
    /// </remarks>
    private static bool NotUnderBuildOutput(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar);

        return !parts.Contains("bin", StringComparer.OrdinalIgnoreCase)
            && !parts.Contains("obj", StringComparer.OrdinalIgnoreCase)
            && !parts.Contains("node_modules", StringComparer.Ordinal);
    }

    private static string SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static Dictionary<string, string?> Packages(
        IReadOnlyList<string> projects,
        List<Finding> findings,
        List<string> observations)
    {
        var references = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
        {
            XDocument document;

            try
            {
                document = XDocument.Load(project);
            }
            catch (System.Xml.XmlException)
            {
                findings.Add(new Finding(
                    "noelia.project.unreadable",
                    FindingSeverity.Warning,
                    Path.GetFileName(project),
                    "The project file could not be parsed as XML, so its package references "
                    + "were not read.",
                    "Open it and fix the markup, or exclude the directory from the scan."));
                continue;
            }

            foreach (var reference in document.Descendants("PackageReference"))
            {
                var id = reference.Attribute("Include")?.Value;

                if (id is null || !id.StartsWith("Noelia", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                references[id] = reference.Attribute("Version")?.Value;
            }
        }

        if (references.Count == 0)
        {
            observations.Add("No Noelia package reference found.");
            return references;
        }

        observations.Add("Noelia packages: " + string.Join(", ", references
            .Select(entry => entry.Value is null ? entry.Key : $"{entry.Key} {entry.Value}")
            .Order(StringComparer.Ordinal)));

        // A version is often absent because it is pinned centrally, which is
        // correct; only differing explicit versions are a finding.
        var versions = references.Values
            .Where(version => version is { Length: > 0 })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (versions.Length > 1)
        {
            findings.Add(new Finding(
                "noelia.packages.version-skew",
                FindingSeverity.Problem,
                "project files",
                $"Noelia packages are pinned to {versions.Length} different versions "
                + $"({string.Join(", ", versions.Order(StringComparer.Ordinal))}). All of them "
                + "ship under one version, and mixing them is untested.",
                "Pin every Noelia package to the same version, centrally in "
                + "Directory.Packages.props."));
        }

        return references;
    }

    private static void Composition(
        string text,
        Dictionary<string, string?> references,
        List<Finding> findings,
        List<string> observations)
    {
        var engine = references.ContainsKey("Noelia.Infrastructure");
        var composes = text.Contains("AddNoelia(", StringComparison.Ordinal);
        var pipeline = text.Contains("UseNoelia(", StringComparison.Ordinal);

        if (engine && !composes)
        {
            findings.Add(new Finding(
                "noelia.composition.absent",
                FindingSeverity.Problem,
                "source",
                "Noelia.Infrastructure is referenced and no AddNoelia(...) call was found, so "
                + "nothing in the package is composed.",
                "Call AddNoelia(configuration, environment, serviceName, noelia => ...) once, "
                + "in the composition root."));
            return;
        }

        if (!composes)
        {
            return;
        }

        observations.Add("AddNoelia(...) is called.");

        if (!pipeline)
        {
            findings.Add(new Finding(
                "noelia.pipeline.absent",
                FindingSeverity.Problem,
                "source",
                "AddNoelia(...) composes the services and no UseNoelia(...) was found, so none "
                + "of the middleware — health checks, rate limiting, authentication, audit — "
                + "is in the request pipeline.",
                "Call app.UseNoelia(environment, serviceName) before mapping endpoints."));
        }

        var modules = Regex.Matches(text, @"\.Use([A-Z][A-Za-z0-9]*)\s*\(")
            .Select(match => match.Groups[1].Value)
            .Where(name => name is not ("Noelia" or "Defaults"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (modules.Length > 0)
        {
            observations.Add("Composed by literal call: " + string.Join(", ", modules));
        }

        if (text.Contains("UseDefaults()", StringComparison.Ordinal))
        {
            observations.Add("UseDefaults() is called.");
        }
    }

    /// <summary>
    /// The eager-registration trap, reported per file.
    /// </summary>
    /// <remarks>
    /// Per file rather than across the whole tree, because the pair has to be
    /// in the same composition root to mean anything — and a project that
    /// registers Redis in one service and composes it in another is a finding
    /// this would otherwise hide.
    /// </remarks>
    private static void EagerProviders(IReadOnlyList<string> sources, List<Finding> findings)
    {
        foreach (var source in sources)
        {
            var text = SafeRead(source);

            if (!text.Contains("AddNoelia(", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var (eager, composed) in ProviderPairs.All)
            {
                if (!text.Contains(eager + "(", StringComparison.Ordinal)
                    || text.Contains(composed + "(", StringComparison.Ordinal))
                {
                    continue;
                }

                findings.Add(new Finding(
                    "noelia.provider.eager",
                    FindingSeverity.Problem,
                    Path.GetFileName(source),
                    $"{eager}(...) is called beside AddNoelia(...). The built-in modules "
                    + "register their own provider while the composition is being built, so "
                    + "this registration is overwritten and nothing reports that it was.",
                    $"Compose it instead: noelia.{composed}(...) inside the AddNoelia "
                    + "callback."));
            }
        }
    }

    private static void Egress(
        string text,
        Dictionary<string, string?> references,
        List<Finding> findings,
        List<string> observations)
    {
        if (!references.ContainsKey("Noelia.Infrastructure"))
        {
            return;
        }

        var declares = text.Contains("AddSovereignPlatform", StringComparison.Ordinal)
            || text.Contains("AddNoeliaEgressPolicy", StringComparison.Ordinal);

        if (declares)
        {
            observations.Add("An egress policy is declared.");
            return;
        }

        findings.Add(new Finding(
            "noelia.egress.undeclared",
            FindingSeverity.Warning,
            "source",
            "No egress policy was found, so every outbound call is permitted and the "
            + "destination register lists what configuration mentions rather than what is "
            + "reachable.",
            "Declare the hosts this service may reach with AddSovereignPlatform(sovereign => "
            + "sovereign.Allow(...)), so that an undeclared call fails instead of succeeding "
            + "unnoticed."));
    }

    private static void Configuration(
        IReadOnlyList<string> settings,
        List<Finding> findings,
        List<string> observations)
    {
        foreach (var file in settings)
        {
            JsonNode? root;

            try
            {
                root = JsonNode.Parse(
                    SafeRead(file),
                    documentOptions: new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    });
            }
            catch (JsonException)
            {
                findings.Add(new Finding(
                    "noelia.configuration.unreadable",
                    FindingSeverity.Warning,
                    Path.GetFileName(file),
                    "The configuration file is not valid JSON, so it was not read.",
                    "Fix the JSON, or exclude the file from the scan."));
                continue;
            }

            if (root is null)
            {
                continue;
            }

            Walk(root, string.Empty, Path.GetFileName(file), findings, observations);
        }
    }

    private static void Walk(
        JsonNode node,
        string path,
        string file,
        List<Finding> findings,
        List<string> observations)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    if (child is not null)
                    {
                        Walk(child, path.Length == 0 ? key : $"{path}:{key}", file,
                            findings, observations);
                    }
                }

                break;

            case JsonArray array:
                foreach (var child in array)
                {
                    if (child is not null)
                    {
                        Walk(child, path, file, findings, observations);
                    }
                }

                break;

            case JsonValue value:
                Leaf(value, path, file, findings, observations);
                break;
        }
    }

    private static void Leaf(
        JsonValue value,
        string path,
        string file,
        List<Finding> findings,
        List<string> observations)
    {
        if (value.GetValueKind() is not JsonValueKind.String)
        {
            return;
        }

        var text = value.GetValue<string>();

        if (text.Length == 0)
        {
            return;
        }

        var leaf = path.Split(':').Last();

        // A placeholder is the intended state of a committed file, not a leak.
        var placeholder = text.StartsWith('$')
            || text.StartsWith("${", StringComparison.Ordinal)
            || text.Contains("CHANGE", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<", StringComparison.Ordinal);

        if (!placeholder
            && SecretishKeys.Any(word => leaf.Contains(word, StringComparison.OrdinalIgnoreCase)))
        {
            findings.Add(new Finding(
                "noelia.configuration.inline-secret",
                FindingSeverity.Problem,
                $"{file} · {path}",
                "A key whose name says it holds a credential has a literal value in a file "
                + "that is normally committed. The value itself is not reproduced here.",
                "Move it to an environment variable, a Docker secret read with KeyPerFile, or "
                + "the registered secret provider."));
        }

        // Destinations, so the reader can see what this configuration reaches
        // before the service has ever run.
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && !uri.IsLoopback)
        {
            observations.Add($"{file} · {path} points at {uri.Host}");
        }
    }
}
