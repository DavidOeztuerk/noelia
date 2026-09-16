using FluentAssertions;
using Noelia.Core.Exceptions;

namespace Noelia.Core.Tests.Exceptions;

/// <summary>
/// Noelia decides the structure of an error; the application decides the words.
/// </summary>
/// <remarks>
/// Until 5.3.0 the shipped table held German sentences in a package whose API,
/// documentation and README are English. Any application not written for a
/// German-speaking audience showed its users a language they had not asked for,
/// with no way to change it short of replacing the whole service.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class ErrorTextTests
{
    [Fact]
    public void The_shipped_wording_is_the_language_of_the_package()
    {
        var service = new ErrorMessageService();

        var message = service.GetUserMessage(ErrorCodes.Unauthorized);

        message.Should().Be("You need to sign in to do that.");
        message.Should().NotContainAny("Sie", "Bitte", "ä", "ö", "ü", "ß");
    }

    [Fact]
    public void An_application_that_supplies_wording_gets_its_own()
    {
        var service = new ErrorMessageService(text: new Dutch());

        service.GetUserMessage(ErrorCodes.Unauthorized).Should().Be("Log eerst in.");
    }

    [Fact]
    public void A_half_finished_translation_leaves_the_rest_readable()
    {
        var service = new ErrorMessageService(text: new Dutch());

        service.GetUserMessage(ErrorCodes.ResourceNotFound)
            .Should().StartWith("That item could not be found",
                "a code nobody has translated yet must not come back blank");
    }

    [Fact]
    public void Suggested_actions_are_keys_an_application_can_look_up()
    {
        var actions = new ErrorMessageService().GetSuggestedActions(ErrorCodes.ResourceNotFound);

        actions.Should().Equal(ErrorActions.CheckIdentifier, ErrorActions.AskTheOwner);
        actions.Should().OnlyContain(a => a == a.ToLowerInvariant() && !a.Contains(' '),
            "a key is something to look up; a sentence is something to override");
    }

    [Fact]
    public void An_application_can_replace_the_actions_too()
    {
        var service = new ErrorMessageService(text: new Dutch());

        service.GetSuggestedActions(ErrorCodes.Unauthorized).Should().Equal("Log in");
    }

    [Fact]
    public void The_structure_is_Noelias_and_a_text_provider_does_not_change_it()
    {
        var service = new ErrorMessageService(text: new Dutch());

        service.IsUserFacingError(ErrorCodes.InternalError).Should().BeFalse(
            "whether an error may be shown at all is a property of the error, "
            + "not of who is reading it");
        service.GetHelpUrl(ErrorCodes.Unauthorized).Should().EndWith("authentication");
    }

    /// <summary>Translates two codes, and nothing else.</summary>
    private sealed class Dutch : IErrorTextProvider
    {
        public string? Message(string errorCode) =>
            errorCode == ErrorCodes.Unauthorized ? "Log eerst in." : null;

        public string[]? SuggestedActions(string errorCode) =>
            errorCode == ErrorCodes.Unauthorized ? ["Log in"] : null;
    }
}
