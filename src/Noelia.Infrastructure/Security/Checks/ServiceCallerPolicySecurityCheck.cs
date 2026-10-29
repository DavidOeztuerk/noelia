using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security.Checks;

namespace Noelia.Infrastructure.Security.Checks;

internal sealed class ServiceCallerPolicySecurityCheck(
    IServiceProvider services,
    NoeliaComposition composition) : SecurityCheckBase
{
    public override string Id => "noelia.m2m.caller-policy";
    public override NoeliaModule Module => NoeliaModule.ServiceCallers;
    public override SecurityCheckCategory Category => SecurityCheckCategory.Composition;
    public override SecurityCheckSeverity Severity => SecurityCheckSeverity.High;
    public override string Remediation => "";

    public override Task<SecurityCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        _ = (services, composition);
        throw new NotImplementedException();
    }
}
