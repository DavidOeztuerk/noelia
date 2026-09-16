using Noelia.Core.Exceptions;
using FluentAssertions;

namespace Shared.Tests.Exceptions;

public class ErrorMessageServiceTests
{
    private readonly ErrorMessageService _sut = new();

    #region GetUserMessage

    [Fact]
    public void GetUserMessage_KnownCode_ReturnsUserMessage()
    {
        var message = _sut.GetUserMessage(ErrorCodes.ResourceNotFound);

        message.Should().NotBeNullOrEmpty();
        message.Should().Contain("could not be found");
    }

    [Fact]
    public void GetUserMessage_UnknownCode_ReturnsDefaultFallback()
    {
        var message = _sut.GetUserMessage("ERR_UNKNOWN");

        message.Should().Be("Something went wrong.");
    }

    [Fact]
    public void GetUserMessage_UnknownCode_WithCustomDefault_ReturnsCustom()
    {
        var message = _sut.GetUserMessage("ERR_UNKNOWN", "Custom fallback");

        message.Should().Be("Custom fallback");
    }

    [Theory]
    [InlineData(ErrorCodes.InvalidCredentials)]
    [InlineData(ErrorCodes.ValidationFailed)]
    [InlineData(ErrorCodes.PaymentFailed)]
    [InlineData(ErrorCodes.FileTooLarge)]
    [InlineData(ErrorCodes.InsufficientBalance)]
    public void GetUserMessage_CommonCodes_ReturnsNonEmptyMessage(string errorCode)
    {
        var message = _sut.GetUserMessage(errorCode);

        message.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region GetHelpUrl

    [Fact]
    public void GetHelpUrl_KnownCode_ReturnsUrl()
    {
        var url = _sut.GetHelpUrl(ErrorCodes.ResourceNotFound);

        url.Should().NotBeNull();
        url.Should().StartWith(Noelia.Core.Exceptions.ErrorMessageService.DefaultHelpUrl);
    }

    [Fact]
    public void GetHelpUrl_UnknownCode_ReturnsNull()
    {
        var url = _sut.GetHelpUrl("ERR_UNKNOWN");

        url.Should().BeNull();
    }

    [Fact]
    public void GetHelpUrl_CodeWithoutHelpPath_ReturnsNull()
    {
        // BusinessRuleViolation has no HelpPath defined
        var url = _sut.GetHelpUrl(ErrorCodes.BusinessRuleViolation);

        url.Should().BeNull();
    }

    #endregion

    #region GetSuggestedActions

    [Fact]
    public void GetSuggestedActions_KnownCode_ReturnsActions()
    {
        var actions = _sut.GetSuggestedActions(ErrorCodes.InvalidCredentials);

        actions.Should().NotBeNull();
        actions.Should().NotBeEmpty();
    }

    [Fact]
    public void GetSuggestedActions_UnknownCode_ReturnsNull()
    {
        var actions = _sut.GetSuggestedActions("ERR_UNKNOWN");

        actions.Should().BeNull();
    }

    #endregion

    #region IsUserFacingError

    [Theory]
    [InlineData(ErrorCodes.InvalidCredentials, true)]
    [InlineData(ErrorCodes.ResourceNotFound, true)]
    [InlineData(ErrorCodes.ValidationFailed, true)]
    [InlineData(ErrorCodes.PaymentFailed, true)]
    public void IsUserFacingError_UserFacingCodes_ReturnsTrue(string errorCode, bool expected)
    {
        _sut.IsUserFacingError(errorCode).Should().Be(expected);
    }

    [Theory]
    [InlineData(ErrorCodes.DatabaseError)]
    [InlineData(ErrorCodes.InternalError)]
    public void IsUserFacingError_SystemCodes_ReturnsFalse(string errorCode)
    {
        _sut.IsUserFacingError(errorCode).Should().BeFalse();
    }

    [Fact]
    public void IsUserFacingError_UnknownCode_ReturnsFalse()
    {
        _sut.IsUserFacingError("ERR_UNKNOWN").Should().BeFalse();
    }

    #endregion
}
