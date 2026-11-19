namespace Jellyfin.Api.Auth.Oidc;

/// <summary>
/// The device a user logs in from with OpenID Connect.
/// </summary>
/// <param name="DeviceId">The device id.</param>
/// <param name="DeviceName">The device name.</param>
/// <param name="Client">The client application.</param>
/// <param name="Version">The client application version.</param>
public sealed record OidcDevice(string DeviceId, string DeviceName, string Client, string Version);
