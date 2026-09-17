using Noelia.Cli;

namespace Noelia.Infrastructure.Tests.Architecture;

/// <summary>
/// What <c>noelia analyze</c> finds, against directories written for it.
/// </summary>
/// <remarks>
/// Each case is a small real project on disk rather than a mocked file system,
/// because the thing under test is a reader of files and the interesting
/// failures are about which files it decides to read.
/// </remarks>
[Trait("Category", "Unit")]
public class CliAnalysisTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "noelia-cli-" + Guid.NewGuid().ToString("N"));

    public CliAnalysisTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }

        GC.SuppressFinalize(this);
    }

    private void Write(string relative, string contents)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private const string Engine = """
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup>
            <PackageReference Include="Noelia.Infrastructure" Version="6.4.0" />
          </ItemGroup>
        </Project>
        """;

    [Fact]
    public void An_eagerly_registered_provider_is_reported_with_the_call_that_composes_it()
    {
        Write("App.csproj", Engine);
        Write("Program.cs", """
            services.AddRedisCache("app");
            services.AddNoelia(configuration, environment, "app", noelia => noelia.UseDefaults());
            app.UseNoelia(app.Environment, "app");
            services.AddSovereignPlatform(s => s.AllowLoopback());
            """);

        var result = ProjectScan.Run(_root);

        var finding = result.Findings.Single(f => f.Id == "noelia.provider.eager");

        finding.Severity.Should().Be(FindingSeverity.Problem);
        finding.What.Should().Contain("overwritten");
        finding.Do.Should().Contain("UseRedisCache",
            "a finding that names the defect without naming the call is a finding the reader "
            + "has to go and research");
    }

    [Fact]
    public void Composing_the_provider_properly_is_not_a_finding()
    {
        Write("App.csproj", Engine);
        Write("Program.cs", """
            services.AddNoelia(configuration, environment, "app", noelia => noelia
                .UseDefaults()
                .UseRedisCache("app"));
            app.UseNoelia(app.Environment, "app");
            services.AddSovereignPlatform(s => s.AllowLoopback());
            """);

        ProjectScan.Run(_root).Findings
            .Should().NotContain(finding => finding.Id == "noelia.provider.eager");
    }

    [Fact]
    public void A_composition_without_a_pipeline_is_a_problem_and_says_what_is_missing()
    {
        Write("App.csproj", Engine);
        Write("Program.cs", """
            services.AddNoelia(configuration, environment, "app", noelia => noelia.UseDefaults());
            services.AddSovereignPlatform(s => s.AllowLoopback());
            """);

        var finding = ProjectScan.Run(_root).Findings
            .Single(f => f.Id == "noelia.pipeline.absent");

        finding.Severity.Should().Be(FindingSeverity.Problem);
        finding.What.Should().Contain("rate limiting");
    }

    [Fact]
    public void A_credential_in_a_committed_file_is_reported_without_reproducing_it()
    {
        const string canary = "hunter2-do-not-print-me";

        Write("App.csproj", Engine);
        Write("appsettings.json", $$"""
            { "Operator": { "Secret": "{{canary}}" } }
            """);

        var result = ProjectScan.Run(_root);
        var finding = result.Findings.Single(f => f.Id == "noelia.configuration.inline-secret");

        finding.Where.Should().Contain("Operator:Secret");

        var everything = string.Join('\n', result.Findings
            .SelectMany(f => new[] { f.Where, f.What, f.Do })
            .Concat(result.Observations));

        everything.Should().NotContain(canary,
            "a scanner that prints the secret it found has published it to every log the "
            + "output reaches");
    }

    [Fact]
    public void A_placeholder_is_not_reported_as_a_credential()
    {
        Write("App.csproj", Engine);
        Write("appsettings.json", """
            {
              "Jwt": { "Secret": "${NOELIA_JWT_SECRET}" },
              "Redis": { "Password": "CHANGE-ME" }
            }
            """);

        ProjectScan.Run(_root).Findings
            .Should().NotContain(finding => finding.Id == "noelia.configuration.inline-secret",
                "a committed placeholder is the intended state of the file, and a scanner "
                + "that flags it teaches people to pass --no-verify");
    }

    [Fact]
    public void Build_output_is_not_scanned_twice()
    {
        Write("App.csproj", Engine);
        Write("appsettings.json", """{ "Noelia": { "ServiceName": "app" } }""");
        Write("bin/Release/net10.0/appsettings.json", """{ "Operator": { "Secret": "copied" } }""");

        ProjectScan.Run(_root).Findings
            .Should().NotContain(finding => finding.Where.Contains("bin"),
                "a copy under bin is the same file again, and sending a reader there sends "
                + "them somewhere they cannot fix it");
    }

    [Fact]
    public void Mixed_package_versions_are_a_problem()
    {
        Write("A/A.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Noelia.Infrastructure" Version="6.4.0" />
              </ItemGroup>
            </Project>
            """);

        Write("B/B.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Noelia.Redis" Version="6.2.0" />
              </ItemGroup>
            </Project>
            """);

        ProjectScan.Run(_root).Findings
            .Should().Contain(finding => finding.Id == "noelia.packages.version-skew");
    }

    [Fact]
    public void A_project_with_no_noelia_in_it_produces_nothing()
    {
        Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.0.0" />
              </ItemGroup>
            </Project>
            """);

        var result = ProjectScan.Run(_root);

        result.Findings.Should().BeEmpty(
            "a tool that finds something in every directory is one that gets uninstalled");
        result.ExitCode.Should().Be(0);
    }

    [Fact]
    public void The_exit_code_separates_findings_from_a_broken_run()
    {
        Write("App.csproj", Engine);
        Write("Program.cs", "services.AddNoelia(configuration, environment, \"app\", n => n);");

        var result = ProjectScan.Run(_root);

        result.Findings.Should().Contain(f => f.Severity == FindingSeverity.Problem);
        result.ExitCode.Should().Be(1,
            "0 is clean and 2 is the tool itself failing; a gate that cannot tell them apart "
            + "cannot tell a broken scanner from a failing project");
    }

    [Fact]
    public void Init_writes_nothing_on_a_dry_run_and_still_prints_the_composition_root()
    {
        Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup />
            </Project>
            """);

        var before = File.ReadAllText(Path.Combine(_root, "App.csproj"));

        using var output = new StringWriter();
        InitCommand.Run(_root, "billing", dryRun: true, output).Should().Be(0);

        File.ReadAllText(Path.Combine(_root, "App.csproj")).Should().Be(before);
        File.Exists(Path.Combine(_root, "appsettings.json")).Should().BeFalse();

        var text = output.ToString();
        text.Should().Contain("AddNoelia(");
        text.Should().Contain("UseNoelia(app.Environment, \"billing\")");
        text.Should().Contain("AddSovereignPlatform");
    }

    [Fact]
    public void Init_never_overwrites_a_section_that_is_already_configured()
    {
        Write("App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup /></Project>""");
        Write("appsettings.json", """{ "Dashboard": { "Fleet": "produktion" } }""");

        using var output = new StringWriter();
        InitCommand.Run(_root, "billing", dryRun: false, output);

        var settings = File.ReadAllText(Path.Combine(_root, "appsettings.json"));

        settings.Should().Contain("produktion",
            "the second run of a setup command is the one most likely to be an accident");
        output.ToString().Should().Contain("kept      Dashboard");
    }

    [Fact]
    public void Init_adds_the_packages_and_the_sections_that_are_missing()
    {
        Write("App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup /></Project>""");

        using var output = new StringWriter();
        InitCommand.Run(_root, "billing", dryRun: false, output).Should().Be(0);

        var project = File.ReadAllText(Path.Combine(_root, "App.csproj"));
        project.Should().Contain("Noelia.Infrastructure");
        project.Should().NotContain("Version=",
            "a version written into the csproj is an error where packages are managed "
            + "centrally, so the version is named in the instruction instead");

        var settings = File.ReadAllText(Path.Combine(_root, "appsettings.json"));
        settings.Should().Contain("\"ServiceName\": \"billing\"");
    }

    [Fact]
    public void Init_writes_no_value_that_looks_like_a_credential()
    {
        Write("App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup /></Project>""");

        using var output = new StringWriter();
        InitCommand.Run(_root, "billing", dryRun: false, output);

        var settings = File.ReadAllText(Path.Combine(_root, "appsettings.json"));

        // Its own analyzer, on its own output. A generated placeholder that
        // reads like a key is a key somebody ships.
        ProjectScan.Run(_root).Findings
            .Should().NotContain(finding => finding.Id == "noelia.configuration.inline-secret");

        settings.Should().Contain("\"Issuer\": \"\"");
    }
}
