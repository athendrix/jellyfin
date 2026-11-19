using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Session;
using Jellyfin.Api.Models.SyncPlayDtos;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests;

/// <summary>
/// Creates a SyncPlay group from a session with a WebSocket, then makes the server give up on that WebSocket.
/// </summary>
internal static class SyncPlayLostWebSocketHelper
{
    public static WebApplicationFactory<Startup> WithGracePeriod(JellyfinApplicationFactory factory, int seconds)
        => factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SyncPlay:DisconnectGraceSeconds"] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
            })));

    public static async Task<(HttpClient Client, IWebSocketConnection Connection)> CreateGroupAndLoseWebSocketAsync(
        WebApplicationFactory<Startup> factory,
        CancellationToken cancellationToken)
    {
        var client = factory.CreateClient();
        var accessToken = await AuthHelper.CompleteStartupAsync(client);
        client.DefaultRequestHeaders.AddAuthHeader(accessToken);

        var wsClient = factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request =>
            request.Headers.Authorization = AuthHelper.DummyAuthHeader + $", Token={accessToken}";

        var webSocket = await wsClient.ConnectAsync(
            new UriBuilder(factory.Server.BaseAddress)
            {
                Scheme = "ws",
                Path = "websocket"
            }.Uri,
            cancellationToken);

        _ = DrainAsync(webSocket, cancellationToken);

        var watched = await WaitForWatchedWebSocketsAsync(factory, TimeSpan.FromSeconds(10), cancellationToken);
        var connection = Assert.Single(watched);

        using var createResponse = await client.PostAsync(
            "SyncPlay/New",
            JsonContent.Create(new NewGroupRequestDto { GroupName = "ZombieGroupRepro" }, options: JsonDefaults.Options),
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        Assert.Equal(1, await WaitForGroupCountAsync(client, 1, TimeSpan.FromSeconds(10), cancellationToken));

        connection.LastKeepAliveDate = DateTime.UtcNow - TimeSpan.FromSeconds(180);
        return (client, connection);
    }

    public static async Task DrainAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                await webSocket.ReceiveAsync(buffer, cancellationToken);
            }
        }
        catch
        {
            // The server tears the connection down once the watchdog gives up on it.
        }
    }

    public static async Task<IReadOnlyList<IWebSocketConnection>> WaitForWatchedWebSocketsAsync(WebApplicationFactory<Startup> factory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var listener = factory.Services.GetRequiredService<IEnumerable<IWebSocketListener>>()
            .OfType<SessionWebSocketListener>()
            .Single();
        var watchlistField = typeof(SessionWebSocketListener)
            .GetField("_webSockets", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(watchlistField);
        var watchlist = (IEnumerable<IWebSocketConnection>)watchlistField.GetValue(listener)!;

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var snapshot = watchlist.ToArray();
                if (snapshot.Length > 0 || stopwatch.Elapsed >= timeout)
                {
                    return snapshot;
                }
            }
            catch (InvalidOperationException)
            {
                // The watchdog mutated the set during enumeration; retry.
            }

            await Task.Delay(100, cancellationToken);
        }
    }

    public static async Task<int> WaitForGroupCountAsync(HttpClient client, int expected, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var count = -1;
        while (stopwatch.Elapsed < timeout)
        {
            using var response = await client.GetAsync("SyncPlay/List", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            count = document.RootElement.GetArrayLength();
            if (count == expected)
            {
                return count;
            }

            await Task.Delay(500, cancellationToken);
        }

        return count;
    }
}
