using System.Net;

using Noelia.Abstractions.Sovereignty;

namespace Noelia.Infrastructure.Sovereignty;

/// <summary>
/// Classifies a host name by what it reveals about who operates it.
/// </summary>
public static class HostJurisdiction
{
    /// <summary>
    /// Domains belonging to providers subject to third-country access laws such
    /// as the US CLOUD Act.
    /// </summary>
    /// <remarks>
    /// Recognition by name is deliberately narrow: it produces no false alarms
    /// and it cannot be complete. A provider missing from this list comes back
    /// as <see cref="Jurisdiction.Undetermined"/>, which is the honest answer —
    /// absence of a match is not evidence of sovereignty.
    /// </remarks>
    private static readonly string[] ThirdCountryDomains =
    [
        ".amazonaws.com", ".aws.amazon.com", ".cloudfront.net",
        ".azure.com", ".azurewebsites.net", ".windows.net", ".azure-api.net",
        ".microsoftonline.com", ".cognitiveservices.azure.com",
        ".googleapis.com", ".cloud.google.com", ".appspot.com", ".gcp.gvt2.com",
        ".oraclecloud.com", ".ibmcloud.com", ".cloudflare.com", ".workers.dev",
        ".openai.com", ".anthropic.com", ".datadoghq.com", ".newrelic.com",
        ".sentry.io", ".segment.com", ".google-analytics.com", ".doubleclick.net",

        // The layer an application actually calls. The list above covers where
        // a system is hosted; these are the services it talks to — payment,
        // mail, messaging, support, identity — and they were missing entirely,
        // so a customer running Stripe and SendGrid was told twice that it
        // could not be determined.
        ".stripe.com", ".paypal.com", ".adyen.com", ".braintreegateway.com",
        ".sendgrid.net", ".sendgrid.com", ".mailgun.net", ".mailgun.org",
        ".postmarkapp.com", ".mandrillapp.com",
        ".twilio.com", ".sendbird.com", ".pusher.com",
        ".slack.com", ".zendesk.com", ".intercom.io", ".hubapi.com",
        ".auth0.com", ".okta.com", ".onelogin.com",
        ".github.com", ".githubusercontent.com", ".atlassian.net",
        ".algolia.net", ".algolianet.com", ".mixpanel.com", ".amplitude.com",
        ".bugsnag.com", ".rollbar.com", ".logdna.com", ".loggly.com",
        ".snowflakecomputing.com", ".mongodb.net", ".redislabs.com",
        ".upstash.io", ".planetscale.com", ".supabase.co", ".firebaseio.com"
    ];

    /// <summary>
    /// Hosts that serve a model or an inference API.
    /// </summary>
    /// <remarks>
    /// <para>Separate from <see cref="ThirdCountryDomains"/> because the two
    /// questions are separate, and the answers cross: <c>api.openai.com</c> is
    /// both, <c>api.mistral.ai</c> is AI and not obviously third-country, and
    /// an S3 bucket is third-country and not AI. Folding them into one list
    /// would make every AI finding also a jurisdiction finding, which is how a
    /// register starts telling people things that are not true.</para>
    ///
    /// <para><strong>What this cannot see.</strong> A model served from
    /// <c>ml.internal</c> or from a name the operator chose is invisible here,
    /// and so is one reached through a gateway. The inventory therefore has a
    /// floor, not a ceiling: everything it lists is an AI destination, and it
    /// does not claim to list every one. The check that reports it says exactly
    /// that, because an AI inventory that looks exhaustive and is not is the
    /// worst possible input to an Article 26 assessment.</para>
    /// </remarks>
    private static readonly string[] ArtificialIntelligenceDomains =
    [
        // Model APIs.
        ".openai.com", ".anthropic.com", ".mistral.ai", ".cohere.ai", ".cohere.com",
        ".groq.com", ".deepseek.com", ".x.ai", ".together.xyz", ".together.ai",
        ".perplexity.ai", ".fireworks.ai", ".anyscale.com", ".replicate.com",
        ".aleph-alpha.com", ".ai21.com", ".voyageai.com",

        // Hyperscaler model surfaces. The parent domains already appear as
        // third-country; these narrower names add the second fact.
        ".openai.azure.com", ".cognitiveservices.azure.com",
        ".bedrock.amazonaws.com", ".sagemaker.amazonaws.com",
        "generativelanguage.googleapis.com", "aiplatform.googleapis.com",

        // Weights, inference and evaluation infrastructure.
        ".huggingface.co", ".hf.co", ".modal.run", ".runpod.io",
        ".langsmith.com", ".smith.langchain.com", ".wandb.ai",

        // Speech, vision and embedding services, which are AI systems under the
        // Act whether or not anyone in the building calls them that.
        ".elevenlabs.io", ".deepgram.com", ".assemblyai.com", ".stability.ai",
        ".clarifai.com", ".pinecone.io", ".weaviate.cloud", ".qdrant.tech"
    ];

    /// <summary>
    /// Endings that mean a value is a file on disk rather than a name in DNS.
    /// </summary>
    /// <remarks>
    /// <para><c>invoices.db</c> is a SQLite file and <c>.db</c> is not a
    /// delegated top-level domain, but a classifier that only looks for a dot
    /// cannot tell the two apart — and reported the file as a public name whose
    /// operator could not be determined.</para>
    ///
    /// <para>A false <c>Undetermined</c> is worse than no classification. The
    /// register exists to answer "where can our data go"; reporting a local
    /// file as an open question teaches an operator to skip past
    /// <c>Undetermined</c>, and then they skip past the real one.</para>
    ///
    /// <para>Narrow on purpose, like the third-country list. A file extension
    /// nobody thought of is still classified by name, which is the honest
    /// failure — the alternative, carrying the IANA list to decide what is not
    /// a domain, is a dependency on a moving target for a question this rarely
    /// asks.</para>
    /// </remarks>
    private static readonly string[] FileSuffixes =
    [
        ".db", ".db3", ".sqlite", ".sqlite3", ".mdb", ".mdf",
        ".json", ".xml", ".yaml", ".yml", ".ini", ".toml", ".config",
        ".log", ".dat", ".csv", ".txt", ".bak", ".tmp",
        ".pem", ".key", ".pfx", ".p8", ".crt"
    ];

    /// <summary>Suffixes reserved for names that never leave an internal network.</summary>
    private static readonly string[] InternalSuffixes =
    [
        ".internal", ".local", ".localdomain", ".lan", ".home.arpa",
        ".svc", ".svc.cluster.local", ".cluster.local"
    ];

    /// <summary>
    /// Classifies <paramref name="host"/> and explains the classification.
    /// </summary>
    public static (Jurisdiction Jurisdiction, string Note) Classify(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return (Jurisdiction.Undetermined, "No host configured.");
        }

        if (IsLoopback(host))
        {
            return (Jurisdiction.SelfHosted, "Loopback — the same machine.");
        }

        if (IPAddress.TryParse(host, out var address))
        {
            return IsPrivate(address)
                ? (Jurisdiction.SelfHosted, "Private network address.")
                : (Jurisdiction.Undetermined, "Public IP address; the operator cannot be told from it.");
        }

        // Before the dot test, because a path is not a name however many dots
        // it contains.
        if (IsFilePath(host))
        {
            return (Jurisdiction.SelfHosted, "A file on this machine, not a name in DNS.");
        }

        foreach (var suffix in InternalSuffixes)
        {
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return (Jurisdiction.SelfHosted, $"Internal name ({suffix}).");
            }
        }

        if (!host.Contains('.'))
        {
            return (Jurisdiction.SelfHosted, "Single-label name — resolved inside the network.");
        }

        foreach (var domain in ThirdCountryDomains)
        {
            // The apex as well as anything under it. Every entry is written
            // with a leading dot so that "notamazonaws.com" cannot match, but
            // EndsWith alone then answers for api.openai.com and not at all for
            // openai.com — which is where a REST API usually lives. A provider
            // reported as Undetermined reads as "we could not tell", and that
            // is the one answer this must never give about a name it knows.
            var apex = domain.TrimStart('.');

            if (host.EndsWith(domain, StringComparison.OrdinalIgnoreCase)
                || host.Equals(apex, StringComparison.OrdinalIgnoreCase))
            {
                return (Jurisdiction.ThirdCountryProvider,
                    $"{apex} is operated by a provider subject to third-country access law.");
            }
        }

        return (Jurisdiction.Undetermined,
            "Public name; who operates it and under which law cannot be told from the name.");
    }

    /// <summary>
    /// Whether this host is known to serve a model or an inference API.
    /// </summary>
    /// <remarks>
    /// A name-based answer, so <c>false</c> means "not recognised" and never
    /// "not an AI system". Callers are expected to say so where they show it.
    /// </remarks>
    /// <param name="host">The host, without scheme, credentials or port.</param>
    public static bool IsArtificialIntelligence(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        foreach (var domain in ArtificialIntelligenceDomains)
        {
            var apex = domain.TrimStart('.');

            if (host.EndsWith(domain, StringComparison.OrdinalIgnoreCase)
                || host.Equals(apex, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether this is plainly a path rather than a host.</summary>
    /// <remarks>
    /// A separator settles it outright: no DNS name contains one. Failing that,
    /// a known data-file ending does — <c>invoices.db</c> has no separator and
    /// is still a file.
    /// </remarks>
    private static bool IsFilePath(string host) =>
        host.Contains('/', StringComparison.Ordinal)
        || host.Contains('\\', StringComparison.Ordinal)
        || FileSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
        }

        var octets = address.GetAddressBytes();

        return octets[0] switch
        {
            10 => true,
            172 => octets[1] >= 16 && octets[1] <= 31,
            192 => octets[1] == 168,
            _ => false
        };
    }
}
