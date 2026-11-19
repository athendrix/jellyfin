using MediaBrowser.MediaEncoding.Encoder;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.MediaEncoding.Tests;

public class EncodingUtilsTests
{
    [Theory]
    [InlineData(MediaProtocol.Http, "http://x/a\" -y -f mp4 \"/config/x")]
    [InlineData(MediaProtocol.File, "http://x/a\" -y -f mp4 \"/config/x")]
    public void GetInputArgument_QuoteInUrl_StaysOneArgument(MediaProtocol protocol, string url)
    {
        var argument = EncodingUtils.GetInputArgument("file", url, protocol);

        // The only unescaped quotes are the two around the argument.
        Assert.StartsWith("\"", argument, System.StringComparison.Ordinal);
        Assert.EndsWith("\"", argument, System.StringComparison.Ordinal);
        Assert.DoesNotContain("a\" -y", argument, System.StringComparison.Ordinal);
        Assert.Contains("a\\\" -y", argument, System.StringComparison.Ordinal);
    }
}
