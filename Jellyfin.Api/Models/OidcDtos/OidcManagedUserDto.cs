namespace Jellyfin.Api.Models.OidcDtos;

/// <summary>
/// Which settings of a user are managed by the identity provider.
/// </summary>
public class OidcManagedUserDto
{
    /// <summary>
    /// Gets or sets a value indicating whether the user is linked to an identity provider account.
    /// </summary>
    public bool Managed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user name comes from the identity provider.
    /// </summary>
    public bool Name { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether administrator rights come from the identity provider.
    /// </summary>
    public bool Administrator { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether library access comes from the identity provider's groups.
    /// </summary>
    public bool Libraries { get; set; }
}
