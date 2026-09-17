using System.Reflection;
using Noelia.Abstractions.Hosting;
using Noelia.Cli;

namespace Noelia.Infrastructure.Tests.Architecture;

/// <summary>
/// Keeps the CLI's idea of the eager-registration trap tied to the shipped API.
/// </summary>
/// <remarks>
/// <para><c>noelia analyze</c> reports a provider registered beside
/// <c>AddNoelia</c> instead of inside it, and it does so from a hardcoded list
/// of pairs. A hardcoded list in a separate package is a list that goes stale:
/// a provider added to <c>Noelia.Redis</c> next year would simply never be
/// checked, and the analysis would look as complete as it does today.</para>
///
/// <para>So the list is checked against reflection over the assemblies
/// themselves. A new <c>AddX</c>/<c>UseX</c> pair fails this test until it is
/// added to the CLI, which is the only moment anyone is thinking about it.</para>
/// </remarks>
[Trait("Category", "Unit")]
public class CliProviderPairTests
{
    private static readonly Assembly[] Shipped =
    [
        typeof(NoeliaBuilder).Assembly,
        typeof(Noelia.Infrastructure.Sovereignty.SovereigntyReport).Assembly,
        typeof(Noelia.Redis.RedisNoeliaModule).Assembly,
        typeof(Noelia.InMemory.Security.InMemorySecurityRegistration).Assembly,
        typeof(Noelia.Passwords.BCrypt.BCryptPasswordHasher).Assembly,
        typeof(Noelia.Passwords.Argon2.Argon2PasswordHasher).Assembly
    ];

    [Fact]
    public void Every_composable_provider_is_known_to_the_analyzer()
    {
        var composing = Extensions("Use", typeof(NoeliaBuilder));
        var eager = Extensions("Add", typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection));

        // A pair exists when both halves do. UseJwt has no AddJwt, AddCQRS has
        // no UseCQRS — neither is a provider that can be registered twice, and
        // reporting them would train a reader to ignore the finding.
        var pairs = composing
            .Where(use => eager.Contains("Add" + use[3..], StringComparer.Ordinal))
            .Select(use => ("Add" + use[3..], use))
            .ToArray();

        var known = ProviderPairs.All
            .Select(pair => (pair.Eager, pair.Composed))
            .ToHashSet();

        // Not vacuous: if reflection found nothing, OnlyContain would pass on an
        // empty set and this test would guard a list nobody was checking.
        pairs.Should().HaveCountGreaterThan(5,
            "the shipped API has several such pairs, and a reflection query that found none "
            + "would make the assertion below pass without checking anything");

        pairs.Should().OnlyContain(pair => known.Contains(pair),
            "every Add/Use pair in the shipped API is a way to register a provider that the "
            + "built-in modules then overwrite, and `noelia analyze` can only report the ones "
            + "it has been told about");
    }

    [Fact]
    public void The_analyzer_names_no_pair_that_does_not_exist()
    {
        var methods = Shipped
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsSealed && type.IsAbstract)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (eager, composed) in ProviderPairs.All)
        {
            methods.Should().Contain(eager,
                $"the analyzer reports {eager}(...) as a real call somebody could write");
            methods.Should().Contain(composed,
                $"the analyzer tells a reader to use {composed}(...) instead");
        }
    }

    private static HashSet<string> Extensions(string prefix, Type firstParameter) =>
        Shipped
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsSealed && type.IsAbstract && type.IsPublic)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(method => method.Name.StartsWith(prefix, StringComparison.Ordinal)
                             && method.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)
                             && method.GetParameters().FirstOrDefault()?.ParameterType == firstParameter)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
}
