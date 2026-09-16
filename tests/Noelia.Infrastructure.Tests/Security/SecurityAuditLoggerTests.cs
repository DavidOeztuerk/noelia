using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Noelia.Abstractions.Security.Audit;
using Noelia.Infrastructure.Security;

namespace Noelia.Infrastructure.Tests.Security;

/// <summary>
/// The front door for a security event, with and without a store behind it.
/// </summary>
/// <remarks>
/// The suite this replaces asserted <c>GetSecurityEventsAsync</c> returned an
/// empty sequence — it pinned the defect as the specification. It would have
/// passed with the method body deleted, which is the test CLAUDE.md warns
/// about: the reading half never worked, and a test said so approvingly.
/// </remarks>
[Trait("Category", "Unit")]
public class SecurityAuditLoggerTests
{
    private static SecurityAuditLogger Logger(ISecurityAuditService? store = null)
    {
        var services = new ServiceCollection();
        if (store is not null)
        {
            services.AddSingleton(store);
        }

        return new SecurityAuditLogger(
            Substitute.For<ILogger<SecurityAuditLogger>>(),
            services.BuildServiceProvider());
    }

    private static SecurityAuditEvent Event(string type = "LoginSuccess") => new()
    {
        EventType = type,
        Description = "User logged in",
        UserId = "user-1",
        Severity = SecurityEventSeverity.Information
    };

    /// <summary>
    /// The half that matters: an event has to reach the store.
    /// </summary>
    /// <remarks>
    /// Until 6.0.0 it reached the log stream and stopped, with a comment saying
    /// a real implementation would also store it. <c>Audit</c> is in
    /// <c>UseDefaults()</c>, so that was every Noelia service.
    /// </remarks>
    [Fact]
    public async Task A_recorded_event_reaches_the_registered_store()
    {
        var store = Substitute.For<ISecurityAuditService>();
        var auditEvent = Event();

        await Logger(store).LogSecurityEventAsync(auditEvent);

        await store.Received(1).LogSecurityEventAsync(auditEvent, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Recording_without_a_store_still_succeeds()
    {
        var act = () => Logger().LogSecurityEventAsync(Event());

        await act.Should().NotThrowAsync(
            "the Audit module composes with nothing else registered, so writing must not "
            + "depend on a provider having been chosen");
    }

    /// <summary>
    /// Reading nothing and reading from nowhere are different answers.
    /// </summary>
    [Fact]
    public async Task Reading_without_a_store_refuses_instead_of_reporting_none()
    {
        var act = () => Logger().GetSecurityEventsAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Noelia.Redis",
                "a caller who has to register something should be told what");
    }

    [Fact]
    public async Task Reading_with_a_store_returns_what_it_holds()
    {
        var stored = Event("PasswordChanged");
        var store = Substitute.For<ISecurityAuditService>();
        store.GetSecurityEventsAsync(Arg.Any<SecurityAuditQuery>(), Arg.Any<CancellationToken>())
            .Returns([stored]);

        var result = await Logger(store).GetSecurityEventsAsync();

        result.Should().ContainSingle().Which.EventType.Should().Be("PasswordChanged");
    }

    [Fact]
    public async Task The_filters_are_passed_to_the_store()
    {
        var store = Substitute.For<ISecurityAuditService>();
        store.GetSecurityEventsAsync(Arg.Any<SecurityAuditQuery>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await Logger(store).GetSecurityEventsAsync(
            fromDate: from, eventType: "LoginFailed", userId: "user-9");

        await store.Received(1).GetSecurityEventsAsync(
            Arg.Is<SecurityAuditQuery>(query =>
                query.FromDate == from
                && query.EventType == "LoginFailed"
                && query.UserId == "user-9"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// One severity scale, and this is the one.
    /// </summary>
    /// <remarks>
    /// The scale removed in 6.0.0 read Information, Warning, Error, Critical —
    /// and put <c>Critical</c> at 3 where this one puts it at 4. A value
    /// written on one scale and read on the other changed meaning without
    /// anything failing, which is why only one of them could survive.
    /// </remarks>
    [Theory]
    [InlineData(SecurityEventSeverity.Information, 0)]
    [InlineData(SecurityEventSeverity.Low, 1)]
    [InlineData(SecurityEventSeverity.Medium, 2)]
    [InlineData(SecurityEventSeverity.High, 3)]
    [InlineData(SecurityEventSeverity.Critical, 4)]
    public void The_severity_scale_is_the_one_from_Abstractions(
        SecurityEventSeverity severity, int expected)
    {
        ((int)severity).Should().Be(expected);
        new SecurityAuditEvent { Severity = severity }.Severity.Should().Be(severity);
    }

    [Fact]
    public void A_fresh_event_has_usable_defaults()
    {
        var evt = new SecurityAuditEvent();

        evt.Id.Should().NotBeNullOrEmpty();
        evt.EventType.Should().BeEmpty();
        evt.Severity.Should().Be(SecurityEventSeverity.Information);
        evt.Source.Should().Be("Noelia");
        evt.Metadata.Should().NotBeNull().And.BeEmpty();
    }
}
