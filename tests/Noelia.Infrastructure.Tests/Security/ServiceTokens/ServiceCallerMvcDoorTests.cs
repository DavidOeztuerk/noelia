using System.Net;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceCallerHost;

namespace Noelia.Infrastructure.Tests.Security.ServiceTokens;

/// <summary>
/// A controller action carries the same door as a minimal API: an attribute, and a
/// fallback mapped with an explicit prefix, because controllers have no group.
/// </summary>
[Trait("Category", "Unit")]
[Collection(ServiceCallerHttpCollection.Name)]
public class ServiceCallerMvcDoorTests
{
    [Fact]
    public async Task A_listed_caller_reaches_the_action()
    {
        await using var host = await StartAsync(s => s.Controllers = true);

        using var response = await host.Client.SendAsync(
            Wire.Request(HttpMethod.Get, "/internal-mvc/ping", await Wire.Issued("advisor")));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("pong");
    }

    [Fact]
    public async Task A_bound_body_is_reached_by_the_listed_caller()
    {
        await using var host = await StartAsync(s => s.Controllers = true);

        using var response = await host.Client.SendAsync(Wire.Request(
            HttpMethod.Post, "/internal-mvc/note", await Wire.Issued("notification"), "{\"text\":\"hi\"}"));

        (await response.Content.ReadAsStringAsync()).Should().Be("note hi");
    }

    [Theory]
    [InlineData("none")]
    [InlineData("garbage")]
    [InlineData("other-caller")]
    [InlineData("person")]
    [InlineData("expired")]
    public async Task Everyone_else_gets_the_answer_of_the_fallback(string who)
    {
        await using var host = await StartAsync(s => s.Controllers = true);
        var token = who switch
        {
            "garbage" => Garbage,
            "other-caller" => await Wire.Issued("notification"),
            "person" => Wire.Person(),
            "expired" => Wire.Service(lifetime: TimeSpan.FromMinutes(-2)),
            _ => null
        };

        using var door = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, "/internal-mvc/ping", token));
        using var missing = await host.Client.SendAsync(Wire.Request(HttpMethod.Get, "/internal-mvc/ping/no", token));

        door.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Wire.Snapshot(door)).Should().Be(await Wire.Snapshot(missing));
    }

    [Fact]
    public async Task A_broken_body_from_a_stranger_is_a_404_and_never_a_400()
    {
        await using var host = await StartAsync(s => s.Controllers = true);
        var token = await Wire.Issued("advisor");

        using var door = await host.Client.SendAsync(
            Wire.Request(HttpMethod.Post, "/internal-mvc/note", token, "{broken"));
        using var missing = await host.Client.SendAsync(
            Wire.Request(HttpMethod.Post, "/internal-mvc/note/no", token, "{broken"));

        door.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Wire.Snapshot(door)).Should().Be(await Wire.Snapshot(missing));
    }

    [Fact]
    public async Task The_wrong_method_on_the_action_is_a_404_and_never_a_405()
    {
        await using var host = await StartAsync(s => s.Controllers = true);
        var token = await Wire.Issued("advisor");

        using var door = await host.Client.SendAsync(Wire.Request(HttpMethod.Delete, "/internal-mvc/ping", token));
        using var missing = await host.Client.SendAsync(Wire.Request(HttpMethod.Delete, "/internal-mvc/ping/no", token));

        door.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Wire.Snapshot(door)).Should().Be(await Wire.Snapshot(missing));
    }

    private static Task<ServiceCallerHost> StartAsync(Action<Settings> configure) =>
        ServiceCallerHost.StartAsync(configure);
}
