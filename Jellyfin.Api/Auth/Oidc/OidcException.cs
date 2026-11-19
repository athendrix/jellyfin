using System;

namespace Jellyfin.Api.Auth.Oidc;

/// <summary>
/// An OpenID Connect login failed.
/// </summary>
public sealed class OidcException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OidcException"/> class.
    /// </summary>
    public OidcException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcException"/> class.
    /// </summary>
    /// <param name="message">The message, for the server log only.</param>
    public OidcException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcException"/> class.
    /// </summary>
    /// <param name="message">The message, for the server log only.</param>
    /// <param name="innerException">The cause.</param>
    public OidcException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcException"/> class.
    /// </summary>
    /// <param name="error">Why the login failed.</param>
    /// <param name="message">The message, for the server log only.</param>
    /// <param name="innerException">The cause.</param>
    public OidcException(OidcError error, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    /// <summary>
    /// Gets why the login failed.
    /// </summary>
    public OidcError Error { get; } = OidcError.InvalidRequest;
}
