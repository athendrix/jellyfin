using Jellyfin.Server.Helpers;
using Xunit;

namespace Jellyfin.Server.Tests.Helpers;

public class WebClientCachePolicyTests
{
    [Theory]
    [InlineData("index.html")]
    [InlineData("main.bundle.js")]
    [InlineData("config.json")]
    [InlineData("manifest.json")]
    [InlineData("robots.txt")]
    [InlineData("touchicon.png")]
    public void GetCacheControl_StableName_MustRevalidate(string fileName)
    {
        Assert.Equal(WebClientCachePolicy.Revalidate, WebClientCachePolicy.GetCacheControl(fileName));
    }

    [Theory]
    [InlineData("12345.0c1a2b3c4d5e6f7a8b9c.chunk.js")]
    [InlineData("main.0c1a2b3c4d5e6f7a8b9c.css")]
    [InlineData("icon.8f14e45fceea167a5a36.png")]
    public void GetCacheControl_ContentHashedName_IsImmutable(string fileName)
    {
        Assert.Equal(WebClientCachePolicy.Immutable, WebClientCachePolicy.GetCacheControl(fileName));
    }
}
