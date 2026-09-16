using System.Text.Json;
using Noelia.Core.Identity;
using Noelia.Core.Logging;

namespace Noelia.Core.Probe;

public sealed class CorePackageTests
{
    private const string Canary = "CORE-PROBE-CANARY-MUST-NOT-LEAK";

    [Fact]
    public void Typed_subject_id_roundtrips_as_a_plain_json_string()
    {
        var subject = SubjectId.New();

        var json = JsonSerializer.Serialize(subject);
        var roundtrip = JsonSerializer.Deserialize<SubjectId>(json);

        roundtrip.Should().Be(subject);
        json.Should().Be($"\"{subject}\"");
    }

    [Fact]
    public void Log_sanitizer_removes_named_secret_values()
    {
        var sanitized = new LogSanitizer().Sanitize(new { Password = Canary, Note = "shape only" });
        var serialized = JsonSerializer.Serialize(sanitized);

        serialized.Should().NotContain(Canary);
        serialized.Should().Contain("[REDACTED]");
        serialized.Should().Contain("shape only");
    }
}
