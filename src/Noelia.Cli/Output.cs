using System.Text;
using System.Text.Json;

namespace Noelia.Cli;

/// <summary>
/// Writes an analysis for a person or for a program.
/// </summary>
/// <remarks>
/// Two renderings of one result, never two readings. Whatever the table shows
/// is in the JSON and the other way round, because a CI gate that disagrees
/// with the terminal is a gate nobody believes twice.
/// </remarks>
internal static class Output
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static void Write(AnalysisResult result, string format, TextWriter output)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine(JsonSerializer.Serialize(result, Json));
            return;
        }

        Table(result, output);
    }

    private static void Table(AnalysisResult result, TextWriter output)
    {
        output.WriteLine($"Source    {result.Source}");
        output.WriteLine($"Reading   {result.Kind}");

        if (result.Kind == "static")
        {
            output.WriteLine("          Literal calls and configuration files only. Anything "
                             + "decided at runtime is invisible;");
            output.WriteLine("          `noelia analyze --url <dashboard>` reads the "
                             + "composition that actually happened.");
        }

        output.WriteLine();

        if (result.Observations.Count > 0)
        {
            output.WriteLine("What it read");

            foreach (var observation in result.Observations)
            {
                output.WriteLine("  · " + observation);
            }

            output.WriteLine();
        }

        if (result.Findings.Count == 0)
        {
            output.WriteLine("No findings.");
            return;
        }

        output.WriteLine($"Findings ({result.Findings.Count})");
        output.WriteLine();

        foreach (var finding in result.Findings)
        {
            var mark = finding.Severity switch
            {
                FindingSeverity.Problem => "!!",
                FindingSeverity.Warning => " !",
                _ => "  "
            };

            output.WriteLine($"{mark} {finding.Id}");
            output.WriteLine($"   in  {finding.Where}");
            Wrapped(output, "       ", finding.What);

            if (finding.Do.Length > 0)
            {
                Wrapped(output, "   →   ", finding.Do);
            }

            output.WriteLine();
        }

        var problems = result.Findings.Count(f => f.Severity is FindingSeverity.Problem);
        var warnings = result.Findings.Count(f => f.Severity is FindingSeverity.Warning);

        output.WriteLine($"{problems} problem(s), {warnings} warning(s). "
                         + $"Exit code {result.ExitCode}.");
    }

    /// <summary>
    /// Wraps at the terminal width, and never mid-word.
    /// </summary>
    /// <remarks>
    /// A fixed 80 where the console has no width of its own — a redirected
    /// stream reports zero, and wrapping to zero produces one word per line.
    /// </remarks>
    private static void Wrapped(TextWriter output, string prefix, string text)
    {
        var width = 100;

        try
        {
            if (Console.WindowWidth > 40)
            {
                width = Math.Min(Console.WindowWidth - 1, 110);
            }
        }
        catch (IOException)
        {
            // No console attached. The default stands.
        }

        var line = new StringBuilder(prefix);
        var first = true;

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!first && line.Length + word.Length + 1 > width)
            {
                output.WriteLine(line.ToString());
                line.Clear().Append(new string(' ', prefix.Length));
                first = true;
            }

            if (!first)
            {
                line.Append(' ');
            }

            line.Append(word);
            first = false;
        }

        if (line.Length > prefix.Length)
        {
            output.WriteLine(line.ToString());
        }
    }
}
