namespace Jellyfin.Api.Auth.Oidc;

/// <summary>
/// Why an OpenID Connect login failed. Sent to the web client, which shows a matching message.
/// </summary>
public enum OidcError
{
    /// <summary>
    /// OpenID Connect login is not enabled.
    /// </summary>
    NotEnabled,

    /// <summary>
    /// The login request is invalid.
    /// </summary>
    InvalidRequest,

    /// <summary>
    /// Too many logins are in progress.
    /// </summary>
    TooManyLogins,

    /// <summary>
    /// The identity provider cannot be reached or is misconfigured.
    /// </summary>
    ProviderUnavailable,

    /// <summary>
    /// The identity provider reported an error, for example because the user cancelled.
    /// </summary>
    ProviderError,

    /// <summary>
    /// The login was not started from this browser, or has expired.
    /// </summary>
    InvalidState,

    /// <summary>
    /// The identity provider response is invalid.
    /// </summary>
    InvalidResponse,

    /// <summary>
    /// The ID token is invalid.
    /// </summary>
    InvalidToken,

    /// <summary>
    /// The ID token has no user name.
    /// </summary>
    MissingUsername,

    /// <summary>
    /// The user name is not a valid Jellyfin user name.
    /// </summary>
    InvalidUsername,

    /// <summary>
    /// The user may not log in.
    /// </summary>
    AccessDenied
}
