namespace Jellyfin.Api.Models.OidcDtos;

/// <summary>
/// How users log in to this server.
/// </summary>
public class OidcConfigDto
{
    /// <summary>
    /// Gets or sets a value indicating whether users can log in with OpenID Connect.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether users can log in with a password.
    /// </summary>
    public bool PasswordLoginEnabled { get; set; }

    /// <summary>
    /// Gets or sets the name of the identity provider, shown on the login button.
    /// </summary>
    public string? ProviderName { get; set; }
}
