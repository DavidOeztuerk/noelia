using System.Net;
using Microsoft.AspNetCore.Builder;
using Noelia.Infrastructure.Security.ServiceTokens;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceCallerHost;

namespace Noelia.Infrastructure.Tests.Security.ServiceTokens;

/// <summary>
/// The receiving side through the whole Noelia chain and real routing: who gets in,
/// and that everyone else gets an answer no different from a door that is not there.
/// </summary>
[Trait("Category", "Unit")]
[Collection(ServiceCallerHttpCollection.Name)]
public class ServiceCallerDoorTests
{
    private const string Account = "/internal/account/7";
    private const string AccountMissing = "/internal/account/7/does-not-exist";
    private const string Note = "/internal/account/7/note";
    private const string NoteBodyJson = "{\"text\":\"hello\"}";

    // ---- who gets in ---------------------------------------------------------

    [Theory]
    [InlineData("advisor", Account, "GET", HttpStatusCode.OK)]
    [InlineData("transfer", Account, "GET", HttpStatusCode.OK)]
    [InlineData("notification", Account, "GET", HttpStatusCode.NotFound)]
    [InlineData("notification", Note, "POST", HttpStatusCode.OK)]
    [InlineData("advisor", Note, "POST", HttpStatusCode.NotFound)]
    [InlineData("transfer", Note, "POST", HttpStatusCode.NotFound)]
    public async Task The_matrix_of_callers_and_doors_lets_in_exactly_the_listed_pairs(
        string caller, string path, string method, HttpStatusCode expected)
    {
        await using var host = await StartAsync();
        var token = caller == "transfer" ? Wire.Service("transfer") : await Wire.Issued(caller);
        using var request = Wire.Request(
            new HttpMethod(method), path, token, method == "POST" ? NoteBodyJson : null);

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task A_listed_caller_reaches_the_handler_and_the_body_is_bound()
    {
        await using var host = await StartAsync();
        using var request = Wire.Request(HttpMethod.Post, Note, await Wire.Issued("notification"), NoteBodyJson);

        using var response = await host.Client.SendAsync(request);

        (await response.Content.ReadAsStringAsync()).Should().Be("note hello");
    }

    [Fact]
    public async Task A_listed_caller_on_a_path_that_does_not_exist_gets_the_same_404_as_everyone()
    {
        await using var host = await StartAsync();
        var token = await Wire.Issued("advisor");

        using var missing = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, AccountMissing, token));
        using var nobody = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, AccountMissing));

        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Wire.Snapshot(missing)).Should().Be(await Wire.Snapshot(nobody));
    }

    // ---- refusals are the 404 of a missing door -------------------------------

    public static TheoryData<string> Refusals =>
    [
        "no-token", "garbage", "expired", "wrong-audience", "wrong-type", "person",
        "person-with-client-id", "two-headers", "not-bearer", "stranger", "wrong-issuer"
    ];

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Every_refusal_is_byte_for_byte_the_answer_of_a_path_that_is_not_there(string refusal)
    {
        await using var host = await StartAsync();
        await AssertRefusalIsIndistinguishable(host, refusal);
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task The_same_holds_with_status_code_pages_in_the_chain(string refusal)
    {
        await using var host = await StartAsync(s => s.StatusCodePages = true);
        await AssertRefusalIsIndistinguishable(host, refusal);
    }

    [Fact]
    public async Task A_person_token_with_a_self_set_client_id_does_not_pass_a_door_that_also_requires_a_login()
    {
        await using var host = await StartAsync();
        var token = Wire.Person(clientIdClaim: "advisor");

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, "/internal/both", token));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_listed_caller_passes_a_door_that_also_requires_a_login()
    {
        await using var host = await StartAsync();
        var token = await Wire.Issued("advisor");

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, "/internal/both", token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_failing_second_policy_on_the_same_door_is_the_same_404()
    {
        await using var host = await StartAsync();
        var token = await Wire.Issued("advisor");

        using var other = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, "/internal/other", token));
        using var missing = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, "/internal/not-there", token));

        other.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Wire.Snapshot(other)).Should().Be(await Wire.Snapshot(missing));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task The_wrong_method_on_an_existing_door_is_a_404_and_never_a_405(string method)
    {
        await using var host = await StartAsync();
        var token = await Wire.Issued("advisor");

        using var wrong = await host.Client.SendAsync(Wire.Request(new HttpMethod(method), Account, token));
        using var missing = await host.Client.SendAsync(Wire.Request(new HttpMethod(method), AccountMissing, token));

        wrong.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Wire.Snapshot(wrong)).Should().Be(await Wire.Snapshot(missing));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData(null)]
    public async Task A_broken_or_missing_body_from_a_caller_who_may_not_enter_is_a_404_and_never_a_400(string? body)
    {
        await using var host = await StartAsync();
        var token = await Wire.Issued("advisor");

        using var refused = await host.Client.SendAsync(Wire.Request(HttpMethod.Post, Note, token, body));
        using var missing = await host.Client.SendAsync(
            Wire.Request(HttpMethod.Post, "/internal/account/7/note/does-not-exist", token, body));

        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Wire.Snapshot(refused)).Should().Be(await Wire.Snapshot(missing));
    }

    [Fact]
    public async Task An_expired_person_token_sets_the_same_header_at_a_door_and_at_the_fallback()
    {
        await using var host = await StartAsync();
        var token = Wire.Person(lifetime: TimeSpan.FromMinutes(-2));

        using var door = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, Account, token));
        using var fallback = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, AccountMissing, token));

        door.Headers.Contains("Token-Expired").Should().BeTrue(
            "the person scheme runs in UseAuthentication for every path; the ADR accepts and records it");
        (await Wire.Snapshot(door)).Should().Be(await Wire.Snapshot(fallback));
    }

    [Fact]
    public async Task The_refusal_carries_no_challenge_and_no_body()
    {
        await using var host = await StartAsync();

        using var response = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, Account));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.Contains("WWW-Authenticate").Should().BeFalse();
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    // ---- people stay as they were ----------------------------------------------

    [Fact]
    public async Task A_service_token_at_an_ordinary_login_endpoint_is_a_401_as_before()
    {
        await using var host = await StartAsync();
        var service = await Wire.Issued("advisor");

        using var withService = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, "/people/me", service));
        using var withGarbage = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, "/people/me", Garbage));

        withService.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Wire.Snapshot(withService)).Should().Be(await Wire.Snapshot(withGarbage));
        (await withService.Content.ReadAsStringAsync()).Should().Contain("unauthorized");
    }

    [Fact]
    public async Task A_person_still_gets_in_at_the_login_endpoint()
    {
        await using var host = await StartAsync();

        using var response = await host.Client.SendAsync(
            Wire.Request(HttpMethod.Get, "/people/me", Wire.Person()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- the mapping refuses a door nobody could open ---------------------------

    [Fact]
    public async Task Mapping_a_door_with_no_callers_throws_at_the_mapping()
    {
        var act = async () => await StartAsync(s => s.Map = endpoints =>
            endpoints.MapGet("/internal/x", () => "x").RequireServiceCaller());

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Mapping_a_door_with_a_malformed_caller_throws_at_the_mapping()
    {
        var act = async () => await StartAsync(s => s.Map = endpoints =>
            endpoints.MapGet("/internal/x", () => "x").RequireServiceCaller("Advisor"));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_fallback_without_a_group_needs_a_prefix_because_a_root_catch_all_would_take_every_path()
    {
        var act = async () => await StartAsync(s => s.Map = endpoints => endpoints.MapServiceFallback());

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ---- helpers ---------------------------------------------------------------

    private static async Task AssertRefusalIsIndistinguishable(ServiceCallerHost host, string refusal)
    {
        string? token = refusal switch
        {
            "garbage" => Garbage,
            "expired" => Wire.Service(lifetime: TimeSpan.FromMinutes(-2)),
            "wrong-audience" => Wire.Service(audience: "billing"),
            "wrong-type" => Wire.Service(type: "JWT"),
            "person" => Wire.Person(),
            "person-with-client-id" => Wire.Person(clientIdClaim: "advisor"),
            "stranger" => Wire.Service("profile"),
            "wrong-issuer" => Wire.Service(issuer: "https://someone-else.test"),
            _ => null
        };

        foreach (var (method, path, body) in new (HttpMethod, string, string?)[]
                 {
                     (HttpMethod.Get, Account, null),
                     (HttpMethod.Post, Note, NoteBodyJson)
                 })
        {
            using var atDoor = Prepare(method, path, token, body, refusal);
            using var atMissing = Prepare(
                method, path + "/does-not-exist", token, body, refusal);

            using var doorResponse = await host.Client.SendAsync(atDoor);
            using var missingResponse = await host.Client.SendAsync(atMissing);

            doorResponse.StatusCode.Should().Be(HttpStatusCode.NotFound, refusal);
            (await Wire.Snapshot(doorResponse)).Should().Be(await Wire.Snapshot(missingResponse), refusal);
        }
    }

    private static HttpRequestMessage Prepare(
        HttpMethod method, string path, string? token, string? body, string refusal)
    {
        var request = Wire.Request(method, path, refusal is "two-headers" or "not-bearer" ? null : token, body);
        switch (refusal)
        {
            case "two-headers":
                request.Headers.TryAddWithoutValidation(
                    "Authorization", new[] { "Bearer " + Wire.Service(), "Bearer " + Wire.Service() });
                break;
            case "not-bearer":
                request.Headers.TryAddWithoutValidation("Authorization", "Basic " + Convert.ToBase64String("a:b"u8));
                break;
        }

        return request;
    }

    private static Task<ServiceCallerHost> StartAsync(Action<ServiceCallerHost.Settings>? configure = null) =>
        ServiceCallerHost.StartAsync(configure);
}
