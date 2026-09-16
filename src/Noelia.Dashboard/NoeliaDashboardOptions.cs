using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Noelia.Dashboard;

internal sealed record NoeliaDashboardOptions(
    string Path,
    Func<HttpContext, bool>? Visibility,
    string? ProductionReason,
    IReadOnlyList<string> ConfigurationSections,
    IConfiguration Configuration,
    string ServiceName,
    string EnvironmentName,
    string InstanceName,
    string? Fleet);

internal sealed class NoeliaDashboardMarker : INoeliaDashboard;
