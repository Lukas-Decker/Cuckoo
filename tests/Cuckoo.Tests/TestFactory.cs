using System.Globalization;
using System.Text.Json.Nodes;
using Cuckoo.Core;
using Cuckoo.Models;
using Cuckoo.Services;

namespace Cuckoo.Tests;

/// <summary>A do-nothing <see cref="IMinerGui"/>, so the mining core can be built without WPF.</summary>
internal sealed class NullGui : IMinerGui
{
    public bool CloseRequested => false;

    public void Print(string message) { }
    public void SetStatus(string status) { }
    public void UpdateWebsocketStatus(int index, string? status, int? topics) { }
    public void RemoveWebsocketStatus(int index) { }
    public void DisplayDrop(TimedDrop? drop, bool countdown = true, bool subone = false) { }
    public void ClearDrop() { }
    public bool MinuteAlmostDone() => false;
    public void StopTimer() { }
    public void ShowDeviceCode(string verificationUri, string userCode) { }
    public void LoginUpdate(string status, long? userId = null, string? userName = null) { }
    public void AddChannel(Channel channel) { }
    public void ClearChannels() { }
    public void RemoveChannel(Channel channel) { }
    public void SetWatching(Channel channel) { }
    public void ClearWatching() { }
    public Channel? GetSelectedChannel() => null;
    public void ClearSelectedChannel() { }
    public void ClearInventory() { }
    public void AddCampaigns(IReadOnlyList<DropsCampaign> campaigns) { }
    public void SetGames(IReadOnlyCollection<Game> games, IReadOnlySet<string> linkedGameNames) { }
    public void TrayNotify(string message, string title) { }
    public void ChangeTrayIcon(string state) { }
}

/// <summary>
/// Builds campaigns from the same GQL-shaped JSON the real inventory fetch produces,
/// so the tests exercise the actual parsing path rather than a hand-made object graph.
/// </summary>
internal static class TestFactory
{
    private static int _nextId = 1;

    public static TwitchClient Client(Settings settings) => new(settings, new NullGui());

    /// <param name="endsInMinutes">How far in the future the campaign and its drop end.</param>
    /// <param name="requiredMinutes">Watch time the drop needs, which sets the availability ratio.</param>
    public static DropsCampaign Campaign(
        TwitchClient client,
        string gameName,
        double endsInMinutes,
        bool linked = true,
        int requiredMinutes = 60)
    {
        int id = Interlocked.Increment(ref _nextId);
        string starts = Iso(DateTime.UtcNow.AddDays(-1));
        string ends = Iso(DateTime.UtcNow.AddMinutes(endsInMinutes));
        var data = new JsonObject
        {
            ["id"] = $"campaign-{id}",
            ["name"] = $"{gameName} campaign",
            ["status"] = "ACTIVE",
            ["startAt"] = starts,
            ["endAt"] = ends,
            ["self"] = new JsonObject { ["isAccountConnected"] = linked },
            ["game"] = new JsonObject
            {
                ["id"] = id.ToString(CultureInfo.InvariantCulture),
                ["displayName"] = gameName,
                ["boxArtURL"] = "",
            },
            ["timeBasedDrops"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = $"drop-{id}",
                    ["name"] = $"{gameName} drop",
                    ["startAt"] = starts,
                    ["endAt"] = ends,
                    ["requiredMinutesWatched"] = requiredMinutes,
                    ["benefitEdges"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["benefit"] = new JsonObject
                            {
                                ["id"] = $"benefit-{id}",
                                ["name"] = $"{gameName} reward",
                                ["distributionType"] = "DIRECT_ENTITLEMENT",
                            },
                        },
                    },
                    ["self"] = new JsonObject
                    {
                        ["isClaimed"] = false,
                        ["currentMinutesWatched"] = 0,
                    },
                },
            },
        };
        return new DropsCampaign(client, data, new Dictionary<string, DateTime>());
    }

    private static string Iso(DateTime value)
        => value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
