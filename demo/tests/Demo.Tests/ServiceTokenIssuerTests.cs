using System.Net;
using System.Text.Json;
using Noelia.Infrastructure.Security.Keys;

namespace Demo.Tests;

/// <summary>
/// The user service is the demo's issuer of service tokens. These tests assert
/// the effect over HTTP — the token endpoint answers, the brake in front of it
/// brakes, and the host reports the property it promises — rather than that a
/// module was composed.
/// </summary>
public sealed class ServiceTokenIssuerTests : IDisposable
{
    private const string TokenPath = "/m2m/token";
    private static readonly GeneratedKeyPair Keys = SigningKey.GenerateKeyPair("service-token-issuer");
    private readonly TemporaryDatabase _database = new();
    private readonly ServiceFactory<UserService.Api.ServiceEntryPoint> _users;

    public ServiceTokenIssuerTests() =>
        _users = new ServiceFactory<UserService.Api.ServiceEntryPoint>(Keys, true, _database.Path);

    [Fact]
    public async Task The_report_after_start_shows_the_caller_policy_check_passing()
    {
        using var client = _users.CreateClient();

        // The first run happens before the routes exist and says Warning; the
        // run after startup is the one that counts, so wait for it rather than
        // judging the first line.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        string? status = null;
        string? summary = null;
        while (DateTime.UtcNow < deadline)
        {
            using var report = JsonDocument.Parse(await client.GetStringAsync("/noelia/report.json"));
            var check = report.RootElement.GetProperty("securityChecks").GetProperty("results")
                .EnumerateArray()
                .FirstOrDefault(result => result.GetProperty("id").GetString() == "noelia.m2m.caller-policy");
            if (check.ValueKind == JsonValueKind.Object)
            {
                status = check.GetProperty("status").GetString();
                summary = check.GetProperty("summary").GetString();
                if (status == "Pass")
                {
                    break;
                }
            }

            await Task.Delay(250);
        }

        status.Should().Be("Pass", "the issuer maps exactly one rate-limited token endpoint; summary: {0}", summary);
    }

    [Fact]
    public async Task The_token_endpoint_issues_a_token_for_the_demo_client()
    {
        using var client = _users.CreateClient();

        using var response = await client.PostAsync(TokenPath, Form(M2mTestClient.Secret.Secret));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();
        body.RootElement.GetProperty("token_type").GetString().Should().BeEquivalentTo("Bearer");
    }

    [Fact]
    public async Task A_wrong_secret_gets_the_one_uniform_refusal()
    {
        using var client = _users.CreateClient();
        var wrong = Noelia.Infrastructure.Security.ServiceTokens.ServiceClientSecret.Generate(M2mTestClient.Id).Secret;

        using var response = await client.PostAsync(TokenPath, Form(wrong));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("invalid_client");
    }

    [Fact]
    public async Task The_token_endpoint_is_braked_so_a_guessing_caller_is_stopped()
    {
        using var client = _users.CreateClient();
        var wrong = Noelia.Infrastructure.Security.ServiceTokens.ServiceClientSecret.Generate(M2mTestClient.Id).Secret;

        // Far above any sane limit, far below anything that could be slow.
        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 60; attempt++)
        {
            using var response = await client.PostAsync(TokenPath, Form(wrong));
            statuses.Add(response.StatusCode);
        }

        statuses.Should().Contain(HttpStatusCode.TooManyRequests,
            "RequireRateLimiting names a policy; unless the limiter middleware runs and the policy exists, it limits nothing");
    }

    private static FormUrlEncodedContent Form(string secret) => new(new Dictionary<string, string>
    {
        ["grant_type"] = "client_credentials",
        ["client_id"] = M2mTestClient.Id,
        ["client_secret"] = secret,
        ["audience"] = M2mTestClient.Audience
    });

    public void Dispose()
    {
        _users.Dispose();
        _database.Dispose();
    }
}
