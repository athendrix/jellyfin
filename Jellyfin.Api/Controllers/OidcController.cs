using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Auth.Oidc;
using Jellyfin.Api.Models.OidcDtos;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// OpenID Connect login.
/// </summary>
public class OidcController : BaseJellyfinApiController
{
    /// <summary>
    /// The cookie binding a login to the browser that started it.
    /// </summary>
    internal const string StateCookieName = "jellyfin_oidc_state";

    private readonly OidcService _oidcService;
    private readonly IAuthorizationContext _authContext;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<OidcController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcController"/> class.
    /// </summary>
    /// <param name="oidcService">The OpenID Connect service.</param>
    /// <param name="authContext">The authorization context.</param>
    /// <param name="serverConfigurationManager">The server configuration manager.</param>
    /// <param name="appHost">The application host.</param>
    /// <param name="logger">The logger.</param>
    public OidcController(
        OidcService oidcService,
        IAuthorizationContext authContext,
        IServerConfigurationManager serverConfigurationManager,
        IServerApplicationHost appHost,
        ILogger<OidcController> logger)
    {
        _oidcService = oidcService;
        _authContext = authContext;
        _serverConfigurationManager = serverConfigurationManager;
        _appHost = appHost;
        _logger = logger;
    }

    private string BaseUrl => _serverConfigurationManager.GetNetworkConfiguration().BaseUrl;

    /// <summary>
    /// Gets how users log in to this server.
    /// </summary>
    /// <response code="200">Login options returned.</response>
    /// <returns>The login options.</returns>
    [HttpGet("Config")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<OidcConfigDto> GetConfig()
    {
        var settings = _oidcService.Settings;
        return new OidcConfigDto
        {
            Enabled = settings.Enabled,
            PasswordLoginEnabled = settings.PasswordLoginEnabled,
            ProviderName = settings.Enabled ? settings.ProviderName : null
        };
    }

    /// <summary>
    /// Starts a login: redirects to the identity provider.
    /// </summary>
    /// <param name="deviceId">The id of the device logging in.</param>
    /// <param name="deviceName">The name of the device logging in.</param>
    /// <param name="client">The client application.</param>
    /// <param name="version">The client application version.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="302">Redirect to the identity provider, or back to the login page on error.</response>
    /// <returns>A redirect.</returns>
    [HttpGet("Start")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<ActionResult> Start(
        [FromQuery, Required] string deviceId,
        [FromQuery, Required] string deviceName,
        [FromQuery, Required] string client,
        [FromQuery, Required] string version,
        CancellationToken cancellationToken)
    {
        try
        {
            var (state, authorizationUrl) = await _oidcService
                .StartLoginAsync(new OidcDevice(deviceId, deviceName, client, version), cancellationToken)
                .ConfigureAwait(false);

            Response.Cookies.Append(StateCookieName, state, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                // Lax: sent on the top-level redirect back from the identity provider, not on cross-site subrequests.
                SameSite = SameSiteMode.Lax,
                Path = BaseUrl + OidcSettings.CallbackPath,
                MaxAge = OidcService.LoginLifetime,
                IsEssential = true
            });

            return Redirect(authorizationUrl.AbsoluteUri);
        }
        catch (OidcException ex)
        {
            _logger.LogWarning(ex, "Could not start an OpenID Connect login: {Error}.", ex.Error);
            return RedirectToLogin("oidcError", ex.Error.ToString());
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or IOException)
        {
            // Raised by the metadata retriever when the identity provider cannot be reached.
            _logger.LogError(ex, "Could not reach the OpenID Connect identity provider.");
            return RedirectToLogin("oidcError", nameof(OidcError.ProviderUnavailable));
        }
    }

    /// <summary>
    /// Completes a login: the identity provider redirects here.
    /// </summary>
    /// <param name="code">The authorization code.</param>
    /// <param name="state">The state.</param>
    /// <param name="error">The error reported by the identity provider, if any.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="302">Redirect to the login page, which picks up the session.</response>
    /// <returns>A redirect.</returns>
    [HttpGet(OidcSettings.CallbackPath)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<ActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken cancellationToken)
    {
        var browserState = Request.Cookies[StateCookieName];
        Response.Cookies.Delete(StateCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = BaseUrl + OidcSettings.CallbackPath
        });

        if (!string.IsNullOrEmpty(error))
        {
            // The provider's error text is not reflected to the client, only logged.
            _logger.LogWarning("The identity provider reported an error: {ProviderError}.", error.Length > 100 ? error[..100] : error);
            return RedirectToLogin("oidcError", nameof(OidcError.ProviderError));
        }

        try
        {
            var handoffCode = await _oidcService
                .CompleteLoginAsync(state, browserState, code, HttpContext.GetNormalizedRemoteIP().ToString(), cancellationToken)
                .ConfigureAwait(false);
            return RedirectToLogin("oidcCode", handoffCode);
        }
        catch (OidcException ex)
        {
            _logger.LogWarning(ex, "OpenID Connect login failed: {Error}.", ex.Error);
            return RedirectToLogin("oidcError", ex.Error.ToString());
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or IOException)
        {
            _logger.LogError(ex, "Could not reach the OpenID Connect identity provider.");
            return RedirectToLogin("oidcError", nameof(OidcError.ProviderUnavailable));
        }
    }

    /// <summary>
    /// Exchanges the one-time code from a completed login for the session.
    /// </summary>
    /// <param name="request">The one-time code.</param>
    /// <response code="200">Session returned.</response>
    /// <response code="401">The code is unknown, has expired, or belongs to another device.</response>
    /// <returns>The <see cref="AuthenticationResult"/> of the new session.</returns>
    [HttpPost("Redeem")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [Tags("Authentication")]
    public async Task<ActionResult<AuthenticationResult>> Redeem([FromBody, Required] OidcRedeemDto request)
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var result = _oidcService.Redeem(request.Code, auth.DeviceId);
        if (result is null)
        {
            return Unauthorized();
        }

        return result;
    }

    private RedirectResult RedirectToLogin(string parameter, string value)
    {
        // The web client uses hash routing: everything after '#' stays in the browser and is never sent to a server
        // or written to proxy logs, which matters for the one-time code.
        var url = BaseUrl + "/web/#/login?serverid=" + Uri.EscapeDataString(_appHost.SystemId)
            + "&" + parameter + "=" + Uri.EscapeDataString(value);
        return Redirect(url);
    }
}
