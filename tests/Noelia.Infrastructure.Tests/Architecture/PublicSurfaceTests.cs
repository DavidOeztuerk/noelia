using System.Reflection;
using System.Xml.Linq;
using Noelia.Abstractions.Audit;
using Noelia.Abstractions.Operator;
using Noelia.Abstractions.Sovereignty;
using Noelia.Infrastructure.Audit;

namespace Noelia.Infrastructure.Tests.Architecture;

/// <summary>
/// What a consumer can name, and what they are told about it.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PublicSurfaceTests
{
    /// <summary>
    /// The port is the public contract for <c>completeness: Complete</c>. The
    /// signed-file implementation was wired by nothing and had no producer
    /// workflow, so it is not offered as if it were a supported way to get there.
    /// </summary>
    [Fact]
    public void Only_the_checkpoint_port_is_public_not_its_unwired_implementation()
    {
        typeof(ITrustedAuditCheckpointSource).IsPublic.Should().BeTrue();
        typeof(TrustedAuditCheckpoint).IsPublic.Should().BeTrue();

        var infrastructure = typeof(AuditChainVerifier).Assembly;
        var implementation = new[] { "SignedFileAuditCheckpointSource", "AuditCheckpointSignature", "SignedAuditCheckpoint" }
            .Select(name => infrastructure.GetType($"Noelia.Infrastructure.Audit.{name}"))
            .ToArray();

        implementation.Should().OnlyContain(type => type != null,
            "a renamed type would make the assertion below pass without checking anything");
        implementation.Should().OnlyContain(type => !type!.IsPublic && !type.IsNestedPublic);
    }

    /// <summary>
    /// Compiler warning CS1591 is suppressed for the solution, so a public member
    /// added without documentation builds. These are the ones found missing in
    /// 7.0.0; the generated XML file is the ground truth a consumer's IDE reads.
    /// </summary>
    [Fact]
    public void The_http_egress_and_observation_surface_carries_documentation()
    {
        var abstractions = typeof(IHttpEgressPolicyReport).Assembly;
        var xml = Path.ChangeExtension(abstractions.Location, ".xml");
        File.Exists(xml).Should().BeTrue("the library generates its documentation file");
        var documented = XDocument.Load(xml).Descendants("member")
            .Where(member => member.Element("summary") is { Value: var text } && text.Trim().Length > 0)
            .Select(member => (string)member.Attribute("name")!)
            .ToHashSet(StringComparer.Ordinal);

        var expected = new List<string> { "M:" + typeof(IHttpEgressPolicyReport).FullName + ".Assess" };
        foreach (var type in new[] { typeof(HttpEgressPolicyView), typeof(OutboundObservationView) })
        {
            expected.AddRange(type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(property => $"P:{type.FullName}.{property.Name}"));
        }

        expected.Should().HaveCountGreaterThan(8, "an empty reflection result would pass vacuously");
        expected.Where(name => !documented.Any(d => d == name || d.StartsWith(name + "(", StringComparison.Ordinal)))
            .Should().BeEmpty("every public member a consumer can read needs prose documentation");
    }
}
