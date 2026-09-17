using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Noelia.Cli;

/// <summary>
/// The entry point.
/// </summary>
/// <remarks>
/// <para>Argument parsing by hand. Three commands and six options do not
/// justify a parser library, and the dependency would be carried by everyone
/// who installs the tool for the sake of code that fits on one screen.</para>
///
/// <para>The split between reading and writing is the shape Terraform settled
/// on and it is the right one here: <c>analyze</c> never writes, <c>init</c>
/// only writes, and the MCP server exposes the reading half alone.</para>
/// </remarks>
internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        var command = args.FirstOrDefault();

        try
        {
            return command switch
            {
                "init" => Init(args),
                "analyze" => await AnalyzeAsync(args).ConfigureAwait(false),
                "mcp" => await McpAsync().ConfigureAwait(false),
                "--version" or "-v" => Version(),
                null or "--help" or "-h" or "help" => Help(0),
                _ => Unknown(command)
            };
        }
        catch (Exception exception)
        {
            // Exit 2 for "the tool failed", distinct from exit 1 for "the tool
            // worked and found something". A pipeline that treats them alike
            // cannot tell a broken scanner from a failing project.
            Console.Error.WriteLine("noelia: " + exception.Message);
            return 2;
        }
    }

    private static int Init(string[] args)
    {
        var directory = Positional(args) ?? Directory.GetCurrentDirectory();

        return InitCommand.Run(
            Path.GetFullPath(directory),
            Option(args, "--name") ?? new DirectoryInfo(Path.GetFullPath(directory)).Name,
            Flag(args, "--dry-run"),
            Console.Out);
    }

    private static async Task<int> AnalyzeAsync(string[] args)
    {
        var format = Option(args, "--format") ?? "table";
        var url = Option(args, "--url");

        var result = url is { Length: > 0 }
            ? await LiveScan.RunAsync(url, Option(args, "--operator-secret"), CancellationToken.None)
                .ConfigureAwait(false)
            : ProjectScan.Run(Path.GetFullPath(Positional(args) ?? Directory.GetCurrentDirectory()));

        Output.Write(result, format, Console.Out);

        return result.ExitCode;
    }

    /// <summary>
    /// Serves the reading half over stdio.
    /// </summary>
    /// <remarks>
    /// Logging goes to stderr and must stay there: stdout carries the protocol,
    /// and one stray line on it desynchronises the session in a way that looks
    /// like the client is broken.
    /// </remarks>
    private static async Task<int> McpAsync()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        // Warnings and worse. A client shows the server's stderr to the person
        // using it, and a per-request line for a tool that answers in
        // milliseconds turns that panel into something nobody reads — which is
        // where the one line that mattered would have been.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<McpTools>();

        await builder.Build().RunAsync().ConfigureAwait(false);

        return 0;
    }

    private static int Version()
    {
        Console.Out.WriteLine(typeof(Program).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "unknown");

        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"noelia: unknown command '{command}'.");
        return Help(2);
    }

    private static int Help(int code)
    {
        var output = code == 0 ? Console.Out : Console.Error;

        output.WriteLine("""
            noelia — the Noelia command line

              noelia init [directory] [--name <service>] [--dry-run]
                  Adds the Noelia packages, writes the configuration sections they read, and
                  prints the composition root. Never edits source, never overwrites a value.

              noelia analyze [directory] [--format table|json]
              noelia analyze --url <dashboard> [--operator-secret <value>] [--format table|json]
                  Reads and reports. Never writes. A directory is read statically — literal
                  calls and configuration files only. A URL reads the report a running service
                  publishes, which is what the composition actually did.

                  Exit 0 clean, 1 findings, 2 the tool itself failed.

              noelia mcp
                  Serves the reading half to an MCP client over stdio.

              noelia --version
            """);

        return code;
    }

    private static string? Positional(string[] args) =>
        args.Skip(1).FirstOrDefault(argument => !argument.StartsWith('-'));

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);

        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool Flag(string[] args, string name) => args.Contains(name, StringComparer.Ordinal);
}
