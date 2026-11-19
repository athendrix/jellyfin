using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace Jellyfin.Api.Auth.Oidc;

/// <summary>
/// The OpenID Connect login settings, read from the <c>Oidc</c> configuration section.
/// </summary>
/// <remarks>
/// Set through environment variables, for example <c>JELLYFIN_Oidc__Issuer=https://auth.example.com</c>.
/// OpenID Connect is enabled when an issuer is set. Password login is then disabled unless
/// <c>Oidc:EnablePasswordLogin</c> is true.
/// </remarks>
public sealed class OidcSettings
{
    /// <summary>
    /// The path the identity provider redirects to after login, relative to the server base URL.
    /// </summary>
    public const string CallbackPath = "/charon-oidc-redirect";

    /// <summary>
    /// The configuration section holding the settings.
    /// </summary>
    public const string SectionName = "Oidc";

    private OidcSettings()
    {
    }

    /// <summary>
    /// Gets a value indicating whether OpenID Connect login is enabled.
    /// </summary>
    public bool Enabled { get; private init; }

    /// <summary>
    /// Gets a value indicating whether users may log in with a password.
    /// </summary>
    public bool PasswordLoginEnabled { get; private init; } = true;

    /// <summary>
    /// Gets the issuer of the identity provider.
    /// </summary>
    public Uri? Issuer { get; private init; }

    /// <summary>
    /// Gets the public URL of this server, including its base URL.
    /// </summary>
    public Uri? PublicUrl { get; private init; }

    /// <summary>
    /// Gets the client id registered with the identity provider. Defaults to the host name of <see cref="PublicUrl"/>.
    /// </summary>
    public string ClientId { get; private init; } = string.Empty;

    /// <summary>
    /// Gets the redirect URI registered with the identity provider.
    /// </summary>
    public Uri? RedirectUri { get; private init; }

    /// <summary>
    /// Gets the scopes requested from the identity provider.
    /// </summary>
    public string Scopes { get; private init; } = "openid profile";

    /// <summary>
    /// Gets the claim holding the Jellyfin user name.
    /// </summary>
    public string UsernameClaim { get; private init; } = "preferred_username";

    /// <summary>
    /// Gets the claim holding the groups of the user.
    /// </summary>
    public string GroupsClaim { get; private init; } = "groups";

    /// <summary>
    /// Gets the group whose members are administrators, if group mapping is used.
    /// </summary>
    public string? AdminGroup { get; private init; }

    /// <summary>
    /// Gets the group whose members may log in, if access is restricted to a group.
    /// </summary>
    public string? AllowedGroup { get; private init; }

    /// <summary>
    /// Gets the user names that are always administrators.
    /// </summary>
    public IReadOnlySet<string> AdminUsers { get; private init; } = new HashSet<string>();

    /// <summary>
    /// Gets a value indicating whether users that do not exist yet are created on their first login.
    /// </summary>
    public bool AutoCreateUsers { get; private init; } = true;

    /// <summary>
    /// Gets the name of the identity provider shown on the login button.
    /// </summary>
    public string ProviderName { get; private init; } = "SSO";

    /// <summary>
    /// Reads and validates the settings.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="InvalidOperationException">The settings are invalid.</exception>
    public static OidcSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var issuerValue = section["Issuer"];
        if (string.IsNullOrWhiteSpace(issuerValue))
        {
            // Not configured: behave exactly like a stock server.
            return new OidcSettings();
        }

        var issuer = ParseHttpsUrl(issuerValue, "Oidc:Issuer");

        var publicUrlValue = section["PublicUrl"];
        if (string.IsNullOrWhiteSpace(publicUrlValue))
        {
            throw new InvalidOperationException("Oidc:PublicUrl must be set to the public https URL of this server when Oidc:Issuer is set.");
        }

        var publicUrl = ParseHttpsUrl(publicUrlValue, "Oidc:PublicUrl");
        var clientId = NullIfBlank(section["ClientId"]) ?? publicUrl.Host;
        var redirectUri = new Uri(publicUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') + CallbackPath);

        var adminGroup = NullIfBlank(section["AdminGroup"]);
        var allowedGroup = NullIfBlank(section["AllowedGroup"]);

        // Group claims usually need the "groups" scope.
        var scopes = NullIfBlank(section["Scopes"])
            ?? (adminGroup is null && allowedGroup is null ? "openid profile" : "openid profile groups");
        if (!scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("openid", StringComparer.Ordinal))
        {
            scopes = "openid " + scopes;
        }

        var adminUsers = (section["AdminUsers"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new OidcSettings
        {
            Enabled = true,
            PasswordLoginEnabled = section.GetValue("EnablePasswordLogin", false),
            Issuer = issuer,
            PublicUrl = publicUrl,
            ClientId = clientId,
            RedirectUri = redirectUri,
            Scopes = scopes,
            UsernameClaim = NullIfBlank(section["UsernameClaim"]) ?? "preferred_username",
            GroupsClaim = NullIfBlank(section["GroupsClaim"]) ?? "groups",
            AdminGroup = adminGroup,
            AllowedGroup = allowedGroup,
            AdminUsers = adminUsers,
            AutoCreateUsers = section.GetValue("AutoCreateUsers", true),
            ProviderName = NullIfBlank(section["ProviderName"]) ?? "SSO"
        };
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Uri ParseHttpsUrl(string value, string key)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException($"{key} must be an absolute https URL without query, fragment or credentials.");
        }

        return uri;
    }
}
