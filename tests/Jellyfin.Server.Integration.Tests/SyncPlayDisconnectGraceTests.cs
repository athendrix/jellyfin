using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static Jellyfin.Server.Integration.Tests.SyncPlayLostWebSocketHelper;

namespace Jellyfin.Server.Integration.Tests;

public sealed class SyncPlayDisconnectGraceTests : IClassFixture<JellyfinApplicationFactory>
{
    private readonly JellyfinApplicationFactory _factory;

    public SyncPlayDisconnectGraceTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task LostWebSocket_KeepsTheSessionInItsGroupDuringTheGracePeriod()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var factory = WithGracePeriod(_factory, 600);
        var (client, connection) = await CreateGroupAndLoseWebSocketAsync(factory, cancellationToken);

        // Wait until the server has given up on the WebSocket.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while ((await WaitForWatchedWebSocketsAsync(factory, TimeSpan.Zero, cancellationToken)).Contains(connection)
            && stopwatch.Elapsed < TimeSpan.FromSeconds(45))
        {
            await Task.Delay(250, cancellationToken);
        }

        Assert.DoesNotContain(connection, await WaitForWatchedWebSocketsAsync(factory, TimeSpan.Zero, cancellationToken));

        // A client that lost its connection for a moment keeps its group.
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        Assert.Equal(1, await WaitForGroupCountAsync(client, 1, TimeSpan.FromSeconds(1), cancellationToken));
    }
}
