using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Cuckoo.Core;

public sealed record ClientInfo(string ClientUrl, string ClientId, string[] UserAgents)
{
    public string UserAgent { get; } = UserAgents[Random.Shared.Next(UserAgents.Length)];
}

public static class ClientType
{
    public static readonly ClientInfo AndroidApp = new(
        "https://www.twitch.tv",
        "kd1unb4b3q4t58fwlpcbzcbnm76a8fp",
        [
            "Dalvik/2.1.0 (Linux; U; Android 16; SM-S911B Build/TP1A.220624.014) tv.twitch.android.app/25.3.0/2503006",
            "Dalvik/2.1.0 (Linux; U; Android 16; SM-S938B Build/BP2A.250605.031) tv.twitch.android.app/25.3.0/2503006",
            "Dalvik/2.1.0 (Linux; Android 16; SM-X716N Build/UP1A.231005.007) tv.twitch.android.app/25.3.0/2503006",
            "Dalvik/2.1.0 (Linux; U; Android 15; SM-G990B Build/AP3A.240905.015.A2) tv.twitch.android.app/25.3.0/2503006",
            "Dalvik/2.1.0 (Linux; U; Android 15; SM-G970F Build/AP3A.241105.008) tv.twitch.android.app/25.3.0/2503006",
            "Dalvik/2.1.0 (Linux; U; Android 15; SM-A566E Build/AP3A.240905.015.A2) tv.twitch.android.app/25.3.0/2503006",
            "Dalvik/2.1.0 (Linux; U; Android 14; SM-X306B Build/UP1A.231005.007) tv.twitch.android.app/25.3.0/2503006",
        ]);

    public static readonly ClientInfo SmartBox = new(
        "https://android.tv.twitch.tv",
        "ue6666qo983tsx6so1t0vnawi233wa",
        [
            "Mozilla/5.0 (Linux; Android 7.1; Smart Box C1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36",
        ]);
}

public static class Constants
{
    public const int MaxWebsockets = 8;
    public const int WsTopicsLimit = 50;
    public const int BaseTopics = 2;
    public const int TopicsPerChannel = 2;
    public const int MaxTopics = MaxWebsockets * WsTopicsLimit - BaseTopics;
    public const int MaxChannels = MaxTopics / TopicsPerChannel;
    public const int MaxExtraMinutes = 15;

    public static readonly TimeSpan PingInterval = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan OnlineDelay = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(59);

    public const string GqlUrl = "https://gql.twitch.tv/gql";
    public const string WebsocketUrl = "wss://pubsub-edge.twitch.tv/v1";

    public static readonly string WorkingDir = AppContext.BaseDirectory;
    public static readonly string SettingsPath = Path.Combine(WorkingDir, "settings.json");
    public static readonly string AuthPath = Path.Combine(WorkingDir, "auth.json");

    /// <summary>
    /// Stable identity for this install folder (16 hex chars of a SHA-256 over the
    /// lowercased working directory). Used to scope the single-instance mutex and the
    /// per-instance autostart entries, so separate copies never collide.
    /// </summary>
    public static readonly string InstanceId = Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(
            WorkingDir.TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant())))[..16];

    /// <summary>Name of this install's folder, for human-readable labels.</summary>
    public static readonly string InstanceFolderName =
        new DirectoryInfo(WorkingDir.TrimEnd(Path.DirectorySeparatorChar)).Name;
}

/// <summary>
/// Persisted GQL query definitions. Single source of truth for operation names and SHA256 hashes.
/// </summary>
public static class GqlQueries
{
    /// <summary>Returns stream information for a particular channel. Vars: channel (login).</summary>
    public static GqlOperation GetStreamInfo => new(
        "VideoPlayerStreamInfoOverlayChannel",
        "198492e0857f6aedead9665c81c5a06d67b25b58034649687124083ff288597d");

    /// <summary>Claims a drop. Vars: input.dropInstanceID.</summary>
    public static GqlOperation ClaimDrop => new(
        "DropsPage_ClaimDropRewards",
        "a455deea71bdc9015b78eb49f4acfbce8baa7ccbedd28e549bb025bd0f751930");

    /// <summary>Returns all in-progress campaigns.</summary>
    public static GqlOperation Inventory => new(
        "Inventory",
        "8337eb8541b314040b0edde0c09c5c7a2783ba1960aa9edfbf3bac16d0fec404",
        new JsonObject { ["fetchRewardCampaigns"] = false });

    /// <summary>Returns the current drop mining progress. Vars: channelID.</summary>
    public static GqlOperation CurrentDrop => new(
        "DropCurrentSessionContext",
        "4d06b702d25d652afb9ef835d2a550031f1cf762b193523a92166f40ea3d142b",
        new JsonObject { ["channelLogin"] = "" });

    /// <summary>Returns all available campaigns.</summary>
    public static GqlOperation Campaigns => new(
        "ViewerDropsDashboard",
        "d9cae7761dafab85908c85e6683cb4201b449e66ac3bb5e894f15ff12aeafaa7",
        new JsonObject { ["fetchRewardCampaigns"] = false });

    /// <summary>Returns extended information about a campaign. Vars: channelLogin (user id), dropID.</summary>
    public static GqlOperation CampaignDetails => new(
        "DropCampaignDetails",
        "039277bf98f3130929262cc7c6efd9c141ca3749cb6dca442fc8ead9a53f77c1");

    /// <summary>Returns drops available for a particular channel. Vars: channelID.</summary>
    public static GqlOperation AvailableDrops => new(
        "DropsHighlightService_AvailableDrops",
        "782dad0f032942260171d2d80a654f88bdd0c5a9dddc392e9bc92218a0f42d20");

    /// <summary>Returns live channels for a particular game. Vars: slug, limit, options.</summary>
    public static GqlOperation GameDirectory => new(
        "DirectoryPage_Game",
        "86bcceb4e8b1a51256ff8eed8bd8aae4acacf80d737efe904f84f3aeadf8cafd",
        new JsonObject
        {
            ["limit"] = 30,
            ["imageWidth"] = 50,
            ["includeCostreaming"] = false,
            ["options"] = new JsonObject
            {
                ["broadcasterLanguages"] = new JsonArray(),
                ["freeformTags"] = null,
                ["includeRestricted"] = new JsonArray("SUB_ONLY_LIVE"),
                ["recommendationsContext"] = new JsonObject { ["platform"] = "web" },
                ["sort"] = "RELEVANCE",
                ["systemFilters"] = new JsonArray(),
                ["tags"] = new JsonArray(),
                ["requestID"] = "JIRA-VXP-2397",
            },
            ["sortTypeIsRecency"] = false,
        });

    /// <summary>Deletes an on-site notification. Vars: input.id.</summary>
    public static GqlOperation NotificationsDelete => new(
        "OnsiteNotifications_DeleteNotification",
        "13d463c831f28ffe17dccf55b3148ed8b3edbbd0ebadd56352f1ff0160616816");
}

/// <summary>PubSub topic name mapping.</summary>
public static class WebsocketTopics
{
    public static readonly IReadOnlyDictionary<string, string> User = new Dictionary<string, string>
    {
        ["Drops"] = "user-drop-events",
        ["Notifications"] = "onsite-notifications",
        ["CommunityPoints"] = "community-points-user-v1",
    };

    public static readonly IReadOnlyDictionary<string, string> Channel = new Dictionary<string, string>
    {
        ["StreamState"] = "video-playback-by-id",
        ["StreamUpdate"] = "broadcast-settings-update",
    };

    public static string AsString(string category, string topicName, long targetId)
    {
        var map = category == "User" ? User : Channel;
        return $"{map[topicName]}.{targetId}";
    }
}

/// <summary>A subscribed PubSub topic bound to its message processor.</summary>
public sealed class WebsocketTopic(string category, string topicName, long targetId, Func<long, JsonNode, Task> process)
    : IEquatable<WebsocketTopic>
{
    public string Id { get; } = WebsocketTopics.AsString(category, topicName, targetId);
    public long TargetId { get; } = targetId;

    public Task ProcessAsync(JsonNode message) => process(TargetId, message);

    public override string ToString() => Id;
    public bool Equals(WebsocketTopic? other) => other is not null && Id == other.Id;
    public override bool Equals(object? obj) => obj is WebsocketTopic other && Equals(other);
    public override int GetHashCode() => Id.GetHashCode();
}
