# Digital sovereignty

Noelia is built so that an application on top of it can run without depending on
infrastructure outside the operator's control or jurisdiction.

**What this document does not claim.** A library cannot make a deployment
sovereign. Where servers stand, who owns them and which law reaches them are
decisions made outside the code. What Noelia can do — and what it does — is make
every such decision explicit, refuse to make one silently on your behalf, and
report what the running configuration actually points at.

## The frame

The European Commission's Cloud Sovereignty Framework scores eight objectives
(SOV-1 to SOV-8) on five assurance levels, SEAL-0 (no sovereignty) to SEAL-4
(full digital sovereignty). SEAL-4 requires two things in particular: data held
only within the EU, and *customer key sovereignty* — the provider has no
technical access to the encryption keys.

Most of those objectives are properties of a deployment, not of a library. Three
are directly affected by code:

| Objective | What Noelia contributes |
|---|---|
| **SOV-2** Legal & jurisdictional | No SDK for a third-country provider is a dependency. Configuring one is refused rather than silently substituted. |
| **SOV-3** Data & AI | Outbound destinations must be declared; undeclared calls fail. Key material can be held outside the application. |
| **SOV-6** Technology | Every dependency is permissively licensed, and every backend can be replaced by one you run yourself. |

SOV-1, SOV-4, SOV-5, SOV-7 and SOV-8 — ownership, operations, supply chain,
certification, sustainability — belong to whoever runs the system.

## Backends that keep you sovereign

Noelia talks to each of these through a wire protocol, not a vendor SDK, so the
sovereign option is a configuration change rather than a rewrite.

| Purpose | Use | Rather than | Why |
|---|---|---|---|
| Secrets | **OpenBao** | HashiCorp Vault | Vault moved to the Business Source License in 2023. OpenBao is the MPL-2.0 fork under Linux Foundation governance, and speaks the same HTTP API. |
| Cache | **Valkey** | Redis | Redis moved to RSALv2/SSPL in 2024. Valkey is the BSD-3-Clause fork under Linux Foundation governance, wire-compatible with the same client. |
| Logs & search | **OpenSearch** | Elasticsearch | Elasticsearch is under SSPL, which the Open Source Initiative does not recognise as open source. |
| Database | **PostgreSQL** | — | Already permissive, already self-hostable. |
| Messaging | **RabbitMQ** | — | MPL-2.0, self-hostable. |
| Telemetry | **OpenTelemetry** to a collector you run | a vendor endpoint | The exporter defaults to `localhost:4317`, so nothing leaves the host unless you point it elsewhere. |

None of these switches requires a change in Noelia. The client libraries are
unchanged; only the endpoint moves.

## What the code does

### Outbound calls must be declared

```csharp
builder.Services.AddNoeliaEgressPolicy(p => p
    .AllowLoopback()
    .AllowPrivateNetworks()
    .Allow("openbao.internal"));
```

Every client created through `IHttpClientFactory` is then guarded, and a call to
anything else throws `EgressDeniedException` **before it leaves**. A log entry
about data that already left is a record, not a control.

When the policy enforces a boundary, automatic redirects are disabled. Redirect
responses are returned for an explicit, separately policy-checked decision; an
allowed host must not silently send a request or its body to another host.
HttpClientHandler and SocketsHttpHandler are supported primary transports;
custom primary transports are refused under an enforcing policy. This is an
HTTP-client boundary, not process-wide network isolation.

Declaring nothing keeps the previous behaviour, so adding this changes nothing
until someone states an intent. Loopback and private ranges are opt-in rather
than assumed: a collector on localhost is still a destination.

A client built with `new HttpClient()` bypasses the factory and therefore this
guard. That is a limitation of the mechanism, not a gap the report hides.

### The configuration reports itself

```csharp
builder.Services.AddNoeliaSovereigntyReport(
    new DeclaredDependency("Secrets", configuration["OpenBao:Address"]),
    new DeclaredDependency("Telemetry", configuration["Otlp:Endpoint"]));
```

Connection strings are picked up automatically. Each destination is classified:

- **Self-hosted** — loopback, a private range, or an internal name.
- **Third-country provider** — a domain belonging to a provider subject to
  third-country access law, recognised by name. A region in Frankfurt does not
  change who operates the service.
- **Undetermined** — a public name whose operator cannot be told from the name.

**Undetermined is not a pass.** Recognition by name produces no false alarms and
cannot be complete; a provider missing from the list comes back undetermined,
and answering that is the operator's job. Credentials never reach the report.

### You hold the master key

Noelia never generates and never stores the key that protects stored key
material. It asks for it, uses it, and forgets it when the process ends.

```csharp
// The sovereign arrangement: the key lives in a store you run.
builder.Services.AddSecretStoreMasterKey();

// Or, where the key arrives as an environment variable from outside:
builder.Services.AddConfiguredMasterKey();
```

Without one of these the application does not start. There is no generated
default, because a key the application invents is a key the operator does not
hold — which is exactly what SEAL-4 rules out.

Key material is sealed under that master key with AES-GCM before it reaches the
store, so whoever can read the cache holds ciphertext rather than keys, and a
modified entry fails to open rather than being used.

### Secrets stay where you put them

`ISecretProvider` is the seam that makes the secret store a choice. Only
providers you can run yourself ship in the box: OpenBao, environment variables,
an encrypted file, and ASP.NET Data Protection. Configuring `azure` or `aws`
fails with a message saying so.

Earlier versions shipped `AzureKeyVaultProvider` and `AwsSecretsManagerProvider`
that referenced no cloud SDK and kept secrets in a dictionary. They are gone.

The file provider derives its key with PBKDF2-HMAC-SHA256, 600,000 iterations,
and a salt generated once per installation. There is no fallback password, and
values are sealed with AES-GCM so a tampered file fails to open rather than
decrypting into something the caller trusts.

### Personal data does not reach the log

Three paths can carry a value into a log: the CQRS behaviour writing a whole
command, the HTTP middleware writing a whole body, and Serilog writing a
structured property. All three read one list — `SensitiveFieldNames` — and all
three write one mask, `[REDACTED]`.

```
[10:14:03 WRN] [corr-7f2a] Identity: sign-in failed for {Username}
                           Username: [REDACTED]
```

Matched **exactly**, never as a substring. That is what keeps `SecretName`,
`TokenId`, `ServiceName` and `TokenEndpoint` readable — a secret's name is not a
secret and a token's id is not a token, and a log with those redacted is one
nobody can follow. It also means a name the list does not carry is **not**
redacted: if you log something personal under a name of your own, add the name to
the list.

The list is deliberately broad — `username`, `email`, `city`, `name`,
`birthdate`, `iban`, `passportnumber`. If you want one of them visible, take it
out of `SensitiveFieldNames` once and it is out on all three paths.

### The audit trail says whether it was changed

An audit trail that can be edited afterwards records nothing an auditor can rely
on. `IAuditTrailService` chains every entry to the previous one with SHA-256:

```csharp
await audit.RecordAsync(
    actorId: subject.ToString(),
    capacity: principal.Capacity,          // as self, or for a company
    action: "Update",
    resource: $"Consent/{consentId}",
    before: previous,
    after: current,
    correlationId: CorrelationId.Current);
```

Each entry carries `PreviousHash`, and its own `Hash` is computed over every
field including that one. Change any field of any entry and every hash after it
stops matching. Fields are written with their length, so content shifted across a
field boundary changes the hash too — otherwise a `CorrelationId` set by the
caller could absorb the field beside it and verify anyway.

The chain is advanced and the entry written to the sink in one serialised turn,
so the stored order is the chained order. A verifier reading the store back finds
a broken chain only where something is actually wrong.

**Where it is stored is your decision.** `ISovereignAuditSink` is the port;
Noelia ships only an in-process sink for tests and local work. Each replica keeps
its own chain — if you need one chain across replicas, that belongs in the sink.

### One call for the sovereign defaults

```csharp
builder.Services.AddNoelia(config, env, "identity-service", noelia => noelia
    .UseDefaults()
    .AddSovereignPlatform(sovereign => sovereign
        .WithoutPrivateNetworks()
        .Allow("openbao.internal", "postgres.internal")
        .DeclareDependency("Secrets", config["OpenBao:Address"])
        .WithAuditSink<PostgresAuditSink>()));
```

It bundles the egress boundary, the sovereignty report and the audit trail. It is
a module like any other — it appears in `NoeliaComposition`, and a service that
deliberately calls outward drops it with a reason:

```csharp
.Without(NoeliaModule.SovereignPlatform, "acceptance stage calls the sandbox on purpose")
```

Dropped, **nothing** of it is set up. A boundary that stood anyway would go on
refusing the calls the reason was written for.

Loopback and the RFC1918 ranges are allowed by default, because the database, the
cache and the broker live there. `WithoutLoopback()` and
`WithoutPrivateNetworks()` close them where nothing needs them.

### The operator dashboard reports one instance, without configuration values

The optional `Noelia.Dashboard` package reads the sovereignty and audit ports
from `Noelia.Abstractions`; it does not take the infrastructure dependency graph
with it. It shows configured dependency hosts and declared egress hosts because
those destinations are the subject of the report. It never shows a connection
string, credential, configuration value, token, raw device fingerprint, audit
state snapshot or rate-limit key.

Access is deliberately absent until the application supplies
`VisibleTo(context => ...)`. Rejected requests receive the same empty 404 as an
unknown path. Production additionally requires `InProduction(reason)`, and the
reason is recorded as a decision but rendered only as a character count. The
page and its embedded assets are read-only, non-cacheable and protected by a
restrictive content-security policy.

Each authorized request is appended to `IAuditTrailService` when that port is
registered; a write failure prevents the page from being served. The page names
the answering machine and labels audit, session and in-process rate-limit views
as instance-scoped. It must never let a reader mistake one replica's partial
view for a cluster-wide guarantee.

## What you still have to do

Noelia cannot do these for you:

1. **Choose where it runs.** A sovereign stack on a third-country hyperscaler is
   not sovereign.
2. **Keep the master key somewhere you control.** Noelia requires one and never
   invents it, but where it lives — a store you run, or an environment variable
   handed in by a platform you may not control — is your decision.
3. **Answer the undetermined entries** in the report — with a contract, not a
   host name.
4. **Keep an exit.** `IDataExportService` and `IDataErasureService` are the
   seams for GDPR Articles 20 and 17; they need implementations in each service
   before portability is real.

## Known gaps

- **No memory-hard password hashing.** Argon2id, Argon2i, Argon2d, BCrypt and
  SCrypt appear in `HashingAlgorithm` but are not implemented, and selecting one
  is refused rather than quietly answered with PBKDF2. Adding a real Argon2id
  would mean adding a dependency, which is a decision, not an omission.
- **`new HttpClient()` escapes the egress guard**, as noted above.
- **One audit chain per replica.** The in-process sink chains what one process
  wrote. A chain across replicas needs a sink that orders writes itself, and that
  is a property of the store, not of this library.
- **The default audit port cannot reread a persisted sink.** The dashboard can
  state that this process produced a valid chain at write time, but it labels
  the sink as unverified. Claiming later integrity needs a provider-specific,
  read-capable verifier.
- **The mask is name-based, not value-based** for structured properties. A
  password logged under a name nobody put on the list still reaches the log; the
  value patterns (`email`, `credit card`, `IBAN`, `SSN`) catch shapes, not
  everything.

## Sources

- [EU Cloud Sovereignty Framework — SEAL levels and sovereignty objectives](https://www.nlighten.com/en/blog/the-eu-cloud-sovereignty-framework-explained-seal-levels-sovereignty-objectives-and-the-sovereignty-score/)
- [European Commission — A Cloud Sovereignty Framework for strategic procurement](https://data-en-maatschappij.ai/en/publications/europese-commissie-een-kader-voor-cloudsoevereiniteit-bij-strategische-aanbesteding)
- [OpenBao — the MPL-2.0 fork of Vault under Linux Foundation governance](https://openbao.org/)
- [Valkey — the BSD-3-Clause fork of Redis](https://valkey.io/)
- [Bitkom — Kriterien für Cloud-Souveränität in Europa](https://www.bitkom.org/Bitkom/Publikationen/Kriterien-fuer-Cloud-Souveraenitaet-in-Europa)
