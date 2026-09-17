using System.Collections.ObjectModel;
using Microsoft.Extensions.Hosting;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security.Checks;

namespace Noelia.Dashboard;

internal sealed class StandaloneDashboardSecurityReport(
    IEnumerable<ISecurityCheck> checks,
    NoeliaComposition composition) : ISecurityCheckReport, IHostedService
{
    private IReadOnlyList<SecurityCheckResult> _latest = Array.Empty<SecurityCheckResult>();

    public IReadOnlyList<SecurityCheckResult> Latest => _latest;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var active = composition.Included.ToHashSet();
        var results = new List<SecurityCheckResult>();

        // The same rule SecurityCheckRunner applies, and it has to stay the same
        // rule. A Composition-category check runs whether or not its module was
        // composed — that is what the category means — and this class selected
        // only by module, so a consumer who added one to a dashboard-only host
        // got a check that was registered, never ran, and reported nothing to
        // say it had not.
        foreach (var check in checks.Where(check =>
                     check.Category == SecurityCheckCategory.Composition
                     || active.Contains(check.Module)))
        {
            try
            {
                results.Add(await check.RunAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                results.Add(new SecurityCheckResult(
                    check.Id,
                    check.Module,
                    check.Category,
                    SecurityCheckStatus.Fail,
                    check.Severity,
                    "The check could not complete.",
                    check.Remediation)
                {
                    References = check.References
                });
            }
        }

        _latest = new ReadOnlyCollection<SecurityCheckResult>(results);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
