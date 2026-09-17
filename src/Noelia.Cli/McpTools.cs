using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Noelia.Cli;

/// <summary>
/// What an assistant may ask this tool about a Noelia project.
/// </summary>
/// <remarks>
/// <para><strong>Every tool here reads.</strong> None of them writes a file,
/// installs a package or changes a configuration. An agent that can propose a
/// change and a person who applies it is a good arrangement; an agent that
/// edits a composition root because a model produced a plausible next step is
/// not, and the way to be sure is to not ship the capability.</para>
///
/// <para><c>init</c> is deliberately absent for the same reason. It is a
/// writing command, and the CLI is where a person runs it, having read what it
/// intends to do.</para>
/// </remarks>
[McpServerToolType]
internal sealed class McpTools
{
    // Sealed with a private constructor rather than static: the server registers
    // tool types generically, and a static class cannot be a type argument. The
    // members stay static, so nothing is ever instantiated.
    private McpTools()
    {
    }

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [McpServerTool(Name = "noelia_analyze_project")]
    [Description("""
        Reads a directory and reports how its Noelia composition is wired: which packages are
        referenced, whether AddNoelia and UseNoelia are called, whether a provider is registered
        eagerly instead of composed, whether an egress policy is declared, and whether a
        credential is sitting in a committed configuration file. Static: it sees literal calls
        and configuration files only, never anything decided at runtime.
        """)]
    public static string AnalyzeProject(
        [Description("Absolute path to the project or solution directory.")] string path)
    {
        if (!Directory.Exists(path))
        {
            return $"No directory at {path}.";
        }

        return JsonSerializer.Serialize(ProjectScan.Run(path), Json);
    }

    [McpServerTool(Name = "noelia_analyze_service")]
    [Description("""
        Reads the report a running Noelia service publishes at its dashboard path, and reports
        what the composition actually did: the checks that ran and how they came out, unmet
        requirements, outbound destinations and their jurisdictions, recognised model endpoints,
        and whether the audit chain verifies. This is authoritative where the static reading is
        an approximation.
        """)]
    public static async Task<string> AnalyzeServiceAsync(
        [Description("The dashboard root, such as http://localhost:5000/noelia.")] string url,
        [Description("The operator header value, if the dashboard is gated on one.")]
        string? operatorSecret = null,
        CancellationToken cancellationToken = default)
    {
        var result = await LiveScan.RunAsync(url, operatorSecret, cancellationToken)
            .ConfigureAwait(false);

        return JsonSerializer.Serialize(result, Json);
    }

    [McpServerTool(Name = "noelia_provider_pairs")]
    [Description("""
        Lists the provider registrations that must be composed rather than called beside
        AddNoelia, and the composing call for each. Calling the eager form registers a provider
        that the built-in modules then overwrite while the composition is being built, so the
        service is present and the provider is not effective, and nothing reports it.
        """)]
    public static string ProviderPairsTool() =>
        JsonSerializer.Serialize(
            ProviderPairs.All.Select(pair => new
            {
                eager = pair.Eager + "(...)",
                composed = "noelia." + pair.Composed + "(...)",
                why = "The built-in modules register their own provider during Build(), "
                      + "so the eager call is overwritten without an error."
            }),
            Json);

    [McpServerTool(Name = "noelia_composition_snippet")]
    [Description("""
        Returns a composition root for a new service: the AddNoelia call, the dashboard, an
        egress policy and the UseNoelia pipeline call, with the service name filled in.
        """)]
    public static string CompositionSnippet(
        [Description("The service name, as it appears in reports and audit entries.")]
        string serviceName)
    {
        using var writer = new StringWriter();

        // The same text `noelia init` prints, produced by the same code. Two
        // snippets that drift apart is how an assistant ends up recommending a
        // shape the tool no longer writes.
        InitCommand.Run(
            Path.GetTempPath(),
            serviceName,
            dryRun: true,
            output: writer);

        var text = writer.ToString();
        var marker = "var builder = WebApplication.CreateBuilder(args);";
        var start = text.IndexOf(marker, StringComparison.Ordinal);

        return start >= 0 ? text[start..] : text;
    }
}
