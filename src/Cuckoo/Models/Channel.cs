using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Cuckoo.Core;

namespace Cuckoo.Models;

/// <summary>A live stream on a channel.</summary>
public sealed class Stream
{
    public Channel Channel { get; }
    public long BroadcastId { get; }
    public int Viewers { get; set; }
    public bool DropsEnabled { get; set; }
    public Game? Game { get; }
    public string Title { get; }

    private Stream(Channel channel, long broadcastId, JsonNode? game, int viewers, string title)
    {
        Channel = channel;
        BroadcastId = broadcastId;
        Viewers = viewers;
        Title = title;
        // when the available-drops check is disabled, all streams are assumed drops-enabled
        DropsEnabled = !channel.Client.Settings.AvailableDropsCheck;
        Game = game is not null ? new Game(game) : null;
    }

    public static Stream FromGetStream(Channel channel, JsonNode channelData)
    {
        var stream = channelData["stream"]!;
        var settings = channelData["broadcastSettings"]!;
        return new Stream(
            channel,
            long.Parse(stream["id"]!.GetValue<string>()),
            settings["game"],
            stream["viewersCount"]!.GetValue<int>(),
            settings["title"]?.GetValue<string>() ?? "");
    }

    public static Stream FromDirectory(Channel channel, JsonNode data, bool dropsEnabled = false)
    {
        var stream = new Stream(
            channel,
            long.Parse(data["id"]!.GetValue<string>()),
            data["game"],
            data["viewersCount"]!.GetValue<int>(),
            data["title"]?.GetValue<string>() ?? "");
        stream.DropsEnabled = dropsEnabled;
        return stream;
    }

    /// <summary>The common "minute-watched" event payload, shared by both watch methods.</summary>
    private JsonArray BuildWatchEvents(long userId) => new(
        new JsonObject
        {
            ["event"] = "minute-watched",
            ["properties"] = new JsonObject
            {
                ["broadcast_id"] = BroadcastId.ToString(),
                ["channel_id"] = Channel.Id.ToString(),
                ["channel"] = Channel.Login,
                ["client_time"] = Utils.IsoNow(),
                ["game"] = Game?.Name ?? "",
                ["game_id"] = Game?.Id.ToString() ?? "",
                ["hidden"] = false,
                ["is_live"] = true,
                ["live"] = true,
                ["logged_in"] = true,
                ["minutes_logged"] = 1,
                ["muted"] = false,
                ["user_id"] = userId,
            }
        });

    /// <summary>The watch payload for the GQL sendSpadeEvents mutation (gzip + base64).</summary>
    public JsonObject BuildGqlWatchPayload(long userId)
        => SpadeEvents.Build(BuildWatchEvents(userId));

    /// <summary>The watch payload for the Spade endpoint: plain base64 form value.</summary>
    public string BuildSpadeWatchData(long userId)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            BuildWatchEvents(userId).ToJsonString(Utils.MinifiedJson)));
}

/// <summary>
/// A tracked Twitch channel; also acts as the row
/// item for the channel list (observable for WPF binding).
/// </summary>
public sealed partial class Channel : ObservableObject, IEquatable<Channel>
{
    public TwitchClient Client { get; }
    public long Id { get; }
    public string Login { get; }
    private string? _displayName;
    private Stream? _stream;
    private string? _spadeUrl;
    private CancellationTokenSource? _pendingStreamUp;

    /// <summary>
    /// ACL-based channels are considered first when switching, and are not
    /// cleaned up unless they stream a game we haven't selected.
    /// </summary>
    public bool AclBased { get; }

    /// <summary>
    /// Broadcast language preference tier: 0 = user's language (or language
    /// preference disabled), 1 = English, 2 = any other language. Lower sorts first,
    /// and within a tier channels are ordered by viewer count.
    /// </summary>
    public int LanguageTier { get; set; }

    [ObservableProperty]
    private bool _isWatching;

    public Channel(TwitchClient client, long id, string login, string? displayName = null, bool aclBased = false)
    {
        Client = client;
        Id = id;
        Login = login;
        _displayName = displayName;
        AclBased = aclBased;
    }

    public static Channel FromAcl(TwitchClient client, JsonNode data) => new(
        client,
        long.Parse(data["id"]!.GetValue<string>()),
        data["name"]!.GetValue<string>(),
        data["displayName"]?.GetValue<string>(),
        aclBased: true);

    public static Channel FromDirectory(TwitchClient client, JsonNode data, bool dropsEnabled = false)
    {
        var broadcaster = data["broadcaster"]!;
        var channel = new Channel(
            client,
            long.Parse(broadcaster["id"]!.GetValue<string>()),
            broadcaster["login"]!.GetValue<string>(),
            broadcaster["displayName"]?.GetValue<string>());
        channel._stream = Stream.FromDirectory(channel, data, dropsEnabled);
        return channel;
    }

    public string Name => _displayName ?? Login;
    public string Url => $"https://www.twitch.tv/{Login}";

    public Stream? Stream => _stream;
    public bool Online => _stream is not null;
    public bool Offline => _stream is null && _pendingStreamUp is null;
    public bool PendingOnline => _stream is null && _pendingStreamUp is not null;

    public Game? Game => _stream?.Game;

    public int? Viewers
    {
        get => _stream?.Viewers;
        set
        {
            if (_stream is not null && value.HasValue)
                _stream.Viewers = value.Value;
        }
    }

    public bool DropsEnabled => _stream?.DropsEnabled ?? false;

    // Binding helpers for the channel list
    public string StatusText => Online ? "ONLINE" : PendingOnline ? "PENDING" : "OFFLINE";
    public string GameName => Game?.Name ?? "";
    public string ViewersText => Viewers?.ToString("N0") ?? "";
    public string DropsText => Online ? (DropsEnabled ? "✔" : "❌") : "";

    /// <summary>Refreshes all bound row values.</summary>
    public void Display()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(GameName));
        OnPropertyChanged(nameof(ViewersText));
        OnPropertyChanged(nameof(DropsText));
    }

    public void Remove()
    {
        _pendingStreamUp?.Cancel();
        _pendingStreamUp = null;
        Client.Gui.RemoveChannel(this);
    }

    public GqlOperation StreamGql
        => GqlQueries.GetStreamInfo.WithVariables(new JsonObject { ["channel"] = Login });

    private bool CheckDropsEnabled(JsonArray availableDrops)
        => availableDrops.Any(campaignData =>
            campaignData?["id"] is JsonNode idNode
            && Client.GetCampaign(idNode.GetValue<string>()) is { } campaign
            && campaign.CanEarn(this, ignoreChannelStatus: true));

    /// <summary>Bulk update of stream info based on externally provided data.</summary>
    public void ExternalUpdate(JsonNode channelData, JsonArray availableDrops)
    {
        if (channelData["stream"] is null)
        {
            _stream = null;
            Display();
            return;
        }
        var stream = Stream.FromGetStream(this, channelData);
        if (!stream.DropsEnabled)
            stream.DropsEnabled = CheckDropsEnabled(availableDrops);
        _stream = stream;
        Display();
    }

    public async Task<Stream?> GetStreamAsync()
    {
        JsonNode response;
        try
        {
            response = await Client.GqlRequestAsync(StreamGql).ConfigureAwait(false);
        }
        catch (MinerException exc)
        {
            throw new MinerException($"Channel: {Login}", exc);
        }
        JsonNode? channelData = response["data"]!["user"];
        if (channelData is null)
            return null;
        _displayName ??= channelData["displayName"]?.GetValue<string>();
        if (channelData["stream"] is null)
            return null;
        var stream = Stream.FromGetStream(this, channelData);
        if (!stream.DropsEnabled)
        {
            try
            {
                JsonNode availableResponse = await Client.GqlRequestAsync(
                    GqlQueries.AvailableDrops.WithVariables(
                        new JsonObject { ["channelID"] = Id.ToString() })).ConfigureAwait(false);
                var availableDrops = availableResponse["data"]?["channel"]?["viewerDropCampaigns"] as JsonArray;
                stream.DropsEnabled = CheckDropsEnabled(availableDrops ?? []);
            }
            catch (MinerException)
            {
                // AvailableDrops call failed: keep drops_enabled as false
            }
        }
        return stream;
    }

    /// <summary>Fetches the current stream and updates channel status.</summary>
    public async Task<bool> UpdateStreamAsync()
    {
        Stream? oldStream = _stream;
        _stream = await GetStreamAsync().ConfigureAwait(false);
        Client.OnChannelUpdate(this, oldStream, _stream);
        return _stream is not null;
    }

    private async Task OnlineDelayAsync(CancellationToken ct)
    {
        // the 'stream-up' event is sent before the stream actually goes online,
        // so wait a bit and then check whether it's actually online
        await Task.Delay(Constants.OnlineDelay, ct).ConfigureAwait(false);
        _pendingStreamUp = null; // clear before update, so Display() reports correctly
        await UpdateStreamAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Sets up a delayed online check. Called when we receive an event indicating
    /// the channel status may be ONLINE, or needs an update.
    /// </summary>
    public void CheckOnline()
    {
        if (_pendingStreamUp is not null)
            return;
        var cts = new CancellationTokenSource();
        _pendingStreamUp = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await OnlineDelayAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (ExitRequestException) { }
            catch (Exception ex)
            {
                Client.LogException("Channel online check", ex);
            }
        }, CancellationToken.None);
        Display();
    }

    /// <summary>Sets the channel status to OFFLINE, cancelling a pending online check.</summary>
    public void SetOffline()
    {
        bool needsDisplay = false;
        if (_pendingStreamUp is not null)
        {
            _pendingStreamUp.Cancel();
            _pendingStreamUp = null;
            needsDisplay = true;
        }
        if (Online)
        {
            Stream? oldStream = _stream;
            _stream = null;
            Client.OnChannelUpdate(this, oldStream, null);
            needsDisplay = false; // OnChannelUpdate always calls Display at the end
        }
        if (needsDisplay)
            Display();
    }

    [GeneratedRegex(@"src=""(https://[\w.]+/config/settings\.[0-9a-f]{32}\.js)""", RegexOptions.IgnoreCase)]
    private static partial Regex SettingsJsRegex();

    [GeneratedRegex(@"""spade_?url"": ?""(https://[.\w\-/]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex SpadeUrlRegex();

    /// <summary>
    /// Extracts the Spade telemetry URL by walking a chain of requests:
    /// streamer page (HTML) -> streamer settings (JavaScript) -> spade URL.
    /// For the mobile view, the spade URL is in the page directly, skipping step #2.
    /// </summary>
    private async Task<string> GetSpadeUrlAsync()
    {
        string streamerHtml;
        using (HttpResponseMessage response = await Client.RequestAsync(
            HttpMethod.Get, Url).ConfigureAwait(false))
        {
            streamerHtml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        Match match = SpadeUrlRegex().Match(streamerHtml);
        if (!match.Success)
        {
            Match settingsMatch = SettingsJsRegex().Match(streamerHtml);
            if (!settingsMatch.Success)
                throw new MinerException("Error while spade_url extraction: step #1");
            string settingsJs;
            using (HttpResponseMessage response = await Client.RequestAsync(
                HttpMethod.Get, settingsMatch.Groups[1].Value).ConfigureAwait(false))
            {
                settingsJs = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            match = SpadeUrlRegex().Match(settingsJs);
            if (!match.Success)
                throw new MinerException("Error while spade_url extraction: step #2");
        }
        return match.Groups[1].Value;
    }

    /// <summary>
    /// Sends the watch event using the configured method (switchable on the fly).
    /// Returns true on success.
    /// </summary>
    public Task<bool> SendWatchAsync()
        => Client.Settings.WatchMethod == WatchMethod.Gql
            ? SendWatchGqlAsync()
            : SendWatchSpadeAsync();

    /// <summary>POSTs the watch event to the per-channel Spade endpoint.</summary>
    private async Task<bool> SendWatchSpadeAsync()
    {
        if (_stream is null)
            return false;
        try
        {
            if (_spadeUrl is null)
            {
                _spadeUrl = await GetSpadeUrlAsync().ConfigureAwait(false);
                Client.LogDebug($"Spade URL for {Login}: {_spadeUrl}");
            }
            using HttpResponseMessage response = await Client.RequestAsync(
                HttpMethod.Post,
                _spadeUrl,
                form: new Dictionary<string, string>
                {
                    ["data"] = _stream.BuildSpadeWatchData(Client.Auth.UserId),
                }).ConfigureAwait(false);
            return response.StatusCode == System.Net.HttpStatusCode.NoContent;
        }
        catch (RequestException)
        {
            return false;
        }
        catch (MinerException ex)
        {
            // spade URL extraction failed: retry the extraction on the next tick
            Client.LogWarning($"Spade watch failed for {Login}: {ex.Message}");
            _spadeUrl = null;
            return false;
        }
    }

    /// <summary>Sends the watch event via the sendSpadeEvents GQL mutation.</summary>
    private async Task<bool> SendWatchGqlAsync()
    {
        if (_stream is null)
            return false;
        try
        {
            JsonNode response = await Client.GqlRequestRawAsync(
                _stream.BuildGqlWatchPayload(Client.Auth.UserId)).ConfigureAwait(false);
            return response["data"]?["sendSpadeEvents"]?["statusCode"]?.GetValue<int>() == 204;
        }
        catch (RequestException)
        {
            return false;
        }
    }

    public bool Equals(Channel? other) => other is not null && Id == other.Id;
    public override bool Equals(object? obj) => obj is Channel other && Equals(other);
    public override int GetHashCode() => Id.GetHashCode();
    public override string ToString() => $"Channel({Name}, {Id})";
}
