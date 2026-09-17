using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security.Checks;

namespace Noelia.Infrastructure.Security.Checks;

internal sealed class SecurityCheckReport : ISecurityCheckReport
{
    private readonly object _gate = new();
    private IReadOnlyList<SecurityCheckResult> _latest = Array.Empty<SecurityCheckResult>();

    public IReadOnlyList<SecurityCheckResult> Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    internal void Replace(IReadOnlyList<SecurityCheckResult> results)
    {
        lock (_gate)
        {
            _latest = Array.AsReadOnly(results.ToArray());
        }
    }
}

internal sealed partial class SecurityCheckRunner(
    IEnumerable<ISecurityCheck> checks,
    NoeliaComposition composition,
    IOptions<SecurityCheckOptions> options,
    SecurityCheckReport report,
    ILogger<SecurityCheckRunner> logger) : ISecurityCheckRunner
{
    private readonly SemaphoreSlim _runGate = new(1, 1);

    [GeneratedRegex("^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex StableIdPattern();

    public async Task<IReadOnlyList<SecurityCheckResult>> RunAsync(
        CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken);
        try
        {
            var active = composition.Included.ToHashSet();
            var selected = checks
                .Where(check => check.Category == SecurityCheckCategory.Composition
                                || active.Contains(check.Module))
                .OrderBy(check => check.Id, StringComparer.Ordinal)
                .ToArray();

            EnsureUniqueStableIds(selected);

            var results = new List<SecurityCheckResult>(selected.Length);
            foreach (var check in selected)
            {
                results.Add(await RunOneAsync(check, options.Value.Timeout, cancellationToken));
            }

            var completed = results.AsReadOnly();
            report.Replace(completed);
            return completed;
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task<SecurityCheckResult> RunOneAsync(
        ISecurityCheck check,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("SecurityCheckOptions.Timeout must be positive.");
        }

        SecurityCheckResult result;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            result = await check.RunAsync(timeoutSource.Token).WaitAsync(timeoutSource.Token);
            EnsureMetadataMatches(check, result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = Failure(check, "The check exceeded its execution time limit.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Never copy an exception or its message: providers often put an
            // endpoint, connection string, key name or token in one.
            result = Failure(check, "The check could not complete safely.");
        }

        logger.Log(
            result.Status == SecurityCheckStatus.Fail ? LogLevel.Error : LogLevel.Information,
            "Security check {SecurityCheckId} for module {Module} finished {Status} ({Severity})",
            result.Id,
            result.Module,
            result.Status,
            result.Severity);

        return result;
    }

    private static SecurityCheckResult Failure(ISecurityCheck check, string summary) => new(
        check.Id,
        check.Module,
        check.Category,
        SecurityCheckStatus.Fail,
        check.Severity,
        summary,
        check.Remediation)
    {
        // Kept even here. A check that failed to run is still evidence about the
        // article it was written for — evidence that nothing is known — and
        // dropping the citation would quietly remove the obligation from the
        // mapping instead of showing it unanswered.
        References = check.References
    };

    private static void EnsureUniqueStableIds(IReadOnlyList<ISecurityCheck> checks)
    {
        var invalid = checks.FirstOrDefault(check => !StableIdPattern().IsMatch(check.Id));
        if (invalid is not null)
        {
            throw new InvalidOperationException(
                "A security check id is not a stable lowercase dotted id.");
        }

        var duplicate = checks.GroupBy(check => check.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException("A security check id is registered twice.");
        }
    }

    private static void EnsureMetadataMatches(ISecurityCheck check, SecurityCheckResult result)
    {
        if (result.Id != check.Id || result.Module != check.Module || result.Category != check.Category)
        {
            throw new InvalidOperationException(
                $"Security check '{check.Id}' returned metadata for a different check.");
        }
    }
}

internal sealed class SecurityCheckStartupService(ISecurityCheckRunner runner) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken) =>
        await runner.RunAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class SecurityCheckResultFactory
{
    internal static SecurityCheckResult Result(
        ISecurityCheck check,
        SecurityCheckStatus status,
        string summary,
        SecurityCheckSeverity? severity = null) => new(
            check.Id,
            check.Module,
            check.Category,
            status,
            severity ?? check.Severity,
            summary,
            check.Remediation)
        {
            References = check.References
        };
}
