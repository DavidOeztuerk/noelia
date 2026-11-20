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

/// <summary>
/// Whether ASP.NET has copied the mapped routes into the container yet. One instance per
/// container, never static: two hosts in one process (every test host, a service that
/// builds a second one) must not tell each other the routes exist.
/// </summary>
internal sealed class RoutesBuiltState
{
    private volatile bool _built;

    public bool Built => _built;

    internal void MarkBuilt() => _built = true;
}

/// <summary>
/// Runs the checks twice: in <c>StartAsync</c>, and again in <c>StartedAsync</c> once every
/// hosted service has started (ADR-0004).
/// </summary>
/// <remarks>
/// A check that reads the routes cannot see them in <c>StartAsync</c>:
/// <c>GenericWebHostService</c> builds the pipeline and fills the container's
/// <c>EndpointDataSource</c> in its own <c>StartAsync</c>, which on a
/// <c>WebApplicationBuilder</c> is the last service to run. <c>StartedAsync</c> is awaited
/// before <c>StartAsync()</c> of the host returns, so the report is already the real one
/// when the process is reachable. <c>ApplicationStarted</c> would not give that: it is
/// synchronous, so an async body would be <c>async void</c> and an exception from it would
/// end the process.
/// </remarks>
internal sealed class SecurityCheckStartupService(
    ISecurityCheckRunner runner,
    RoutesBuiltState routes,
    ILogger<SecurityCheckStartupService> logger) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken) =>
        await runner.RunAsync(cancellationToken);

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        routes.MarkBuilt();
        try
        {
            await runner.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // An exception out of StartedAsync stops the host, and a service must not fail to
            // start because its self-check did. The report keeps the first, honest run
            // ("routes not built yet"). Only the type is logged, as everywhere in this
            // feature: the message of a provider's exception may carry a value.
            logger.LogError(
                "The second security check run after startup failed ({ExceptionType}); the report keeps the first run",
                exception.GetType().Name);
        }
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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
