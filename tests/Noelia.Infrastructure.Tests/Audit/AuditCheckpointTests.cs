using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Noelia.Abstractions.Audit;
using Noelia.Infrastructure.Audit;

namespace Noelia.Infrastructure.Tests.Audit;

[Trait("Category", "Unit")]
public sealed class AuditCheckpointTests
{
    [Fact]
    public async Task A_signed_independent_checkpoint_covers_exactly_the_captured_full_chain()
    {
        var (sink, trail) = Setup();
        await trail.RecordAsync<object>("synthetic", "operator", "Read", "first");
        await trail.RecordAsync<object>("synthetic", "operator", "Read", "second");
        var snapshot = (await sink.TryCaptureSnapshotAsync())!;
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var document = AuditCheckpointSignature.Sign(snapshot, signer.ExportECPrivateKeyPem(), DateTimeOffset.UtcNow);
        var path = Path.Combine(Path.GetTempPath(), $"noelia-checkpoint-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document));
            var source = new SignedFileAuditCheckpointSource(path, signer.ExportSubjectPublicKeyInfoPem());
            var result = await Verify(sink, source);
            result.Evidence!.SchemaVersion.Should().Be(2);
            result.Evidence.Consistency.Should().Be(AuditChainConsistency.Consistent);
            result.Evidence.Completeness.Should().Be(AuditChainCompleteness.Complete);
            result.Evidence.HasStableSnapshot.Should().BeTrue();
            result.Evidence.SequenceVerified.Should().BeTrue();
            result.Evidence.HasIndependentCheckpoint.Should().BeTrue();
            result.Evidence.CheckpointState.Should().Be("Matched");
            result.Evidence.CheckpointId.Should().NotBeNullOrWhiteSpace();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_modified_checkpoint_does_not_turn_a_consistent_store_into_complete_evidence()
    {
        var (sink, trail) = Setup();
        await trail.RecordAsync<object>("synthetic", "operator", "Read", "first");
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var document = AuditCheckpointSignature.Sign((await sink.TryCaptureSnapshotAsync())!,
            signer.ExportECPrivateKeyPem(), DateTimeOffset.UtcNow) with { EntryCount = 99 };
        var path = Path.Combine(Path.GetTempPath(), $"noelia-checkpoint-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document));
            var result = await Verify(sink,
                new SignedFileAuditCheckpointSource(path, signer.ExportSubjectPublicKeyInfoPem()));
            result.Evidence!.Consistency.Should().Be(AuditChainConsistency.Consistent);
            result.Evidence.Completeness.Should().Be(AuditChainCompleteness.Unknown);
            result.Evidence.CheckpointState.Should().Be("Rejected");
            result.Evidence.HasIndependentCheckpoint.Should().BeFalse();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task An_older_valid_checkpoint_cannot_prove_a_newer_head()
    {
        var (sink, trail) = Setup();
        await trail.RecordAsync<object>("synthetic", "operator", "Read", "first");
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var document = AuditCheckpointSignature.Sign((await sink.TryCaptureSnapshotAsync())!,
            signer.ExportECPrivateKeyPem(), DateTimeOffset.UtcNow);
        await trail.RecordAsync<object>("synthetic", "operator", "Read", "second");
        var path = Path.Combine(Path.GetTempPath(), $"noelia-checkpoint-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document));
            var result = await Verify(sink,
                new SignedFileAuditCheckpointSource(path, signer.ExportSubjectPublicKeyInfoPem()));
            result.Evidence!.Consistency.Should().Be(AuditChainConsistency.Consistent);
            result.Evidence.Completeness.Should().Be(AuditChainCompleteness.Unknown);
            result.Evidence.CheckpointState.Should().Be("Mismatch");
        }
        finally { File.Delete(path); }
    }

    private static (InMemorySovereignAuditSink Sink, AuditTrailService Trail) Setup()
    {
        var sink = new InMemorySovereignAuditSink();
        return (sink, new AuditTrailService(sink, NullLogger<AuditTrailService>.Instance));
    }

    private static async Task<AuditChainVerification> Verify(InMemorySovereignAuditSink sink,
        ITrustedAuditCheckpointSource source)
    {
        await using var services = new ServiceCollection()
            .AddSingleton<ISovereignAuditSink>(sink)
            .AddSingleton(source)
            .BuildServiceProvider();
        return await new AuditChainVerifier(services).VerifyAsync();
    }
}
