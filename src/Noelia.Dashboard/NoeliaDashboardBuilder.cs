using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noelia.Abstractions.Hosting;

namespace Noelia.Dashboard;

/// <summary>Configures the read-only operator dashboard.</summary>
public sealed class NoeliaDashboardBuilder
{
    private readonly List<string> _configurationSections = [];
    private Func<HttpContext, bool>? _visibility;
    private string _path = "/noelia";
    private string? _productionReason;

    /// <summary>Sets the path that serves the page. The default is <c>/noelia</c>.</summary>
    public NoeliaDashboardBuilder At(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (path[0] != '/'
            || path is "/"
            || path.Contains('?')
            || path.Contains('#')
            || path.Contains("//", StringComparison.Ordinal)
            || path.Skip(1).Any(character =>
                !char.IsLetterOrDigit(character)
                && character is not '-' and not '_' and not '/'))
        {
            throw new ArgumentException(
                "The dashboard path must be an absolute path segment such as '/noelia'.",
                nameof(path));
        }

        _path = path.TrimEnd('/');
        return this;
    }

    /// <summary>
    /// Supplies the operator authorization decision. Without this call every
    /// dashboard request deliberately falls through as 404.
    /// </summary>
    public NoeliaDashboardBuilder VisibleTo(Func<HttpContext, bool> visibility)
    {
        ArgumentNullException.ThrowIfNull(visibility);
        _visibility = visibility;
        return this;
    }

    /// <summary>
    /// Explicitly permits the page in Production and records why it is needed.
    /// An empty reason is rejected.
    /// </summary>
    /// <remarks>
    /// The reason is shown verbatim on the page and in the
    /// <c>noelia.dashboard.operator-access</c> check, so that whoever reads
    /// either can judge whether it still holds. Write it for that reader, and
    /// put nothing in it that should not be on a screen — it is a
    /// justification, not a place for a host name, a ticket body or a
    /// credential.
    /// </remarks>
    public NoeliaDashboardBuilder InProduction(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (reason.Length > 512)
        {
            throw new ArgumentException(
                "The production reason must not exceed 512 characters.",
                nameof(reason));
        }

        _productionReason = reason.Trim();
        return this;
    }

    /// <summary>
    /// Adds configuration sections whose keys and value shapes may be shown.
    /// Values themselves are never retained or rendered.
    /// </summary>
    public NoeliaDashboardBuilder InspectConfiguration(params string[] sectionPaths)
    {
        ArgumentNullException.ThrowIfNull(sectionPaths);

        foreach (var sectionPath in sectionPaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);

            if (sectionPath.Length > 256)
            {
                throw new ArgumentException(
                    "A configuration section path must not exceed 256 characters.",
                    nameof(sectionPaths));
            }

            if (!_configurationSections.Contains(sectionPath, StringComparer.OrdinalIgnoreCase))
            {
                if (_configurationSections.Count == 32)
                {
                    throw new InvalidOperationException(
                        "The dashboard accepts at most 32 explicit configuration sections.");
                }

                _configurationSections.Add(sectionPath);
            }
        }

        return this;
    }

    internal NoeliaDashboardOptions Build(
        IConfiguration configuration,
        IHostEnvironment environment,
        string serviceName)
    {
        if (environment.IsProduction() && string.IsNullOrWhiteSpace(_productionReason))
        {
            throw new InvalidOperationException(
                "Noelia.Dashboard is disabled in Production. Call InProduction(reason) "
                + "with the operational reason for exposing it there.");
        }

        return new NoeliaDashboardOptions(
            _path,
            _visibility,
            _productionReason,
            _configurationSections.ToArray(),
            configuration,
            serviceName,
            environment.EnvironmentName,
            Environment.MachineName);
    }
}

/// <summary>A marker proving that the dashboard module registered its surface.</summary>
public interface INoeliaDashboard;

/// <summary>Registers the dashboard as an external Noelia module.</summary>
public static class NoeliaDashboardExtensions
{
    /// <summary>Adds the dashboard to an existing <see cref="NoeliaBuilder"/> composition.</summary>
    public static NoeliaBuilder UseDashboard(
        this NoeliaBuilder noelia,
        Action<NoeliaDashboardBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(noelia);
        ArgumentNullException.ThrowIfNull(configure);

        var dashboard = new NoeliaDashboardBuilder();
        configure(dashboard);
        var options = dashboard.Build(noelia.Configuration, noelia.Environment, noelia.ServiceName);

        return noelia.Use(
            NoeliaModule.Dashboard,
            builder => DashboardRegistration.Add(builder.Services, options),
            contract => contract.Provides<INoeliaDashboard>(
                "Noelia.Dashboard", "UseDashboard(dashboard => ...)"));
    }

    /// <summary>
    /// Adds a dashboard-only composition for isolated hosts that intentionally
    /// do not reference <c>Noelia.Infrastructure</c>.
    /// </summary>
    /// <remarks>
    /// Applications already using <c>AddNoelia</c> should call
    /// <see cref="UseDashboard(NoeliaBuilder, Action{NoeliaDashboardBuilder})"/>
    /// inside that composition instead.
    /// </remarks>
    public static IServiceCollection AddNoeliaDashboard(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        string serviceName,
        Action<NoeliaDashboardBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(configure);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(NoeliaComposition)))
        {
            throw new InvalidOperationException(
                "A Noelia composition already exists. Add UseDashboard(...) to that composition.");
        }

        var noelia = new NoeliaBuilder(
            services, configuration, environment, serviceName, [], []);

        noelia.UseDashboard(configure);
        noelia.Build();
        DashboardRegistration.AddStandaloneSecurityReport(services);

        return services;
    }
}
