namespace Noelia.Core.Exceptions;

/// <summary>
/// Service for providing user-friendly error messages and help information
/// </summary>
public class ErrorMessageService : IErrorMessageService
{
  /// <summary>Used when the caller supplies no base URL.</summary>
  public const string DefaultHelpUrl = "https://docs.noelia.dev/errors/";

  private readonly Dictionary<string, ErrorInfo> _errorMappings;
  private readonly string _baseHelpUrl;
  private readonly IErrorTextProvider? _text;

  /// <param name="baseHelpUrl">
  /// Where help pages live. Passed in rather than read from configuration, so
  /// that this layer needs no configuration provider.
  /// </param>
  /// <param name="text">
  /// Supplies the application's own wording. Without one the built-in English
  /// applies — see <see cref="IErrorTextProvider"/> for why that is a fallback
  /// and not a decision about the user's language.
  /// </param>
  public ErrorMessageService(string? baseHelpUrl = null, IErrorTextProvider? text = null)
  {
    _baseHelpUrl = string.IsNullOrWhiteSpace(baseHelpUrl) ? DefaultHelpUrl : baseHelpUrl;
    _text = text;
    _errorMappings = InitializeErrorMappings();
  }

  /// <summary>
  /// The application's wording where it has one, then Noelia's, then the
  /// caller's own default.
  /// </summary>
  /// <remarks>
  /// A provider returning <c>null</c> for one code and not another is not a
  /// mistake — it is a half-finished translation, and the untranslated half
  /// stays readable instead of going blank.
  /// </remarks>
  /// <param name="errorCode">The code, from <see cref="ErrorCodes"/>.</param>
  /// <param name="defaultMessage">Used for a code Noelia does not know.</param>
  public string GetUserMessage(string errorCode, string? defaultMessage = null)
  {
    if (_text?.Message(errorCode) is { Length: > 0 } supplied)
    {
      return supplied;
    }

    if (_errorMappings.TryGetValue(errorCode, out var errorInfo))
    {
      return errorInfo.UserMessage;
    }

    return defaultMessage ?? "Something went wrong.";
  }

  public string? GetHelpUrl(string errorCode)
  {
    if (_errorMappings.TryGetValue(errorCode, out var errorInfo) && !string.IsNullOrEmpty(errorInfo.HelpPath))
    {
      return $"{_baseHelpUrl}{errorInfo.HelpPath}";
    }

    return null;
  }

  /// <summary>
  /// The application's actions where it has them, otherwise Noelia's keys.
  /// </summary>
  /// <remarks>
  /// What Noelia returns are keys from <see cref="ErrorActions"/>, not
  /// sentences. An application that shows them to a user without translating
  /// them shows a key, which is visible immediately — better than a sentence in
  /// a language nobody chose, which is not.
  /// </remarks>
  /// <param name="errorCode">The code, from <see cref="ErrorCodes"/>.</param>
  public string[]? GetSuggestedActions(string errorCode)
  {
    if (_text?.SuggestedActions(errorCode) is { Length: > 0 } supplied)
    {
      return supplied;
    }

    if (_errorMappings.TryGetValue(errorCode, out var errorInfo))
    {
      return errorInfo.SuggestedActions;
    }

    return null;
  }

  public bool IsUserFacingError(string errorCode)
  {
    if (_errorMappings.TryGetValue(errorCode, out var errorInfo))
    {
      return errorInfo.IsUserFacing;
    }

    // Default to not showing technical errors to users
    return false;
  }

  /// <summary>
  /// The built-in wording, in English.
  /// </summary>
  /// <remarks>
  /// English because that is the language of this package — its API, its XML
  /// documentation, its README. It is the library's own language and no claim
  /// about the user's: an application that serves people in another one
  /// registers an <see cref="IErrorTextProvider"/> and every sentence here is
  /// replaced. Until 5.3.0 these were German, which was the same decision made
  /// once and never offered to anyone else.
  /// <para>
  /// The structure — which code exists, whether it may be shown at all, which
  /// help page explains it, which actions might resolve it — stays here,
  /// because that is a property of the error and not of the audience.
  /// </para>
  /// </remarks>
  private static Dictionary<string, ErrorInfo> InitializeErrorMappings() =>
    new()
    {
      // Domain
      [ErrorCodes.BusinessRuleViolation] = new ErrorInfo
      {
        UserMessage = "That action is not allowed by the rules of this system. Check what you sent and try again.",
        SuggestedActions = [ErrorActions.CheckInput, ErrorActions.ContactSupport]
      },
      [ErrorCodes.ResourceNotFound] = new ErrorInfo
      {
        UserMessage = "That item could not be found. It may have been deleted, or you may not have access to it.",
        HelpPath = "resource-not-found",
        SuggestedActions = [ErrorActions.CheckIdentifier, ErrorActions.AskTheOwner]
      },
      [ErrorCodes.ResourceAlreadyExists] = new ErrorInfo
      {
        UserMessage = "Something with that identifier already exists. Use a different one.",
        SuggestedActions = [ErrorActions.ChooseAnotherName]
      },
      [ErrorCodes.DeadlockDetected] = new ErrorInfo
      {
        UserMessage = "That could not be completed because something else changed the same data. Try again.",
        SuggestedActions = [ErrorActions.WaitAndRetry]
      },

      // Authentication and authorisation
      [ErrorCodes.Unauthorized] = new ErrorInfo
      {
        UserMessage = "You need to sign in to do that.",
        HelpPath = "authentication",
        SuggestedActions = [ErrorActions.SignIn]
      },
      [ErrorCodes.InsufficientPermissions] = new ErrorInfo
      {
        UserMessage = "Your account does not have permission to do that.",
        HelpPath = "permissions",
        SuggestedActions = [ErrorActions.AskAnAdministrator]
      },
      [ErrorCodes.TokenExpired] = new ErrorInfo
      {
        UserMessage = "Your session has ended. Sign in again to continue.",
        SuggestedActions = [ErrorActions.SignIn]
      },
      [ErrorCodes.InvalidCredentials] = new ErrorInfo
      {
        // Deliberately says neither which of the two was wrong. Telling a
        // caller that the address exists is telling them half the answer.
        UserMessage = "That email address and password do not match an account.",
        HelpPath = "login-issues",
        SuggestedActions = [ErrorActions.CheckInput]
      },
      [ErrorCodes.AccountNotVerified] = new ErrorInfo
      {
        UserMessage = "Confirm your email address to continue.",
        HelpPath = "email-verification",
        SuggestedActions = [ErrorActions.VerifyAccount]
      },
      [ErrorCodes.TwoFactorRequired] = new ErrorInfo
      {
        UserMessage = "That action needs your second factor.",
        HelpPath = "two-factor-auth",
        SuggestedActions = [ErrorActions.CompleteSecondFactor]
      },
      [ErrorCodes.MaxAttemptsExceeded] = new ErrorInfo
      {
        UserMessage = "Too many attempts. Wait before trying again.",
        HelpPath = "rate-limits",
        SuggestedActions = [ErrorActions.WaitAndRetry]
      },

      // Input
      [ErrorCodes.ValidationFailed] = new ErrorInfo
      {
        UserMessage = "Some of what you sent was not accepted. Check the fields and try again.",
        SuggestedActions = [ErrorActions.CheckInput]
      },
      [ErrorCodes.RequiredFieldMissing] = new ErrorInfo
      {
        UserMessage = "Something required was left out.",
        SuggestedActions = [ErrorActions.CheckInput]
      },
      [ErrorCodes.InvalidEmail] = new ErrorInfo
      {
        UserMessage = "That is not an email address this system can use.",
        SuggestedActions = [ErrorActions.CheckInput]
      },
      [ErrorCodes.InvalidPhoneNumber] = new ErrorInfo
      {
        UserMessage = "That is not a phone number this system can use.",
        SuggestedActions = [ErrorActions.CheckInput]
      },

      // Availability
      [ErrorCodes.ServiceUnavailable] = new ErrorInfo
      {
        UserMessage = "This is temporarily unavailable. Try again shortly.",
        HelpPath = "service-status",
        SuggestedActions = [ErrorActions.WaitAndRetry]
      },
      [ErrorCodes.ServiceTimeout] = new ErrorInfo
      {
        UserMessage = "That took too long to answer. Try again.",
        SuggestedActions = [ErrorActions.WaitAndRetry]
      },
      [ErrorCodes.RateLimitExceeded] = new ErrorInfo
      {
        UserMessage = "Too many requests. Wait a moment before sending more.",
        HelpPath = "rate-limits",
        SuggestedActions = [ErrorActions.WaitAndRetry]
      },
      [ErrorCodes.ConnectionTimeout] = new ErrorInfo
      {
        UserMessage = "The connection timed out. Check your network and try again.",
        SuggestedActions = [ErrorActions.WaitAndRetry]
      },
      [ErrorCodes.SslError] = new ErrorInfo
      {
        UserMessage = "The secure connection could not be established.",
        SuggestedActions = [ErrorActions.ContactSupport]
      },

      // Billing and storage
      [ErrorCodes.PaymentFailed] = new ErrorInfo
      {
        UserMessage = "That payment did not go through.",
        HelpPath = "payment-issues",
        SuggestedActions = [ErrorActions.CheckPaymentMethod]
      },
      [ErrorCodes.InsufficientBalance] = new ErrorInfo
      {
        UserMessage = "There is not enough balance for that.",
        SuggestedActions = [ErrorActions.CheckPaymentMethod]
      },
      [ErrorCodes.SubscriptionExpired] = new ErrorInfo
      {
        UserMessage = "That needs an active subscription.",
        HelpPath = "subscription",
        SuggestedActions = [ErrorActions.RenewSubscription]
      },
      [ErrorCodes.FileTooLarge] = new ErrorInfo
      {
        UserMessage = "That file is larger than this system accepts.",
        SuggestedActions = [ErrorActions.UseADifferentFile]
      },
      [ErrorCodes.InvalidFileType] = new ErrorInfo
      {
        UserMessage = "That kind of file is not accepted here.",
        SuggestedActions = [ErrorActions.UseADifferentFile]
      },
      [ErrorCodes.StorageQuotaExceeded] = new ErrorInfo
      {
        UserMessage = "You have used all the storage available to you.",
        HelpPath = "storage-limits",
        SuggestedActions = [ErrorActions.FreeUpSpace]
      },

      // Not shown to users. The wording exists for a log, not a screen.
      [ErrorCodes.InternalError] = new ErrorInfo
      {
        UserMessage = "Something went wrong that is not the caller's to fix.",
        IsUserFacing = false
      },
      [ErrorCodes.DatabaseError] = new ErrorInfo
      {
        UserMessage = "A data store refused the operation.",
        IsUserFacing = false
      }
    };

  private class ErrorInfo
  {
    public string UserMessage { get; set; } = string.Empty;
    public bool IsUserFacing { get; set; } = true;
    public string? HelpPath { get; set; }
    public string[]? SuggestedActions { get; set; }
  }
}
