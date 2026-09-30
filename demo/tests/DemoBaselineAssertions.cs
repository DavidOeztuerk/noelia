using System.Text.Json;
using FluentAssertions;

namespace Demo.TestSupport;

internal static class DemoBaselineAssertions
{
    internal static async Task MatchAsync(HttpClient client, string role)
    {
        using var baselines = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "composition-baselines.json")));
        baselines.RootElement.GetProperty("SchemaVersion").GetInt32().Should().Be(1);
        var expected = baselines.RootElement.GetProperty("Roles").EnumerateArray()
            .Single(entry => entry.GetProperty("Name").GetString() == role)
            .GetProperty("Modules").EnumerateArray().Select(module => module.GetString()!).ToArray();
        expected.Should().NotBeEmpty().And.OnlyHaveUniqueItems();
        using var response = await client.GetAsync("/noelia/report.json");
        response.EnsureSuccessStatusCode();
        using var report = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var running = report.RootElement.GetProperty("composition").GetProperty("modules").EnumerateArray()
            .Where(module => module.GetProperty("isRunning").GetBoolean())
            .Select(module => module.GetProperty("name").GetString()!).ToArray();
        running.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(expected,
            "the reviewed role baseline must describe the actual composed host, not other service roles");
    }
}
