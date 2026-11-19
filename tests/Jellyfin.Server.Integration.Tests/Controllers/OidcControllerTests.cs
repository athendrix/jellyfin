using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Api.Models.OidcDtos;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public sealed class OidcControllerTests : IClassFixture<JellyfinApplicationFactory>
{
    private readonly WebApplicationFactory<Startup> _factory;

    public OidcControllerTests(JellyfinApplicationFactory factory)
    {
        _factory = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Oidc:Issuer"] = "https://auth.invalid",
                ["Oidc:PublicUrl"] = "https://jellyfin.invalid",
                ["Oidc:ProviderName"] = "Charon"
            })));
    }

    [Fact]
    public async Task Config_OidcEnabled_ReportsPasswordLoginDisabled()
    {
        var client = _factory.CreateClient();

        var config = await client.GetFromJsonAsync<OidcConfigDto>("Oidc/Config", TestContext.Current.CancellationToken);

        Assert.NotNull(config);
        Assert.True(config.Enabled);
        Assert.False(config.PasswordLoginEnabled);
        Assert.Equal("Charon", config.ProviderName);
    }

    [Theory]
    [InlineData("Users/AuthenticateByName", "{\"Username\":\"alice\",\"Pw\":\"\"}")]
    [InlineData("Users/ForgotPassword", "{\"EnteredUsername\":\"alice\"}")]
    [InlineData("Users/ForgotPassword/Pin", "{\"Pin\":\"1234\"}")]
    public async Task PasswordEndpoints_OidcEnabled_AreForbidden(string path, string body)
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        request.Headers.TryAddWithoutValidation("Authorization", AuthHelper.DummyAuthHeader);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Callback_WithoutBrowserState_RedirectsToLoginWithError()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("charon-oidc-redirect?code=abc&state=forged", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.OriginalString ?? string.Empty;
        Assert.StartsWith("/web/#/login?serverid=", location, StringComparison.Ordinal);
        Assert.EndsWith("&oidcError=InvalidState", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Callback_ProviderError_IsNotReflected()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("charon-oidc-redirect?error=%3Cscript%3Ealert(1)%3C%2Fscript%3E", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.OriginalString ?? string.Empty;
        Assert.EndsWith("&oidcError=ProviderError", location, StringComparison.Ordinal);
        Assert.DoesNotContain("script", location, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Redeem_UnknownCode_IsUnauthorized()
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "Oidc/Redeem")
        {
            Content = JsonContent.Create(new OidcRedeemDto { Code = "forged" })
        };
        request.Headers.TryAddWithoutValidation("Authorization", AuthHelper.DummyAuthHeader);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
