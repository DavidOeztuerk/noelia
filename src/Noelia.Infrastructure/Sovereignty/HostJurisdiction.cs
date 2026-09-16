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
        ".sentry.io", ".segment.com", ".google-analytics.com", ".doubleclick.net"
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
