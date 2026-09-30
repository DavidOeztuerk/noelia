using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Noelia.Abstractions.Audit;

namespace Noelia.Infrastructure.Audit;

/// <summary>An immutable signed expectation for an entire audit-chain snapshot.</summary>
/// <remarks>
/// The private signing key must be held outside the mutable audit store. The
/// public verification key is pinned independently of this document. A caller
/// must also check that the named chain is the one it intended to verify.
/// </remarks>
internal sealed record SignedAuditCheckpoint
{
    /// <summary>Supported checkpoint format.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Fixed signature algorithm.</summary>
    public string Algorithm { get; init; } = "ECDSA-P256-SHA256";
    /// <summary>Exact configured chain namespace.</summary>
    public required string ChainId { get; init; }
    /// <summary>First entry's hash, or empty for an empty chain.</summary>
    public required string GenesisHash { get; init; }
    /// <summary>Last entry's hash, or empty for an empty chain.</summary>
    public required string HeadHash { get; init; }
    /// <summary>Number of captured entries.</summary>
    public long EntryCount { get; init; }
    /// <summary>UTC time when an independent signer accepted the snapshot.</summary>
    public DateTimeOffset IssuedAt { get; init; }
    /// <summary>Base64 P1363 ECDSA signature over the canonical payload.</summary>
    public required string Signature { get; init; }
}

/// <summary>Creates and verifies signed checkpoint documents using a pinned P-256 key.</summary>
internal static class AuditCheckpointSignature
{
    private const string CurveOid = "1.2.840.10045.3.1.7";

    /// <summary>Signs an already captured snapshot with an independently held private key.</summary>
    /// <remarks>Signing asserts trust in the captured state. It does not discover or repair missing history.</remarks>
    public static SignedAuditCheckpoint Sign(AuditReadSnapshot snapshot, string privateKeyPem,
        DateTimeOffset issuedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.SequenceVerified || snapshot.StoredHead != snapshot.Entries.LastOrDefault()?.Hash)
            throw new InvalidOperationException("Only a sequence-verified snapshot with a matching head can be checkpointed.");
        string? previous = null;
        foreach (var entry in snapshot.Entries)
        {
            if (!entry.VerifyHash() || entry.PreviousHash != previous)
                throw new InvalidOperationException("The captured audit chain cannot be checkpointed because its entries do not verify.");
            previous = entry.Hash;
        }

        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        RequireP256(key);
        var document = new SignedAuditCheckpoint
        {
            ChainId = snapshot.ChainId,
            GenesisHash = snapshot.Entries.FirstOrDefault()?.Hash ?? string.Empty,
            HeadHash = snapshot.StoredHead ?? string.Empty,
            EntryCount = snapshot.Entries.Count,
            IssuedAt = issuedAt.ToUniversalTime(),
            Signature = string.Empty
        };
        return document with { Signature = Convert.ToBase64String(key.SignData(Canonical(document), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) };
    }

    /// <summary>Authenticates the exact document against a separately pinned public key.</summary>
    public static TrustedAuditCheckpoint Verify(SignedAuditCheckpoint document, string trustedPublicKeyPem)
    {
        ArgumentNullException.ThrowIfNull(document);
        var endpointsValid = document.EntryCount == 0
            ? document.GenesisHash.Length == 0 && document.HeadHash.Length == 0
            : document.GenesisHash.Length > 0 && document.HeadHash.Length > 0;
        if (document.SchemaVersion != 1 || document.Algorithm != "ECDSA-P256-SHA256"
            || string.IsNullOrWhiteSpace(document.ChainId) || document.EntryCount < 0
            || document.IssuedAt.Offset != TimeSpan.Zero
            || !endpointsValid)
            throw new InvalidDataException("The audit checkpoint has an unsupported or inconsistent format.");

        byte[] signature;
        try { signature = Convert.FromBase64String(document.Signature); }
        catch (FormatException) { throw new InvalidDataException("The audit checkpoint signature is malformed."); }
        using var key = ECDsa.Create();
        try { key.ImportFromPem(trustedPublicKeyPem); }
        catch (ArgumentException) { throw new InvalidDataException("The pinned audit checkpoint key is invalid."); }
        RequireP256(key);
        if (!key.VerifyData(Canonical(document), signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidDataException("The audit checkpoint signature is invalid.");

        var id = Convert.ToHexString(SHA256.HashData(Canonical(document))).ToLowerInvariant();
        return new TrustedAuditCheckpoint(document.ChainId, document.GenesisHash,
            document.HeadHash, document.EntryCount, document.IssuedAt, id);
    }

    private static byte[] Canonical(SignedAuditCheckpoint document)
    {
        static string Field(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        var payload = string.Join('\n',
            "noelia.audit.checkpoint.v1", Field(document.ChainId),
            document.EntryCount.ToString(CultureInfo.InvariantCulture),
            Field(document.GenesisHash), Field(document.HeadHash),
            document.IssuedAt.UtcTicks.ToString(CultureInfo.InvariantCulture));
        return Encoding.UTF8.GetBytes(payload);
    }

    private static void RequireP256(ECDsa key)
    {
        if (key.ExportParameters(false).Curve.Oid.Value != CurveOid)
            throw new InvalidDataException("The audit checkpoint key must use P-256.");
    }
}

/// <summary>Reads a signed checkpoint file and pins its trusted public key outside the document.</summary>
internal sealed class SignedFileAuditCheckpointSource(string path, string trustedPublicKeyPem)
    : ITrustedAuditCheckpointSource
{
    /// <inheritdoc />
    public async Task<TrustedAuditCheckpoint?> ReadVerifiedAsync(string chainId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chainId);
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (info.Length > 64 * 1024)
            throw new InvalidDataException("The audit checkpoint document exceeds its size limit.");
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        SignedAuditCheckpoint document;
        try { document = JsonSerializer.Deserialize<SignedAuditCheckpoint>(json)
            ?? throw new InvalidDataException("The audit checkpoint document is empty."); }
        catch (JsonException) { throw new InvalidDataException("The audit checkpoint document is malformed."); }
        var verified = AuditCheckpointSignature.Verify(document, trustedPublicKeyPem);
        if (!string.Equals(verified.ChainId, chainId, StringComparison.Ordinal))
            throw new InvalidDataException("The audit checkpoint names a different chain.");
        return verified;
    }
}
