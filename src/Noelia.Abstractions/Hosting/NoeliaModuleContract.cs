namespace Noelia.Abstractions.Hosting;

/// <summary>One package and call that can satisfy a module requirement.</summary>
/// <param name="PackageId">The exact package to reference.</param>
/// <param name="Registration">The exact registration call to make.</param>
public sealed record NoeliaProviderHint(string PackageId, string Registration)
{
    /// <summary>A readable instruction for startup diagnostics.</summary>
    public override string ToString() => $"{PackageId} → {Registration}";
}

/// <summary>A service an active module needs from another module or provider.</summary>
/// <param name="ServiceType">The required service contract.</param>
/// <param name="Providers">Known packages and calls that provide it.</param>
public sealed record NoeliaServiceRequirement(
    Type ServiceType,
    IReadOnlyList<NoeliaProviderHint> Providers);

/// <summary>A service an active module promises to register.</summary>
/// <param name="ServiceType">The provided service contract.</param>
/// <param name="PackageId">The package making the promise.</param>
/// <param name="Registration">The public call that activates it.</param>
public sealed record NoeliaServiceProvision(
    Type ServiceType,
    string PackageId,
    string Registration);

/// <summary>What one module requires from others and provides to them.</summary>
/// <param name="Module">The module this describes.</param>
/// <param name="Requirements">What it cannot operate without.</param>
/// <param name="Provisions">What it registers for others.</param>
/// <param name="RegistersNothingBecause">
/// Why this module registers nothing, when that is the point of it. A pipeline
/// step has no service to offer, and without this a deliberate emptiness and a
/// contract nobody wrote look identical to anyone reading the dashboard.
/// </param>
public sealed record NoeliaModuleContract(
    NoeliaModule Module,
    IReadOnlyList<NoeliaServiceRequirement> Requirements,
    IReadOnlyList<NoeliaServiceProvision> Provisions,
    string? RegistersNothingBecause = null);

/// <summary>Builds a module contract beside the registration it describes.</summary>
public sealed class NoeliaModuleContractBuilder
{
    private readonly NoeliaModule _module;
    private readonly List<NoeliaServiceRequirement> _requirements = [];
    private readonly List<NoeliaServiceProvision> _provisions = [];
    private string? _registersNothingBecause;

    /// <summary>Starts the contract for <paramref name="module"/>.</summary>
    public NoeliaModuleContractBuilder(NoeliaModule module) => _module = module;

    /// <summary>Declares a service the module cannot operate without.</summary>
    public NoeliaModuleContractBuilder Requires<TService>(params NoeliaProviderHint[] providers)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(providers);

        if (providers.Length == 0)
        {
            throw new ArgumentException(
                $"Requirement {typeof(TService).Name} needs at least one package and registration hint.",
                nameof(providers));
        }

        _requirements.Add(new NoeliaServiceRequirement(typeof(TService), providers));
        return this;
    }

    /// <summary>Declares a service the module registers when it is active.</summary>
    public NoeliaModuleContractBuilder Provides<TService>(string packageId, string registration)
        where TService : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration);

        if (_registersNothingBecause is not null)
        {
            throw new InvalidOperationException(
                $"{_module} declared that it registers nothing and then declared "
                + $"{typeof(TService).Name}.");
        }

        _provisions.Add(new NoeliaServiceProvision(typeof(TService), packageId, registration));
        return this;
    }

    /// <summary>
    /// Declares that this module registers nothing, and why.
    /// </summary>
    /// <remarks>
    /// For a module that is a pipeline step and nothing else. It is a claim
    /// like any other and is checked like one: calling this and then
    /// <see cref="Provides{TService}"/> is a contradiction, and the second call
    /// throws rather than quietly winning.
    /// </remarks>
    /// <param name="reason">What the module does instead, in one sentence.</param>
    public NoeliaModuleContractBuilder RegistersNothing(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (_provisions.Count > 0)
        {
            throw new InvalidOperationException(
                $"{_module} declared a provision and then declared that it registers nothing.");
        }

        _registersNothingBecause = reason;
        return this;
    }

    /// <summary>Produces the immutable contract.</summary>
    public NoeliaModuleContract Build() =>
        new(_module, _requirements.ToArray(), _provisions.ToArray(), _registersNothingBecause);
}

/// <summary>A module's executable registration and its public contract.</summary>
public sealed record NoeliaModuleRegistration(
    NoeliaModule Module,
    Action<NoeliaBuilder> Register,
    NoeliaModuleContract Contract);
