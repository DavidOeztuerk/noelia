namespace Noelia.Core.Exceptions;

/// <summary>
/// Supplies the words an application shows for an error.
/// </summary>
/// <remarks>
/// <para>Noelia decides the <em>structure</em> of an error — which code it is,
/// whether it may be shown to a user at all, which help page explains it and
/// which actions might resolve it. It does not decide the <em>wording</em>,
/// because that is the application's: its language, its tone, its audience, and
/// in a regulated domain sometimes its legal department's.</para>
///
/// <para>Until 5.3.0 the shipped table held German sentences in a package whose
/// API, documentation and README are English. Any application not written for a
/// German-speaking audience showed its users a language they had not asked for,
/// and had no way to change it short of replacing the whole service.</para>
///
/// <para>Register one of these and every string below is yours. Register
/// nothing and the built-in English wording applies, which is the library's own
/// language and no claim about the user's.</para>
/// </remarks>
public interface IErrorTextProvider
{
    /// <summary>
    /// The sentence to show for <paramref name="errorCode"/>, or <c>null</c> to
    /// fall back to the built-in wording.
    /// </summary>
    /// <remarks>
    /// Returning <c>null</c> for a code you have not translated yet is the
    /// point: a half-finished translation should leave the untranslated half
    /// readable, not blank.
    /// </remarks>
    /// <param name="errorCode">The code, from <c>ErrorCodes</c>.</param>
    string? Message(string errorCode);

    /// <summary>
    /// The actions to suggest, or <c>null</c> to fall back.
    /// </summary>
    /// <remarks>
    /// Noelia names the actions as stable keys — <c>check-input</c>,
    /// <c>contact-support</c> — through <see cref="ErrorActions"/>. An
    /// application can translate those keys or return sentences of its own.
    /// </remarks>
    /// <param name="errorCode">The code, from <c>ErrorCodes</c>.</param>
    string[]? SuggestedActions(string errorCode);
}

/// <summary>
/// The action keys Noelia suggests, so a translation has something stable to
/// key on.
/// </summary>
/// <remarks>
/// Keys rather than sentences. A sentence in a library is a sentence somebody
/// has to override; a key is something they can look up.
/// </remarks>
public static class ErrorActions
{
    /// <summary>Check what was sent before sending it again.</summary>
    public const string CheckInput = "check-input";

    /// <summary>Check the address or identifier that was used.</summary>
    public const string CheckIdentifier = "check-identifier";

    /// <summary>Sign in, or sign in again.</summary>
    public const string SignIn = "sign-in";

    /// <summary>Wait, then try the same thing again.</summary>
    public const string WaitAndRetry = "wait-and-retry";

    /// <summary>Choose a different name or identifier.</summary>
    public const string ChooseAnotherName = "choose-another-name";

    /// <summary>Ask whoever owns the resource for access.</summary>
    public const string AskTheOwner = "ask-the-owner";

    /// <summary>Ask support, because this one is not self-service.</summary>
    public const string ContactSupport = "contact-support";

    /// <summary>Ask an administrator for the permission.</summary>
    public const string AskAnAdministrator = "ask-an-administrator";

    /// <summary>Complete the second factor.</summary>
    public const string CompleteSecondFactor = "complete-second-factor";

    /// <summary>Verify the account first.</summary>
    public const string VerifyAccount = "verify-account";

    /// <summary>Renew or upgrade the subscription.</summary>
    public const string RenewSubscription = "renew-subscription";

    /// <summary>Use a smaller file, or a different format.</summary>
    public const string UseADifferentFile = "use-a-different-file";

    /// <summary>Free space, or buy more.</summary>
    public const string FreeUpSpace = "free-up-space";

    /// <summary>Check the payment method.</summary>
    public const string CheckPaymentMethod = "check-payment-method";
}
