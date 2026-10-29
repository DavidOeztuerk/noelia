using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceCallerHost;

namespace Noelia.Infrastructure.Tests.Security.ServiceTokens;

/// <summary>
/// Outwardly every refusal is the same 404; inwardly each one says why, in a log
/// line and a counter, and neither ever holds the token.
/// </summary>
[Trait("Category", "Unit")]
[Collection(ServiceCallerHttpCollection.Name)]
public class ServiceCallerAuditTests
{
    private const string Account = "/internal/account/7";
    private const string Note = "/internal/account/7/note";

    [Theory]
    [InlineData("no-token", "no_token")]
    [InlineData("garbage", "invalid_token")]
    [InlineData("expired", "expired")]
    [InlineData("wrong-audience", "wrong_audience")]
    [InlineData("wrong-type", "wrong_type")]
    [InlineData("two-headers", "invalid_token")]
    [InlineData("not-bearer", "invalid_token")]
    [InlineData("wrong-issuer", "invalid_token")]
    public async Task A_refusal_is_logged_with_its_class_and_counted(string situation, string reason)
    {
        await using var host = await StartAsync();
        using var counter = new M2mCounter();
        using var request = Build(situation);

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        var entry = host.Logs.Audit.Should().ContainSingle().Subject;
        entry.Fields["Reason"].Should().Be(reason);
        entry.Fields["Door"].Should().Be("/internal/account/{id}");
        entry.Fields.Should().NotContainKey("Caller", "an unverified caller is never named");
        entry.Level.Should().Be(LogLevel.Warning);

        var tags = counter.Single();
        tags["outcome"].Should().Be("denied");
        tags["reason"].Should().Be(reason);
        tags["door"].Should().Be("/internal/account/{id}");
        tags.Should().NotContainKey("caller");
    }

    [Fact]
    public async Task A_person_token_is_a_refusal_with_a_class_and_no_caller()
    {
        await using var host = await StartAsync();
        using var counter = new M2mCounter();

        using var response = await host.Client.SendAsync(
            Wire.Request(HttpMethod.Get, Account, Wire.Person(clientIdClaim: "advisor")));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        var tags = counter.Single();
        tags["outcome"].Should().Be("denied");
        tags["reason"].Should().BeOneOf("wrong_audience", "wrong_type", "invalid_token");
        tags.Should().NotContainKey("caller", "the client_id in a person's token is the person's own claim");
    }

    [Fact]
    public async Task A_valid_caller_who_is_not_on_the_list_is_named_because_the_token_was_verified()
    {
        await using var host = await StartAsync();
        using var counter = new M2mCounter();

        using var response = await host.Client.SendAsync(
            Wire.Request(HttpMethod.Get, Account, await Wire.Issued("notification")));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        var entry = host.Logs.Audit.Should().ContainSingle().Subject;
        entry.Fields["Reason"].Should().Be("caller_not_allowed");
        entry.Fields["Caller"].Should().Be("notification");
        var tags = counter.Single();
        tags["reason"].Should().Be("caller_not_allowed");
        tags["caller"].Should().Be("notification");
    }

    [Fact]
    public async Task A_listed_caller_on_a_path_with_no_door_is_counted_as_not_allowed_at_the_fallback()
    {
        await using var host = await StartAsync();
        using var counter = new M2mCounter();

        using var response = await host.Client.SendAsync(
            Wire.Request(HttpMethod.Get, "/internal/nothing/here", await Wire.Issued("advisor")));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        var tags = counter.Single();
        tags["reason"].Should().Be("caller_not_allowed");
        tags["caller"].Should().Be("advisor");
        tags["door"].Should().Be("/internal/{**rest}");
    }

    [Fact]
    public async Task A_failing_second_policy_is_classed_other_policy()
    {
        await using var host = await StartAsync();
        using var counter = new M2mCounter();

        using var response = await host.Client.SendAsync(
            Wire.Request(HttpMethod.Get, "/internal/other", await Wire.Issued("advisor")));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        var tags = counter.Single();
        tags["reason"].Should().Be("other_policy");
        tags["caller"].Should().Be("advisor", "the service identity itself was good");
    }

    [Fact]
    public async Task A_success_names_the_caller_and_the_door()
    {
        await using var host = await StartAsync();
        using var counter = new M2mCounter();

        using var response = await host.Client.SendAsync(
            Wire.Request(HttpMethod.Get, Account, await Wire.Issued("advisor")));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var entry = host.Logs.Audit.Should().ContainSingle().Subject;
        entry.Fields["Caller"].Should().Be("advisor");
        entry.Fields["Door"].Should().Be("/internal/account/{id}");
        entry.Level.Should().Be(LogLevel.Information);

        var tags = counter.Single();
        tags["outcome"].Should().Be("allowed");
        tags["caller"].Should().Be("advisor");
        tags["door"].Should().Be("/internal/account/{id}");
    }

    [Fact]
    public async Task One_request_is_one_event_and_one_count()
    {
        await using var host = await StartAsync();
        using var counter = new M2mCounter();

        for (var i = 0; i < 3; i++)
        {
            using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, Account));
        }

        host.Logs.Audit.Should().HaveCount(3);
        counter.Count.Should().Be(3);
    }

    [Fact]
    public async Task A_person_endpoint_is_not_audited_as_a_door()
    {
        await using var host = await StartAsync();
        using var counter = new M2mCounter();

        using var response = await host.Client.SendAsync(
            Wire.Request(HttpMethod.Get, "/people/me", Wire.Person()));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        host.Logs.Audit.Should().BeEmpty();
        counter.Count.Should().Be(0);
    }

    [Fact]
    public async Task Nothing_the_caller_sent_reaches_the_log()
    {
        await using var host = await StartAsync();
        var served = await Wire.Issued("advisor");
        var tampered = served[..^4] + "AAAA";
        var expired = Wire.Service(lifetime: TimeSpan.FromMinutes(-2));
        var person = Wire.Person(clientIdClaim: "attacker-chosen-name");

        foreach (var token in new[] { served, tampered, expired, person })
        {
            using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Post, Note, token, "{\"text\":\"x\"}"));
        }

        var everything = string.Join("\n", host.Logs.Entries.Select(entry =>
            entry.Message + "\n" + string.Join("\n", entry.Fields.Select(f => f.Key + "=" + f.Value))));
        foreach (var token in new[] { served, tampered, expired, person })
        {
            everything.Should().NotContain(token);
            everything.Should().NotContain(token.Split('.')[1], "the payload is as secret as the token");
            everything.Should().NotContain(token.Split('.')[2], "nor is the signature");
        }

        everything.Should().NotContain("attacker-chosen-name");
    }

    private static HttpRequestMessage Build(string situation)
    {
        switch (situation)
        {
            case "garbage":
                return Wire.Request(HttpMethod.Get, Account, Garbage);
            case "expired":
                return Wire.Request(HttpMethod.Get, Account, Wire.Service(lifetime: TimeSpan.FromMinutes(-2)));
            case "wrong-audience":
                return Wire.Request(HttpMethod.Get, Account, Wire.Service(audience: "billing"));
            case "wrong-type":
                return Wire.Request(HttpMethod.Get, Account, Wire.Service(type: "JWT"));
            case "wrong-issuer":
                return Wire.Request(HttpMethod.Get, Account, Wire.Service(issuer: "https://someone-else.test"));
            case "two-headers":
                var two = Wire.Request(HttpMethod.Get, Account);
                two.Headers.TryAddWithoutValidation(
                    "Authorization", new[] { "Bearer " + Wire.Service(), "Bearer " + Wire.Service() });
                return two;
            case "not-bearer":
                var basic = Wire.Request(HttpMethod.Get, Account);
                basic.Headers.TryAddWithoutValidation("Authorization", "Basic " + Convert.ToBase64String("a:b"u8));
                return basic;
            default:
                return Wire.Request(HttpMethod.Get, Account);
        }
    }

    private static Task<ServiceCallerHost> StartAsync() => ServiceCallerHost.StartAsync();

    /// <summary>Listens to the process-wide counter; the collection keeps other hosts out of it.</summary>
    private sealed class M2mCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<Dictionary<string, string?>> _measurements = [];

        public M2mCounter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == "noelia.m2m.requests")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                var copy = new Dictionary<string, string?>();
                foreach (var tag in tags)
                {
                    copy[tag.Key] = tag.Value?.ToString();
                }

                lock (_measurements)
                {
                    _measurements.Add(copy);
                }
            });
            _listener.Start();
        }

        public int Count
        {
            get
            {
                lock (_measurements)
                {
                    return _measurements.Count;
                }
            }
        }

        public Dictionary<string, string?> Single()
        {
            lock (_measurements)
            {
                return _measurements.Should().ContainSingle().Subject;
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
