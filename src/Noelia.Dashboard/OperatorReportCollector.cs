using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Noelia.Abstractions.Audit;
using Noelia.Abstractions.Caching;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Operator;
using Noelia.Abstractions.Security.Checks;
using Noelia.Abstractions.Security.Sessions;
using Noelia.Abstractions.Sovereignty;

namespace Noelia.Dashboard;

/// <summary>
/// Reads the registered services once and answers with data.
/// </summary>
/// <remarks>
/// <para>Everything the operator view knows passes through here. The page
/// renders the result and so does the JSON endpoint, which is what keeps the
/// two from disagreeing: they are one reading, drawn twice.</para>
///
/// <para>Every section is defensive in the same way and for the same reason. A
/// provider that throws while being inspected must not take the whole report
/// with it — the sections an operator can still be told about are exactly the
/// ones they need when something is already wrong. A failure becomes
/// <see cref="OperatorSectionState.Faulted"/> on that section and nothing
/// more.</para>
/// </remarks>
internal static class OperatorReportCollector
{
    internal static async Task<OperatorReport> CollectAsync(
        HttpContext context,
        NoeliaDashboardOptions options,
        TimeProvider clock)
    {
        var services = context.RequestServices;
        var composition = services.GetRequiredService<NoeliaComposition>();
        var cancellationToken = context.RequestAborted;

        return new OperatorReport
        {
            GeneratedAt = clock.GetUtcNow(),
            Service = options.ServiceName,
            Instance = options.InstanceName,
            Environment = options.EnvironmentName,
            Fleet = options.Fleet,
            Composition = Composition(composition, services.GetService<IServiceProviderIsService>()),
            Configuration = Configuration(composition, options),
            SecurityChecks = SecurityChecks(composition, services.GetService<ISecurityCheckReport>()),
            Sovereignty = Sovereignty(services.GetService<ISovereigntyReport>()),
            Audit = await Audit(
                services.GetService<IAuditTrailService>(),
                services.GetService<ISovereignAuditSink>() is IReadableSovereignAuditSink,
                cancellationToken)
                .ConfigureAwait(false),
            Sessions = await Sessions(services.GetService<ITokenSessionService>(), cancellationToken)
                .ConfigureAwait(false),
            RateLimits = await RateLimits(
                services.GetService<IDistributedRateLimitStore>(), cancellationToken)
                .ConfigureAwait(false),
            Health = await Health(services.GetService<HealthCheckService>(), cancellationToken)
                .ConfigureAwait(false)
        };
    }

    private static CompositionView Composition(
        NoeliaComposition composition,
        IServiceProviderIsService? services)
    {
        var modules = new List<ModuleView>();

        foreach (var module in composition.Included)
        {
            modules.Add(new ModuleView(module.Name, true, "selected"));
        }

        foreach (var (module, reason) in composition.Excluded
            .OrderBy(pair => pair.Key.Name, StringComparer.Ordinal))
        {
            modules.Add(new ModuleView(module.Name, false, reason));
        }

        var contracts = new List<ContractView>();

        foreach (var module in composition.Included)
        {
            if (!composition.Contracts.TryGetValue(module, out var contract))
            {
                continue;
            }

            var requirements = contract.Requirements
                .Select(requirement => new RequirementView(
                    requirement.ServiceType.Name,
                    services?.IsService(requirement.ServiceType) == true,
                    [.. requirement.Providers.Select(provider => provider.ToString())]))
                .ToArray();

            var provisions = contract.Provisions
                .Select(provision => new ProvisionView(
                    provision.ServiceType.Name,
                    services?.IsService(provision.ServiceType) == true,
                    [.. composition.Contracts.Values
                        .Where(other => other.Module != module)
                        .Where(other => other.Requirements.Any(
                            requirement => requirement.ServiceType == provision.ServiceType))
                        .Select(other => other.Module.Name)
                        .Order(StringComparer.Ordinal)]))
                .ToArray();

            contracts.Add(new ContractView(
                module.Name, contract.RegistersNothingBecause, requirements, provisions));
        }

        return new CompositionView { Modules = modules, Contracts = contracts };
    }

    private static ConfigurationView Configuration(
        NoeliaComposition composition,
        NoeliaDashboardOptions options)
    {
        var requested = composition.Included.Select(module => module.Name)
            .Concat(options.ConfigurationSections)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var shapes = new List<ConfigurationShapeView>();

        foreach (var sectionName in requested)
        {
            var section = options.Configuration.GetSection(sectionName);

            // One past the cap, so "there are more" is a fact rather than a
            // guess drawn from hitting the limit exactly.
            var leaves = Leaves(section).Take(201).ToArray();

            if (leaves.Length == 0)
            {
                shapes.Add(new ConfigurationShapeView(sectionName, null, "no explicit value"));
                continue;
            }

            foreach (var leaf in leaves.Take(200))
            {
                shapes.Add(new ConfigurationShapeView(
                    sectionName, RelativeKey(section, leaf), ValueShape(leaf.Value)));
            }

            if (leaves.Length > 200)
            {
                shapes.Add(new ConfigurationShapeView(
                    sectionName, "…", "more than 200 keys; remaining shapes omitted"));
            }
        }

        return new ConfigurationView
        {
            Shapes = shapes,
            ProductionReason = options.ProductionReason
        };
    }

    private static SecurityCheckView SecurityChecks(
        NoeliaComposition composition,
        ISecurityCheckReport? report)
    {
        if (report is null)
        {
            return new SecurityCheckView
            {
                State = OperatorSectionState.Absent,
                Note = "No security-check report is registered."
            };
        }

        // Composition checks run whether or not their module was composed, so
        // they belong in the set even though no module named them.
        var active = composition.Included.ToHashSet();
        active.Add(NoeliaModule.Composition);

        var results = report.Latest
            .Where(result => active.Contains(result.Module))
            .Select(result => new SecurityCheckResultView(
                result.Id,
                result.Module.Name,
                result.Status.ToString(),
                result.Severity.ToString(),
                result.Summary,
                result.Remediation))
            .ToArray();

        return results.Length == 0
            ? new SecurityCheckView
            {
                State = OperatorSectionState.Unavailable,
                Note = "No completed check exists for an active module."
            }
            : new SecurityCheckView
            {
                State = OperatorSectionState.Present,
                Results = results
            };
    }

    private static SovereigntyView Sovereignty(ISovereigntyReport? report)
    {
        if (report is null)
        {
            return new SovereigntyView
            {
                State = OperatorSectionState.Absent,
                Note = "No sovereignty report is registered."
            };
        }

        SovereigntyAssessment assessment;
        try
        {
            assessment = report.Assess();
        }
        catch
        {
            return new SovereigntyView
            {
                State = OperatorSectionState.Faulted,
                Note = "The sovereignty report could not be read."
            };
        }

        return new SovereigntyView
        {
            State = OperatorSectionState.Present,
            EgressIsEnforced = assessment.EgressIsEnforced,
            DeclaredHosts = [.. assessment.DeclaredEgressHosts.Order(StringComparer.Ordinal)],
            Dependencies = [.. assessment.Dependencies.Select(dependency => new DependencyView(
                dependency.Name,
                dependency.Host,
                dependency.Jurisdiction.ToString(),
                dependency.Note))]
        };
    }

    private static async Task<AuditView> Audit(
        IAuditTrailService? audit,
        bool canBeVerified,
        CancellationToken cancellationToken)
    {
        if (audit is null)
        {
            return new AuditView
            {
                State = OperatorSectionState.Absent,
                Note = "No audit trail is registered; the dashboard access check reports this."
            };
        }

        AuditTrailInspection inspection;
        try
        {
            inspection = await audit.InspectAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new AuditView
            {
                State = OperatorSectionState.Faulted,
                Note = "The audit trail could not be inspected."
            };
        }

        if (!inspection.IsAvailable)
        {
            return new AuditView
            {
                State = OperatorSectionState.Unavailable,
                Note = "Access was recorded, but this audit provider exposes no read model."
            };
        }

        return new AuditView
        {
            State = OperatorSectionState.Present,
            IsChainValidAtWriteTime = inspection.IsChainValidAtWriteTime,
            VerifiesPersistedSink = inspection.VerifiesPersistedSink,
            Length = inspection.Length,
            CanBeVerified = canBeVerified,
            Latest = [.. inspection.Latest.Select(entry => new AuditEntryView(
                entry.Timestamp, entry.ActorId, entry.Capacity, entry.Action, entry.Resource))]
        };
    }

    private static async Task<SessionView> Sessions(
        ITokenSessionService? sessions,
        CancellationToken cancellationToken)
    {
        if (sessions is null)
        {
            return new SessionView
            {
                State = OperatorSectionState.Absent,
                Note = "No token-session service is registered."
            };
        }

        TokenSessionInspection inspection;
        try
        {
            inspection = await sessions.InspectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new SessionView
            {
                State = OperatorSectionState.Faulted,
                Note = "Sessions could not be inspected."
            };
        }

        if (!inspection.IsAvailable)
        {
            return new SessionView
            {
                State = OperatorSectionState.Unavailable,
                Note = "The session provider exposes no safe operator read model."
            };
        }

        return new SessionView
        {
            State = OperatorSectionState.Present,
            Count = inspection.Sessions.Count,
            Sessions = [.. inspection.Sessions.Select(session => new SessionEntryView(
                session.Subject,
                session.Session,
                session.StartedAt,
                session.LastUsedAt,
                session.ExpiresAt,
                session.ClientFingerprintShape))]
        };
    }

    private static async Task<RateLimitView> RateLimits(
        IDistributedRateLimitStore? store,
        CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return new RateLimitView
            {
                State = OperatorSectionState.Absent,
                Note = "No rate-limit store is registered."
            };
        }

        RateLimitInspection inspection;
        try
        {
            inspection = await store.InspectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new RateLimitView
            {
                State = OperatorSectionState.Faulted,
                Note = "Rate-limit counters could not be inspected."
            };
        }

        if (!inspection.IsAvailable)
        {
            return new RateLimitView
            {
                State = OperatorSectionState.Unavailable,
                Note = "The active store exposes no safe counter read model."
            };
        }

        return new RateLimitView
        {
            State = OperatorSectionState.Present,
            Counters = [.. inspection.Counters.Select(counter => new RateLimitCounterView(
                counter.KeyFingerprint,
                counter.CurrentCount,
                counter.Limit,
                counter.IsRejected,
                counter.ObservedAt))]
        };
    }

    private static async Task<HealthView> Health(
        HealthCheckService? health,
        CancellationToken cancellationToken)
    {
        if (health is null)
        {
            return new HealthView
            {
                State = OperatorSectionState.Absent,
                Note = "No health-check service is registered."
            };
        }

        HealthReport report;
        try
        {
            report = await health.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new HealthView
            {
                State = OperatorSectionState.Faulted,
                Note = "Health checks could not complete."
            };
        }

        return new HealthView
        {
            State = OperatorSectionState.Present,
            Overall = report.Status.ToString(),
            Entries = [.. report.Entries
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new HealthEntryView(
                    pair.Key,
                    pair.Value.Status.ToString(),
                    pair.Value.Duration.TotalMilliseconds,
                    [.. pair.Value.Tags]))]
        };
    }

    private static IEnumerable<IConfigurationSection> Leaves(IConfigurationSection section)
    {
        var children = section.GetChildren().ToArray();
        if (children.Length == 0)
        {
            if (section.Value is not null)
            {
                yield return section;
            }

            yield break;
        }

        foreach (var child in children)
        {
            foreach (var leaf in Leaves(child))
            {
                yield return leaf;
            }
        }
    }

    private static string RelativeKey(IConfigurationSection root, IConfigurationSection leaf) =>
        leaf.Path.Length > root.Path.Length + 1
            ? leaf.Path[(root.Path.Length + 1)..]
            : leaf.Key;

    private static string ValueShape(string? value) => value is null ? "missing" : "set";
}
