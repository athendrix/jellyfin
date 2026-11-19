namespace Jellyfin.Api.Models.OidcDtos;

/// <summary>
/// The one-time code of a completed OpenID Connect login.
/// </summary>
public class OidcRedeemDto
{
    /// <summary>
    /// Gets or sets the one-time code.
    /// </summary>
    public string? Code { get; set; }
}
