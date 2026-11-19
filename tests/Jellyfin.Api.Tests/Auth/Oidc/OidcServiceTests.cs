using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Auth.Oidc;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Auth.Oidc;

public sealed class OidcServiceTests : IDisposable
{
    private const string Issuer = "https://auth.example.com";
    private const string PublicUrl = "https://jellyfin.example.com";
    private const string ClientId = "jellyfin.example.com";

    private static readonly OidcDevice _device = new("device-1", "Living room", "Jellyfin Desktop", "2.0.0");

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _signingKey;
    private readonly List<User> _users = new();
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly List<string> _loggedOut = new();
    private readonly Dictionary<string, string> _challenges = new();
    private readonly Mock<INetworkManager> _networkManager = new();
    private readonly OidcLinkStore _links = new(null, NullLogger.Instance);
    private readonly List<Guid> _passwordsSet = new();
    private readonly List<Guid> _tokensRevoked = new();
    private string? _userInfo;
    private Func<IDictionary<string, string>, string?>? _issueIdToken;

    public OidcServiceTests()
    {
        _signingKey = new RsaSecurityKey(_rsa) { KeyId = "key-1" };

        _userManager.Setup(m => m.GetUserByName(It.IsAny<string>()))
            .Returns((string name) => _users.FirstOrDefault(u => string.Equals(u.Username, name, StringComparison.OrdinalIgnoreCase)));
        _userManager.Setup(m => m.GetUsers()).Returns(() => _users);
        _userManager.Setup(m => m.CreateUserAsync(It.IsAny<string>()))
            .ReturnsAsync((string name) =>
            {
                var user = new User(name, "provider", "reset-provider");
                _users.Add(user);
                return user;
            });
        _userManager.Setup(m => m.UpdateUserAsync(It.IsAny<User>())).Returns(Task.CompletedTask);
        _userManager.Setup(m => m.GetUserById(It.IsAny<Guid>()))
            .Returns((Guid id) => _users.FirstOrDefault(u => u.Id.Equals(id)));
        _userManager.Setup(m => m.ChangePassword(It.IsAny<Guid>(), It.IsAny<string>()))
            .Callback((Guid id, string password) =>
            {
                _passwordsSet.Add(id);
                _users.First(u => u.Id.Equals(id)).Password = "hashed";
            })
            .Returns(Task.CompletedTask);
        _sessionManager.Setup(m => m.RevokeUserTokens(It.IsAny<Guid>(), It.IsAny<string>()))
            .Callback((Guid id, string _) => _tokensRevoked.Add(id))
            .Returns(Task.CompletedTask);
        _networkManager.Setup(m => m.IsInLocalNetwork(It.IsAny<string>())).Returns(true);

        _sessionManager.Setup(m => m.AuthenticateDirect(It.IsAny<AuthenticationRequest>()))
            .ReturnsAsync((AuthenticationRequest request) => new AuthenticationResult { AccessToken = "token-for-" + request.DeviceId });
        _sessionManager.Setup(m => m.Logout(It.IsAny<string>()))
            .Callback((string token) => _loggedOut.Add(token))
            .Returns(Task.CompletedTask);
    }

    public void Dispose() => _rsa.Dispose();

    [Fact]
    public void Settings_NoIssuer_OidcDisabledAndPasswordLoginEnabled()
    {
        var settings = OidcSettings.FromConfiguration(Configuration());

        Assert.False(settings.Enabled);
        Assert.True(settings.PasswordLoginEnabled);
    }

    [Fact]
    public void Settings_Issuer_DefaultsFromPublicUrl()
    {
        var settings = OidcSettings.FromConfiguration(Configuration(("Issuer", Issuer), ("PublicUrl", PublicUrl + "/")));

        Assert.True(settings.Enabled);
        Assert.False(settings.PasswordLoginEnabled);
        Assert.Equal(ClientId, settings.ClientId);
        Assert.Equal(new Uri(PublicUrl + "/charon-oidc-redirect"), settings.RedirectUri);
        Assert.Contains("openid", settings.Scopes, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://auth.example.com", PublicUrl)]
    [InlineData(Issuer, "http://jellyfin.example.com")]
    [InlineData(Issuer, null)]
    [InlineData("not a url", PublicUrl)]
    public void Settings_Invalid_Throws(string issuer, string? publicUrl)
    {
        var values = new List<(string, string)> { ("Issuer", issuer) };
        if (publicUrl is not null)
        {
            values.Add(("PublicUrl", publicUrl));
        }

        Assert.Throws<InvalidOperationException>(() => OidcSettings.FromConfiguration(Configuration(values.ToArray())));
    }

    [Fact]
    public async Task Login_ValidFlow_CreatesUserAndHandsOffSessionOnce()
    {
        using var service = CreateService();

        var (state, url) = await StartAsync(service);
        var query = ParseQuery(url);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(ClientId, query["client_id"]);
        Assert.Equal(PublicUrl + "/charon-oidc-redirect", query["redirect_uri"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(state, query["state"]);

        var handoff = await service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None);

        var user = Assert.Single(_users);
        Assert.Equal("alice", user.Username);

        var result = service.Redeem(handoff, _device.DeviceId);
        Assert.NotNull(result);
        Assert.Equal("token-for-device-1", result.AccessToken);

        // One-time only.
        Assert.Null(service.Redeem(handoff, _device.DeviceId));
    }

    [Fact]
    public async Task Login_ExistingUser_IsLinkedByNameCaseInsensitively()
    {
        _users.Add(new User("Alice", "provider", "reset-provider"));
        using var service = CreateService();

        var (state, _) = await StartAsync(service);
        await service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None);

        Assert.Single(_users);
        _userManager.Verify(m => m.CreateUserAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Login_StateFromAnotherBrowser_IsRejected()
    {
        using var service = CreateService();
        var (state, _) = await StartAsync(service);

        var ex = await Assert.ThrowsAsync<OidcException>(() => service.CompleteLoginAsync(state, "other-browser", "the-code", "10.0.0.2", CancellationToken.None));
        Assert.Equal(OidcError.InvalidState, ex.Error);

        ex = await Assert.ThrowsAsync<OidcException>(() => service.CompleteLoginAsync(state, null, "the-code", "10.0.0.2", CancellationToken.None));
        Assert.Equal(OidcError.InvalidState, ex.Error);
    }

    [Fact]
    public async Task Login_UnknownState_IsRejected()
    {
        using var service = CreateService();

        var ex = await Assert.ThrowsAsync<OidcException>(() => service.CompleteLoginAsync("forged", "forged", "the-code", "10.0.0.2", CancellationToken.None));
        Assert.Equal(OidcError.InvalidState, ex.Error);
    }

    [Fact]
    public async Task Login_StateIsSingleUse()
    {
        using var service = CreateService();
        var (state, _) = await StartAsync(service);
        await service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<OidcException>(() => service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None));
        Assert.Equal(OidcError.InvalidState, ex.Error);
    }

    [Fact]
    public async Task Login_WrongNonce_IsRejected()
    {
        using var service = CreateService(form => CreateIdToken(new Dictionary<string, object> { ["nonce"] = "something-else" }));
        var (state, _) = await StartAsync(service);

        var ex = await Assert.ThrowsAsync<OidcException>(() => service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None));
        Assert.Equal(OidcError.InvalidToken, ex.Error);
        Assert.Empty(_users);
    }

    [Fact]
    public async Task Login_WrongAudience_IsRejected()
    {
        using var service = CreateService(audience: "another-client");
        var (state, _) = await StartAsync(service);

        var ex = await Assert.ThrowsAsync<OidcException>(() => service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None));
        Assert.Equal(OidcError.InvalidToken, ex.Error);
    }

    [Fact]
    public async Task Login_WrongIssuer_IsRejected()
    {
        using var service = CreateService(issuer: "https://evil.example.com");
        var (state, _) = await StartAsync(service);

        var ex = await Assert.ThrowsAsync<OidcException>(() => service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None));
        Assert.Equal(OidcError.InvalidToken, ex.Error);
    }

    [Fact]
    public async Task Login_TokenSignedWithAnotherKey_IsRejected()
    {
        using var otherRsa = RSA.Create(2048);
        var otherKey = new RsaSecurityKey(otherRsa) { KeyId = "key-1" };
        using var service = CreateService(signingKey: otherKey);
        var (state, _) = await StartAsync(service);

        var ex = await Assert.ThrowsAsync<OidcException>(() => service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None));
        Assert.Equal(OidcError.InvalidToken, ex.Error);
    }

    [Fact]
    public async Task Login_UnsignedToken_IsRejected()
    {
        using var service = CreateService(form =>
        {
            var handler = new JsonWebTokenHandler();
            return handler.CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = ClientId,
                Expires = DateTime.UtcNow.AddMinutes(5),
                Claims = new Dictionary<string, object> { ["preferred_username"] = "alice", ["nonce"] = form["nonce"] }
            });
        });
        var (state, _) = await StartAsync(service);

        var ex = await Assert.ThrowsAsync<OidcException>(() => service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None));
        Assert.Equal(OidcError.InvalidToken, ex.Error);
    }

    [Fact]
    public async Task Login_ExpiredToken_IsRejected()
    {
        using var service = CreateService(form => CreateIdToken(
            new Dictionary<string, object> { ["nonce"] = form["nonce"] },
            expires: DateTime.UtcNow.AddMinutes(-10)));
        var (state, _) = await StartAsync(service);

        var ex = await Assert.ThrowsAsync<OidcException>(() => service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None));
        Assert.Equal(OidcError.InvalidToken, ex.Error);
    }

    [Fact]
    public async Task Redeem_FromAnotherDevice_FailsAndRevokesTheSession()
    {
        using var service = CreateService();
        var (state, _) = await StartAsync(service);
        var handoff = await service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None);

        Assert.Null(service.Redeem(handoff, "attacker-device"));
        Assert.Contains("token-for-device-1", _loggedOut);
        Assert.Null(service.Redeem(handoff, _device.DeviceId));
    }

    [Fact]
    public async Task Login_DisabledUser_IsRejected()
    {
        var user = new User("alice", "provider", "reset-provider");
        user.SetPermission(PermissionKind.IsDisabled, true);
        _users.Add(user);
        using var service = CreateService();
        var (state, _) = await StartAsync(service);

        var ex = await Assert.ThrowsAsync<OidcException>(() => service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None));
        Assert.Equal(OidcError.AccessDenied, ex.Error);
    }

    [Fact]
    public async Task Login_NotInAllowedGroup_IsRejected()
    {
        using var service = CreateService(extraSettings: new[] { ("AllowedGroup", "jellyfin-users") });
        var (state, _) = await StartAsync(service);

        var ex = await Assert.ThrowsAsync<OidcException>(() => service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None));
        Assert.Equal(OidcError.AccessDenied, ex.Error);
        Assert.Empty(_users);
    }

    [Fact]
    public async Task Login_AdminUserFromSettings_BecomesAdministrator()
    {
        using var service = CreateService(extraSettings: new[] { ("AdminUsers", "bob, ALICE") });
        var (state, _) = await StartAsync(service);
        await service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None);

        Assert.True(Assert.Single(_users).HasPermission(PermissionKind.IsAdministrator));
    }

    [Fact]
    public async Task Login_AdminGroupMapping_GrantsAndRemovesAdministrator()
    {
        var otherAdmin = new User("root", "provider", "reset-provider");
        otherAdmin.SetPermission(PermissionKind.IsAdministrator, true);
        _users.Add(otherAdmin);

        var groups = new[] { "admins" };
        using var service = CreateService(
            form => CreateIdToken(new Dictionary<string, object> { ["nonce"] = form["nonce"], ["groups"] = groups }),
            extraSettings: new[] { ("AdminGroup", "admins") });

        var (state, _) = await StartAsync(service);
        await service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None);
        var alice = _users.Single(u => u.Username == "alice");
        Assert.True(alice.HasPermission(PermissionKind.IsAdministrator));

        groups = new[] { "users" };
        (state, _) = await StartAsync(service);
        await service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None);
        Assert.False(alice.HasPermission(PermissionKind.IsAdministrator));
    }

    [Fact]
    public async Task Login_AdminGroupMapping_NeverRemovesTheLastAdministrator()
    {
        var alice = new User("alice", "provider", "reset-provider");
        alice.SetPermission(PermissionKind.IsAdministrator, true);
        _users.Add(alice);

        using var service = CreateService(extraSettings: new[] { ("AdminGroup", "admins") });
        var (state, _) = await StartAsync(service);
        await service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None);

        Assert.True(alice.HasPermission(PermissionKind.IsAdministrator));
    }

    [Fact]
    public async Task Login_NoUsernameClaim_IsRejected()
    {
        using var service = CreateService(form => CreateIdToken(
            new Dictionary<string, object> { ["nonce"] = form["nonce"] },
            username: null));
        var (state, _) = await StartAsync(service);

        var ex = await Assert.ThrowsAsync<OidcException>(() => service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None));
        Assert.Equal(OidcError.MissingUsername, ex.Error);
    }

    [Fact]
    public async Task StartLogin_IncompleteDevice_IsRejected()
    {
        using var service = CreateService();

        var ex = await Assert.ThrowsAsync<OidcException>(() => service.StartLoginAsync(_device with { DeviceId = string.Empty }, "10.0.0.2", CancellationToken.None));
        Assert.Equal(OidcError.InvalidRequest, ex.Error);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>("Oidc:" + v.Key, v.Value)))
            .Build();

    private static Dictionary<string, string> ParseQuery(Uri url)
        => url.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));

    [Fact]
    public async Task Login_CreatedUser_GetsAnUnknownPassword()
    {
        using var service = CreateService();
        var (state, _) = await StartAsync(service);
        await service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None);

        var user = Assert.Single(_users);
        Assert.Contains(user.Id, _passwordsSet);
    }

    [Fact]
    public async Task Login_SameUsernameFromAnotherSubject_IsRejected()
    {
        var subject = "subject-1";
        using var service = CreateService(form => CreateIdToken(new Dictionary<string, object> { ["nonce"] = form["nonce"], ["sub"] = subject }));

        var (state, _) = await StartAsync(service);
        await service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None);

        // Somebody else at the identity provider takes the user name "alice".
        subject = "subject-2";
        (state, _) = await StartAsync(service);
        var ex = await Assert.ThrowsAsync<OidcException>(() => service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None));
        Assert.Equal(OidcError.AccessDenied, ex.Error);
    }

    [Fact]
    public async Task Login_LinkedSubjectWithNewUsername_LogsInToTheLinkedAccount()
    {
        var username = "alice";
        using var service = CreateService(form => CreateIdToken(new Dictionary<string, object> { ["nonce"] = form["nonce"] }, username: username));

        var (state, _) = await StartAsync(service);
        await service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None);

        // The same identity provider account is renamed: it keeps its Jellyfin account rather than getting a new one.
        username = "alice2";
        (state, _) = await StartAsync(service);
        await service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None);

        Assert.Equal("alice", Assert.Single(_users).Username);
    }

    [Fact]
    public async Task Login_RemoteWithoutRemoteAccess_IsRejected()
    {
        var user = new User("alice", "provider", "reset-provider");
        user.SetPermission(PermissionKind.EnableRemoteAccess, false);
        _users.Add(user);
        _networkManager.Setup(m => m.IsInLocalNetwork(It.IsAny<string>())).Returns(false);
        using var service = CreateService();

        var (state, _) = await StartAsync(service, remote: "203.0.113.5");
        var ex = await Assert.ThrowsAsync<OidcException>(() => service.CompleteLoginAsync(state, state, "the-code", "203.0.113.5", CancellationToken.None));
        Assert.Equal(OidcError.AccessDenied, ex.Error);
    }

    [Fact]
    public async Task StartLogin_TooManyFromOneAddress_IsRejected()
    {
        using var service = CreateService();
        for (var i = 0; i < 20; i++)
        {
            await StartAsync(service, remote: "203.0.113.5");
        }

        var ex = await Assert.ThrowsAsync<OidcException>(() => StartAsync(service, remote: "203.0.113.5"));
        Assert.Equal(OidcError.TooManyLogins, ex.Error);

        // Other clients can still log in.
        await StartAsync(service, remote: "203.0.113.6");
    }

    [Fact]
    public async Task Login_DemotedAdministrator_LosesExistingSessions()
    {
        var root = new User("root", "provider", "reset-provider");
        root.SetPermission(PermissionKind.IsAdministrator, true);
        var alice = new User("alice", "provider", "reset-provider");
        alice.SetPermission(PermissionKind.IsAdministrator, true);
        _users.Add(root);
        _users.Add(alice);

        using var service = CreateService(extraSettings: new[] { ("AdminGroup", "admins") });
        var (state, _) = await StartAsync(service);
        await service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None);

        Assert.False(alice.HasPermission(PermissionKind.IsAdministrator));
        Assert.Contains(alice.Id, _tokensRevoked);
    }

    [Fact]
    public void LinkStore_PersistsLinks()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oidc-links-" + Guid.NewGuid() + ".json");
        try
        {
            var userId = Guid.NewGuid();
            new OidcLinkStore(path, NullLogger.Instance).Link(userId, OidcLinkStore.Key(Issuer + "/", "subject-1"));

            var reloaded = new OidcLinkStore(path, NullLogger.Instance);
            Assert.Equal(userId, reloaded.FindUser(OidcLinkStore.Key(Issuer, "subject-1")));
            Assert.Null(reloaded.FindUser(OidcLinkStore.Key(Issuer, "subject-2")));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task Login_UsernameOnlyInUserInfo_IsReadFromThere()
    {
        _userInfo = "{\"sub\":\"subject-1\",\"username\":\"carol\",\"groups\":[\"admins\"]}";
        using var service = CreateService(
            form => CreateIdToken(new Dictionary<string, object> { ["nonce"] = form["nonce"] }, username: null),
            extraSettings: new[] { ("UsernameClaim", "username"), ("AdminGroup", "admins") });

        var (state, url) = await StartAsync(service);
        Assert.Contains("groups", ParseQuery(url)["scope"], StringComparison.Ordinal);
        await service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None);

        var user = Assert.Single(_users);
        Assert.Equal("carol", user.Username);
        Assert.True(user.HasPermission(PermissionKind.IsAdministrator));
    }

    [Fact]
    public async Task Login_UserInfoAboutAnotherSubject_IsRejected()
    {
        _userInfo = "{\"sub\":\"someone-else\",\"username\":\"carol\"}";
        using var service = CreateService(
            form => CreateIdToken(new Dictionary<string, object> { ["nonce"] = form["nonce"] }, username: null),
            extraSettings: new[] { ("UsernameClaim", "username") });

        var (state, _) = await StartAsync(service);
        var ex = await Assert.ThrowsAsync<OidcException>(() => service.CompleteLoginAsync(state, state, "the-code", "10.0.0.2", CancellationToken.None));
        Assert.Equal(OidcError.InvalidResponse, ex.Error);
        Assert.Empty(_users);
    }

    private string CreateIdToken(
        Dictionary<string, object> claims,
        string? issuer = null,
        string? audience = null,
        SecurityKey? signingKey = null,
        DateTime? expires = null,
        string? username = "alice")
    {
        if (username is not null)
        {
            claims.TryAdd("preferred_username", username);
        }

        claims.TryAdd("sub", "subject-1");
        var expiry = expires ?? DateTime.UtcNow.AddMinutes(5);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer,
            Audience = audience ?? ClientId,
            IssuedAt = expiry.AddMinutes(-10),
            NotBefore = expiry.AddMinutes(-10),
            Expires = expiry,
            Claims = claims,
            SigningCredentials = new SigningCredentials(signingKey ?? _signingKey, SecurityAlgorithms.RsaSha256)
        });
    }

    private OidcService CreateService(
        Func<IDictionary<string, string>, string?>? issueIdToken = null,
        string? issuer = null,
        string? audience = null,
        SecurityKey? signingKey = null,
        (string Key, string Value)[]? extraSettings = null)
    {
        var settingValues = new List<(string, string)> { ("Issuer", Issuer), ("PublicUrl", PublicUrl) };
        settingValues.AddRange(extraSettings ?? Array.Empty<(string, string)>());
        var settings = OidcSettings.FromConfiguration(Configuration(settingValues.ToArray()));

        var metadata = new OpenIdConnectConfiguration
        {
            Issuer = Issuer,
            AuthorizationEndpoint = Issuer + "/authorize",
            TokenEndpoint = Issuer + "/token",
            UserInfoEndpoint = Issuer + "/userinfo"
        };
        metadata.SigningKeys.Add(_signingKey);

        // The nonce is only known once the login starts, so the token is created when the code is exchanged.
        _issueIdToken = issueIdToken ?? (form => CreateIdToken(new Dictionary<string, object> { ["nonce"] = form["nonce"] }, issuer, audience, signingKey));

        var handler = new TokenEndpointHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/userinfo")
            {
                Assert.Equal("Bearer access-token", request.Headers.Authorization?.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_userInfo ?? "{\"sub\":\"subject-1\"}", Encoding.UTF8, "application/json")
                };
            }

            var form = ParseForm(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("authorization_code", form["grant_type"]);
            Assert.Equal(ClientId, form["client_id"]);
            Assert.Equal(PublicUrl + "/charon-oidc-redirect", form["redirect_uri"]);

            // PKCE: the verifier must hash to the challenge sent in the authorization request.
            var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"])));
            Assert.True(_challenges.TryGetValue(challenge, out var nonce), "code_verifier does not match any code_challenge");
            form["nonce"] = nonce;

            var idToken = _issueIdToken(form);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string?> { ["id_token"] = idToken, ["access_token"] = "access-token" }), Encoding.UTF8, "application/json")
            };
        });

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));

        return new OidcService(
            settings,
            _userManager.Object,
            _sessionManager.Object,
            factory.Object,
            _networkManager.Object,
            _links,
            NullLogger<OidcService>.Instance,
            TimeProvider.System,
            new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata));
    }

    // Starts a login and records its PKCE challenge and nonce, so the fake token endpoint can check them.
    private async Task<(string State, Uri Url)> StartAsync(OidcService service, OidcDevice? device = null, string remote = "10.0.0.2")
    {
        var (state, url) = await service.StartLoginAsync(device ?? _device, remote, CancellationToken.None);
        var query = ParseQuery(url);
        _challenges[query["code_challenge"]] = query["nonce"];
        return (state, url);
    }

    private static Dictionary<string, string> ParseForm(string body)
        => body.Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    private sealed class TokenEndpointHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public TokenEndpointHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }
}
