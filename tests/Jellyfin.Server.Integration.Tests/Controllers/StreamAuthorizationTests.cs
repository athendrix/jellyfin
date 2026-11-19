using System;
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public sealed class StreamAuthorizationTests : IClassFixture<JellyfinApplicationFactory>
{
    private readonly JellyfinApplicationFactory _factory;

    public StreamAuthorizationTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("Videos/{0}/stream")]
    [InlineData("Videos/{0}/stream.mkv")]
    [InlineData("Audio/{0}/stream")]
    [InlineData("Audio/{0}/stream.mp3")]
    [InlineData("Videos/{0}/{0}/Subtitles/0/Stream.srt")]
    [InlineData("Videos/{0}/{0}/Subtitles/0/0/Stream.srt")]
    public async Task Stream_WithoutToken_IsUnauthorized(string path)
    {
        var client = _factory.CreateClient();

        using var response = await client.GetAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, path, Guid.NewGuid().ToString("N")), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
