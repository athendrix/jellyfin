using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Jellyfin.Api.Auth.Oidc;

/// <summary>
/// Runs the OpenID Connect authorization code flow with PKCE, and turns a verified identity into a Jellyfin session.
/// </summary>
public sealed class OidcService : IDisposable
{
    /// <summary>
    /// How long a login may take at the identity provider.
    /// </summary>
    internal static readonly TimeSpan LoginLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long the web client has to pick up the session after the identity provider redirected back.
    /// </summary>
    internal static readonly TimeSpan HandoffLifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The most logins that may be in progress at once, to bound memory use.
    /// </summary>
    private const int MaxPendingLogins = 10000;

    /// <summary>
    /// The most logins one address may have in progress at once, so one client cannot block everyone's logins.
    /// </summary>
    private const int MaxPendingLoginsPerAddress = 20;

    private const int MaxFieldLength = 256;

    private readonly OidcSettings _settings;
    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly INetworkManager _networkManager;
    private readonly OidcLinkStore _links;
    private readonly ILogger<OidcService> _logger;
    private readonly Timer _sweepTimer;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _configurationManager;
    private readonly HttpClient? _metadataClient;
    private readonly JsonWebTokenHandler _tokenHandler = new();
    private readonly ConcurrentDictionary<string, PendingLogin> _pendingLogins = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Handoff> _handoffs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _userLock = new(1, 1);
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcService"/> class.
    /// </summary>
    /// <param name="settings">The OpenID Connect settings.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="networkManager">The network manager.</param>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public OidcService(
        OidcSettings settings,
        IUserManager userManager,
        ISessionManager sessionManager,
        IHttpClientFactory httpClientFactory,
        INetworkManager networkManager,
        IApplicationPaths applicationPaths,
        ILogger<OidcService> logger)
        : this(
            settings,
            userManager,
            sessionManager,
            httpClientFactory,
            networkManager,
            new OidcLinkStore(Path.Combine(applicationPaths.DataPath, "oidc-links.json"), logger),
            logger,
            TimeProvider.System,
            null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcService"/> class.
    /// </summary>
    /// <param name="settings">The OpenID Connect settings.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="networkManager">The network manager.</param>
    /// <param name="links">The account links.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="configurationManager">The provider metadata source, or null to fetch it from the issuer.</param>
    internal OidcService(
        OidcSettings settings,
        IUserManager userManager,
        ISessionManager sessionManager,
        IHttpClientFactory httpClientFactory,
        INetworkManager networkManager,
        OidcLinkStore links,
        ILogger<OidcService> logger,
        TimeProvider timeProvider,
        IConfigurationManager<OpenIdConnectConfiguration>? configurationManager)
    {
        _settings = settings;
        _userManager = userManager;
        _sessionManager = sessionManager;
        _httpClientFactory = httpClientFactory;
        _networkManager = networkManager;
        _links = links;
        _logger = logger;
        // Revokes the sessions of logins that were never picked up, without waiting for the next login.
        _sweepTimer = new Timer(_ => RemoveExpired(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        _timeProvider = timeProvider;
        ConfigurationSource = configurationManager;

        if (settings.Enabled && configurationManager is null)
        {
            _metadataClient = httpClientFactory.CreateClient(NamedClient.Default);
            var metadataAddress = settings.Issuer!.OriginalString.TrimEnd('/') + "/.well-known/openid-configuration";
            _configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever(_metadataClient) { RequireHttps = true });
            ConfigurationSource = _configurationManager;
        }
    }

    /// <summary>
    /// Gets the OpenID Connect settings.
    /// </summary>
    public OidcSettings Settings => _settings;

    private IConfigurationManager<OpenIdConnectConfiguration>? ConfigurationSource { get; }

    /// <summary>
    /// Starts a login: remembers the device and builds the authorization request to send the user to.
    /// </summary>
    /// <param name="device">The device logging in.</param>
    /// <param name="remoteEndPoint">The address of the client.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The state to bind to the browser, and the URL of the identity provider to redirect to.</returns>
    public async Task<(string State, Uri AuthorizationUrl)> StartLoginAsync(OidcDevice device, string remoteEndPoint, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        ValidateDevice(device);

        var metadata = await ConfigurationSource!.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(metadata.AuthorizationEndpoint)
            || !Uri.TryCreate(metadata.AuthorizationEndpoint, UriKind.Absolute, out var authorizationEndpoint)
            || authorizationEndpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new OidcException(OidcError.ProviderUnavailable, "The identity provider did not publish an https authorization endpoint.");
        }

        RemoveExpired();
        if (_pendingLogins.Count >= MaxPendingLogins
            || _pendingLogins.Values.Count(l => string.Equals(l.RemoteEndPoint, remoteEndPoint, StringComparison.Ordinal)) >= MaxPendingLoginsPerAddress)
        {
            throw new OidcException(OidcError.TooManyLogins, $"Too many logins are in progress from {remoteEndPoint}.");
        }

        var state = NewSecret();
        var nonce = NewSecret();
        var codeVerifier = NewSecret();
        var codeChallenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

        _pendingLogins[state] = new PendingLogin(nonce, codeVerifier, device, remoteEndPoint, _timeProvider.GetUtcNow());

        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = _settings.ClientId,
            ["redirect_uri"] = _settings.RedirectUri!.AbsoluteUri,
            ["scope"] = _settings.Scopes,
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };

        var separator = string.IsNullOrEmpty(authorizationEndpoint.Query) ? '?' : '&';
        var url = authorizationEndpoint.AbsoluteUri + separator + string.Join(
            '&',
            query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

        return (state, new Uri(url));
    }

    /// <summary>
    /// Completes a login: exchanges the authorization code, verifies the identity, and opens a session.
    /// </summary>
    /// <param name="state">The state returned by the identity provider.</param>
    /// <param name="browserState">The state bound to the browser when the login started.</param>
    /// <param name="code">The authorization code.</param>
    /// <param name="remoteEndPoint">The address of the client.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A one-time code the web client exchanges for the session.</returns>
    public async Task<string> CompleteLoginAsync(string? state, string? browserState, string? code, string remoteEndPoint, CancellationToken cancellationToken)
    {
        EnsureEnabled();

        if (string.IsNullOrEmpty(state) || string.IsNullOrEmpty(code))
        {
            throw new OidcException(OidcError.InvalidResponse, "The identity provider response is missing the state or the code.");
        }

        // The state must also be the one this browser started with. Otherwise somebody could send a victim a
        // link completing the attacker's own login, signing the victim in to the attacker's account.
        if (string.IsNullOrEmpty(browserState)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(browserState)))
        {
            throw new OidcException(OidcError.InvalidState, "The login was not started from this browser.");
        }

        if (!_pendingLogins.TryRemove(state, out var pending) || IsExpired(pending.CreatedAt, LoginLifetime))
        {
            throw new OidcException(OidcError.InvalidState, "The login is unknown or has expired.");
        }

        var tokens = await ExchangeCodeAsync(code, pending.CodeVerifier, cancellationToken).ConfigureAwait(false);
        var identity = await ValidateIdTokenAsync(tokens.IdToken!, pending.Nonce, cancellationToken).ConfigureAwait(false);
        await AddUserInfoClaimsAsync(identity, tokens.AccessToken, cancellationToken).ConfigureAwait(false);

        var user = await GetOrCreateUserAsync(identity, remoteEndPoint).ConfigureAwait(false);

        AuthenticationResult result;
        try
        {
            result = await _sessionManager.AuthenticateDirect(new AuthenticationRequest
            {
                UserId = user.Id,
                App = pending.Device.Client,
                AppVersion = pending.Device.Version,
                DeviceId = pending.Device.DeviceId,
                DeviceName = pending.Device.DeviceName,
                RemoteEndPoint = remoteEndPoint
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AuthenticationException or System.Security.SecurityException)
        {
            throw new OidcException(OidcError.AccessDenied, "The session could not be opened: " + ex.Message, ex);
        }

        _logger.LogInformation("User {UserName} logged in with OpenID Connect from {RemoteEndPoint}.", user.Username, remoteEndPoint);

        RemoveExpired();
        var handoffCode = NewSecret();
        _handoffs[handoffCode] = new Handoff(result, pending.Device.DeviceId, _timeProvider.GetUtcNow());
        return handoffCode;
    }

    /// <summary>
    /// Exchanges a one-time code for the session it stands for.
    /// </summary>
    /// <param name="handoffCode">The one-time code.</param>
    /// <param name="deviceId">The device id of the client redeeming the code.</param>
    /// <returns>The session, or null when the code is unknown, expired, or was issued to another device.</returns>
    public AuthenticationResult? Redeem(string? handoffCode, string? deviceId)
    {
        if (string.IsNullOrEmpty(handoffCode) || !_handoffs.TryRemove(handoffCode, out var handoff))
        {
            return null;
        }

        if (IsExpired(handoff.CreatedAt, HandoffLifetime)
            || !string.Equals(handoff.DeviceId, deviceId, StringComparison.Ordinal))
        {
            // The token was created for this login only: revoke it rather than leave it unused.
            _ = _sessionManager.Logout(handoff.Result.AccessToken);
            return null;
        }

        return handoff.Result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _sweepTimer.Dispose();
        _metadataClient?.Dispose();
        _userLock.Dispose();
    }

    internal static string NewSecret() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    private static void ValidateDevice(OidcDevice device)
    {
        static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= MaxFieldLength;

        if (!Valid(device.DeviceId) || !Valid(device.DeviceName) || !Valid(device.Client) || !Valid(device.Version))
        {
            throw new OidcException(OidcError.InvalidRequest, "The client, device, device id and version are required.");
        }
    }

    private void EnsureEnabled()
    {
        if (!_settings.Enabled || ConfigurationSource is null)
        {
            throw new OidcException(OidcError.NotEnabled, "OpenID Connect login is not enabled.");
        }
    }

    private bool IsExpired(DateTimeOffset createdAt, TimeSpan lifetime)
        => _timeProvider.GetUtcNow() - createdAt > lifetime;

    private void RemoveExpired()
    {
        foreach (var (key, login) in _pendingLogins)
        {
            if (IsExpired(login.CreatedAt, LoginLifetime))
            {
                _pendingLogins.TryRemove(key, out _);
            }
        }

        foreach (var (key, handoff) in _handoffs)
        {
            if (IsExpired(handoff.CreatedAt, HandoffLifetime) && _handoffs.TryRemove(key, out _))
            {
                _ = _sessionManager.Logout(handoff.Result.AccessToken);
            }
        }
    }

    private async Task<TokenResponse> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken cancellationToken)
    {
        var metadata = await ConfigurationSource!.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        if (!Uri.TryCreate(metadata.TokenEndpoint, UriKind.Absolute, out var tokenEndpoint) || tokenEndpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new OidcException(OidcError.ProviderUnavailable, "The identity provider did not publish an https token endpoint.");
        }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _settings.RedirectUri!.AbsoluteUri,
            ["client_id"] = _settings.ClientId,
            ["code_verifier"] = codeVerifier
        });

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        TokenResponse? tokens;
        try
        {
            using var response = await client.PostAsync(tokenEndpoint, content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // The body may echo the code, so it is not logged.
                throw new OidcException(OidcError.InvalidResponse, $"The identity provider refused the authorization code ({(int)response.StatusCode}).");
            }

            tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            throw new OidcException(OidcError.ProviderUnavailable, "The identity provider token endpoint could not be reached.", ex);
        }

        if (string.IsNullOrEmpty(tokens?.IdToken))
        {
            throw new OidcException(OidcError.InvalidResponse, "The identity provider did not return an ID token.");
        }

        return tokens;
    }

    /// <summary>
    /// Adds the claims the ID token lacks (the user name, the groups) from the userinfo endpoint.
    /// </summary>
    /// <remarks>
    /// Many providers only put profile and group claims in the userinfo response. Its claims are trusted because it
    /// is fetched from the provider over https with the access token issued alongside the verified ID token, and its
    /// subject must match the ID token's.
    /// </remarks>
    private async Task AddUserInfoClaimsAsync(ClaimsIdentity identity, string? accessToken, CancellationToken cancellationToken)
    {
        var needsUsername = identity.FindFirst(_settings.UsernameClaim) is null;
        var needsGroups = (_settings.AdminGroup is not null || _settings.AllowedGroup is not null)
            && identity.FindFirst(_settings.GroupsClaim) is null;
        if (!needsUsername && !needsGroups)
        {
            return;
        }

        if (string.IsNullOrEmpty(accessToken))
        {
            _logger.LogWarning("The ID token lacks claims the login needs, and the identity provider returned no access token to ask its userinfo endpoint with.");
            return;
        }

        var metadata = await ConfigurationSource!.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        if (!Uri.TryCreate(metadata.UserInfoEndpoint, UriKind.Absolute, out var userInfoEndpoint) || userInfoEndpoint.Scheme != Uri.UriSchemeHttps)
        {
            _logger.LogWarning("The ID token lacks claims the login needs, and the identity provider publishes no https userinfo endpoint.");
            return;
        }

        System.Text.Json.JsonElement userInfo;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, userInfoEndpoint);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("The identity provider userinfo endpoint answered {StatusCode}.", (int)response.StatusCode);
                return;
            }

            userInfo = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "Could not read the identity provider userinfo endpoint.");
            return;
        }

        // The userinfo response must be about the user the ID token is about.
        var subject = identity.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (userInfo.ValueKind != System.Text.Json.JsonValueKind.Object
            || !userInfo.TryGetProperty("sub", out var userInfoSubject)
            || userInfoSubject.ValueKind != System.Text.Json.JsonValueKind.String
            || !string.Equals(userInfoSubject.GetString(), subject, StringComparison.Ordinal))
        {
            throw new OidcException(OidcError.InvalidResponse, "The userinfo response is about another user than the ID token.");
        }

        if (needsUsername
            && userInfo.TryGetProperty(_settings.UsernameClaim, out var username)
            && username.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            identity.AddClaim(new Claim(_settings.UsernameClaim, username.GetString()!));
        }
        else if (needsUsername)
        {
            // Only the names, never the values, which can be personal data.
            _logger.LogWarning(
                "The userinfo response has no string '{UsernameClaim}' claim either. Its claims are: {Claims}.",
                _settings.UsernameClaim,
                string.Join(", ", userInfo.EnumerateObject().Select(p => $"{p.Name} ({p.Value.ValueKind})")));
        }

        if (needsGroups && userInfo.TryGetProperty(_settings.GroupsClaim, out var groups))
        {
            if (groups.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var group in groups.EnumerateArray().Where(g => g.ValueKind == System.Text.Json.JsonValueKind.String))
                {
                    identity.AddClaim(new Claim(_settings.GroupsClaim, group.GetString()!));
                }
            }
            else if (groups.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                identity.AddClaim(new Claim(_settings.GroupsClaim, groups.GetString()!));
            }
        }
    }

    private async Task<ClaimsIdentity> ValidateIdTokenAsync(string idToken, string nonce, CancellationToken cancellationToken)
    {
        var result = await ValidateWithCurrentKeysAsync(idToken, cancellationToken).ConfigureAwait(false);
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // The provider may have rotated its keys since they were fetched.
            ConfigurationSource!.RequestRefresh();
            result = await ValidateWithCurrentKeysAsync(idToken, cancellationToken).ConfigureAwait(false);
        }

        if (!result.IsValid)
        {
            throw new OidcException(OidcError.InvalidToken, "The ID token is not valid.", result.Exception);
        }

        var identity = result.ClaimsIdentity;
        var tokenNonce = identity.FindFirst("nonce")?.Value;
        if (tokenNonce is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(tokenNonce), Encoding.UTF8.GetBytes(nonce)))
        {
            throw new OidcException(OidcError.InvalidToken, "The ID token nonce does not match the login.");
        }

        // With several audiences, the token must say it was issued to this client.
        var audiences = identity.FindAll(JwtRegisteredClaimNames.Aud).Select(c => c.Value).ToList();
        var authorizedParty = identity.FindFirst(JwtRegisteredClaimNames.Azp)?.Value;
        if ((audiences.Count > 1 || authorizedParty is not null)
            && !string.Equals(authorizedParty, _settings.ClientId, StringComparison.Ordinal))
        {
            throw new OidcException(OidcError.InvalidToken, "The ID token was issued to another client.");
        }

        return identity;
    }

    private async Task<TokenValidationResult> ValidateWithCurrentKeysAsync(string idToken, CancellationToken cancellationToken)
    {
        var metadata = await ConfigurationSource!.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var issuer = _settings.Issuer!.OriginalString;

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            // Tolerate only a trailing slash difference between the configured and the published issuer.
            ValidIssuers = new[] { issuer.TrimEnd('/'), issuer.TrimEnd('/') + "/" },
            ValidateAudience = true,
            ValidAudience = _settings.ClientId,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = metadata.SigningKeys,
            ClockSkew = TimeSpan.FromMinutes(2),
            ValidAlgorithms = new[]
            {
                SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
                SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
                SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512
            }
        };

        return await _tokenHandler.ValidateTokenAsync(idToken, parameters).ConfigureAwait(false);
    }

    private async Task<User> GetOrCreateUserAsync(ClaimsIdentity identity, string remoteEndPoint)
    {
        var username = identity.FindFirst(_settings.UsernameClaim)?.Value?.Trim();
        if (string.IsNullOrEmpty(username))
        {
            throw new OidcException(
                OidcError.MissingUsername,
                $"Neither the ID token nor the userinfo response has a '{_settings.UsernameClaim}' claim (Oidc:UsernameClaim). "
                + $"The ID token's claims are: {string.Join(", ", identity.Claims.Select(c => c.Type).Distinct())}. "
                + $"Requested scopes: {_settings.Scopes}.");
        }

        var subject = identity.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (string.IsNullOrEmpty(subject))
        {
            throw new OidcException(OidcError.InvalidToken, "The ID token has no subject.");
        }

        var accountKey = OidcLinkStore.Key(_settings.Issuer!.OriginalString, subject);

        var groups = identity.FindAll(_settings.GroupsClaim).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        if (_settings.AllowedGroup is not null && !groups.Contains(_settings.AllowedGroup))
        {
            // Removed from the group: also end the sessions the account already has.
            var linkedId = _links.FindUser(accountKey);
            if (linkedId is not null)
            {
                await _sessionManager.RevokeUserTokens(linkedId.Value, string.Empty).ConfigureAwait(false);
            }

            throw new OidcException(OidcError.AccessDenied, $"User {username} is not in the group allowed to log in.");
        }

        // Serialize user creation and permission updates, so two simultaneous first logins create one user.
        await _userLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var user = await LinkOrCreateUserAsync(username, accountKey).ConfigureAwait(false);

            if (user.HasPermission(PermissionKind.IsDisabled))
            {
                throw new OidcException(OidcError.AccessDenied, $"User {user.Username} is disabled.");
            }

            // The same policies as a password login.
            if (!user.HasPermission(PermissionKind.EnableRemoteAccess) && !_networkManager.IsInLocalNetwork(remoteEndPoint))
            {
                throw new OidcException(OidcError.AccessDenied, $"User {user.Username} may not log in from outside the local network ({remoteEndPoint}).");
            }

            if (!user.IsParentalScheduleAllowed())
            {
                throw new OidcException(OidcError.AccessDenied, $"User {user.Username} is not allowed access at this time.");
            }

            // A user without a password could be logged in to with an empty one if password login is ever
            // enabled again. Give it a random password nobody knows.
            if (string.IsNullOrEmpty(user.Password))
            {
                await _userManager.ChangePassword(user.Id, NewSecret()).ConfigureAwait(false);
            }

            await ApplyAdministratorAsync(user, groups).ConfigureAwait(false);
            return user;
        }
        finally
        {
            _userLock.Release();
        }
    }

    private async Task<User> LinkOrCreateUserAsync(string username, string accountKey)
    {
        var user = _userManager.GetUserByName(username);
        if (user is not null)
        {
            var linkedKey = _links.FindKey(user.Id);
            if (linkedKey is not null && !string.Equals(linkedKey, accountKey, StringComparison.Ordinal))
            {
                // The user name now belongs to another identity provider account than the one this Jellyfin
                // account was linked to: a rename, or somebody taking the name to take over the account.
                throw new OidcException(OidcError.AccessDenied, $"User {username} is linked to another identity provider account.");
            }

            if (linkedKey is null)
            {
                _logger.LogInformation("Linked user {UserName} to identity provider account {Account}.", user.Username, accountKey);
            }
        }
        else
        {
            if (!_settings.AutoCreateUsers)
            {
                throw new OidcException(OidcError.AccessDenied, $"User {username} does not exist and users are not created automatically.");
            }

            try
            {
                user = await _userManager.CreateUserAsync(username).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                throw new OidcException(OidcError.InvalidUsername, $"'{username}' is not a valid Jellyfin user name.", ex);
            }

            // A new account sees no library until an administrator gives it access to some.
            user.SetPermission(PermissionKind.EnableAllFolders, false);
            user.SetPreference(PreferenceKind.EnabledFolders, Array.Empty<Guid>());
            await _userManager.UpdateUserAsync(user).ConfigureAwait(false);

            _logger.LogInformation("Created user {UserName} on their first OpenID Connect login. It has access to no library yet.", username);
        }

        // The user name decides the account. The link only stops another identity provider account from taking over
        // an account that is already linked; an identity provider account whose name changed (or whose user name claim
        // was reconfigured) moves to the account matching its new name.
        var previousUserId = _links.FindUser(accountKey);
        if (previousUserId is not null && !previousUserId.Value.Equals(user.Id))
        {
            _logger.LogInformation(
                "Identity provider account {Account} now logs in to user {UserName} instead of user {PreviousUserId}.",
                accountKey,
                user.Username,
                previousUserId.Value);
        }

        _links.Link(user.Id, accountKey);
        return user;
    }

    private async Task ApplyAdministratorAsync(User user, IReadOnlySet<string> groups)
    {
        var isAdmin = user.HasPermission(PermissionKind.IsAdministrator);
        bool shouldBeAdmin;
        if (_settings.AdminUsers.Contains(user.Username))
        {
            shouldBeAdmin = true;
        }
        else if (_settings.AdminGroup is not null)
        {
            shouldBeAdmin = groups.Contains(_settings.AdminGroup);
        }
        else
        {
            // No mapping: administrators are managed in Jellyfin.
            return;
        }

        if (isAdmin == shouldBeAdmin)
        {
            return;
        }

        if (!shouldBeAdmin)
        {
            var otherAdmins = _userManager.GetUsers().Count(u => !u.Id.Equals(user.Id) && u.HasPermission(PermissionKind.IsAdministrator));
            if (otherAdmins == 0)
            {
                _logger.LogWarning("Not removing administrator rights from {UserName}: no other administrator would be left.", user.Username);
                return;
            }
        }

        user.SetPermission(PermissionKind.IsAdministrator, shouldBeAdmin);
        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
        if (!shouldBeAdmin)
        {
            // Sessions opened as an administrator keep their rights otherwise.
            await _sessionManager.RevokeUserTokens(user.Id, string.Empty).ConfigureAwait(false);
        }

        _logger.LogInformation("{Change} administrator rights for {UserName} from OpenID Connect.", shouldBeAdmin ? "Granted" : "Removed", user.Username);
    }

    private sealed record PendingLogin(string Nonce, string CodeVerifier, OidcDevice Device, string RemoteEndPoint, DateTimeOffset CreatedAt);

    private sealed record Handoff(AuthenticationResult Result, string DeviceId, DateTimeOffset CreatedAt);

    private sealed class TokenResponse
    {
        [JsonPropertyName("id_token")]
        public string? IdToken { get; set; }

        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }
    }
}
