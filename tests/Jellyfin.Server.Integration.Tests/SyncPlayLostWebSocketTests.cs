using System;
using System.Threading.Tasks;
using Xunit;
using static Jellyfin.Server.Integration.Tests.SyncPlayLostWebSocketHelper;

namespace Jellyfin.Server.Integration.Tests;

public sealed class SyncPlayLostWebSocketTests : IClassFixture<JellyfinApplicationFactory>
{
    private readonly JellyfinApplicationFactory _factory;

    public SyncPlayLostWebSocketTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task LostWebSocket_EndsSession_And_RemovesEmptySyncPlayGroup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (client, _) = await CreateGroupAndLoseWebSocketAsync(WithGracePeriod(_factory, 2), cancellationToken);

        var groupCount = await WaitForGroupCountAsync(client, 0, TimeSpan.FromSeconds(45), cancellationToken);
        Assert.True(
            groupCount == 0,
            $"SyncPlay group still listed {groupCount} group(s) after the WebSocket was lost and the grace period ended: "
            + "the keep-alive watchdog removed the socket from its watchlist without closing "
            + "the session, leaving a zombie participant in the group (SessionWebSocketListener).");
    }
}
