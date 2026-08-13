using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Cuckoo.Models;
using Cuckoo.Services;
using Stream = Cuckoo.Models.Stream;

namespace Cuckoo.Core;

/// <summary>
/// The main mining client: authentication, GQL, the state machine, the watch loop
/// and campaign/channel management.
/// </summary>
public sealed class TwitchClient
{
    public Settings Settings { get; }
    public IMinerGui Gui { get; }
    public ClientInfo ClientInfo { get; } = ClientType.AndroidApp;
    public AuthState Auth { get; }
    public WebsocketPool Websocket { get; }
    public NotificationService Notifications { get; }

    // state management
    private volatile MinerState _state = MinerState.Idle;
    private readonly AsyncManualResetEvent _stateChange = new();
    public List<Game> WantedGames { get; } = [];
    public List<DropsCampaign> Inventory { get; } = [];
    private readonly Dictionary<string, TimedDrop> _drops = [];
    private readonly Dictionary<string, DropsCampaign> _campaigns = [];
    private readonly Queue<DateTime> _mntTriggers = new();

    // NOTE: GQL is pretty volatile and breaks everything if one runs into their rate limit.
    // Do not modify the default, safe values.
    private readonly RateLimiter _gqlLimiter = new(capacity: 5, window: 1);

    private HttpClient? _session;

    // channels and watching
    public Dictionary<long, Channel> Channels { get; } = [];
    public AwaitableValue<Channel> WatchingChannel { get; } = new();
    private CancellationTokenSource? _watchingCts;
    private Task? _watchingTask;
    private readonly AsyncManualResetEvent _watchingRestart = new();

    // maintenance task
    private CancellationTokenSource? _mntCts;
    private Task? _mntTask;

    public TwitchClient(Settings settings, IMinerGui gui)
    {
        Settings = settings;
        Gui = gui;
        Auth = new AuthState(this);
        Websocket = new WebsocketPool(this);
        // identity for remote notifications: the logged-in account name; before login
        // (e.g. "login required" messages) the instance folder name still tells instances apart
        Notifications = new NotificationService(settings, LogWarning, () =>
            !string.IsNullOrEmpty(Auth.UserName)
                ? Auth.UserName
                : new DirectoryInfo(Constants.WorkingDir.TrimEnd(Path.DirectorySeparatorChar)).Name);
    }

    // campaign IDs already notified as completed, reset on each inventory fetch
    private readonly HashSet<string> _completedNotified = [];

    public void NotifyDropClaimed(TimedDrop drop)
        => Notifications.Send(
            NotificationCategory.DropClaimed,
            "Drop claimed",
            $"{drop.RewardsText()}\n{drop.Campaign.Game.Name} " +
            $"({drop.Campaign.ClaimedDrops}/{drop.Campaign.TotalDrops})");

    public void NotifyCampaignMaybeCompleted(DropsCampaign campaign)
    {
        if (campaign.Finished && _completedNotified.Add(campaign.Id))
        {
            Notifications.Send(
                NotificationCategory.CampaignCompleted,
                "Campaign completed",
                $"{campaign.Game.Name}: {campaign.Name}\nAll {campaign.TotalDrops} drops mined.");
        }
    }

    public void NotifyStatus(string message)
        => Notifications.Send(NotificationCategory.MiningStatus, "Mining status", message);

    public void NotifyError(string message)
        => Notifications.Send(NotificationCategory.Errors, "Cuckoo", message);

    #region Logging

    public void LogTrace(string message) => Logger.Instance.Trace(message);
    public void LogDebug(string message) => Logger.Instance.Debug(message);
    public void LogInfo(string message) => Logger.Instance.Info(message);
    public void LogWarning(string message) => Logger.Instance.Warning(message);
    public void LogError(string message) => Logger.Instance.Error(message);
    public void LogException(string context, Exception ex) => Logger.Instance.Exception(context, ex);

    #endregion

    #region Session and requests

    public HttpClient GetSession()
    {
        if (_session is not null)
            return _session;
        int quality = Math.Clamp(Settings.ConnectionQuality, 1, 6);
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5 * quality),
            MaxConnectionsPerServer = 50,
            AutomaticDecompression = DecompressionMethods.All,
        };
        if (!string.IsNullOrEmpty(Settings.Proxy))
            handler.Proxy = new WebProxy(Settings.Proxy);
        _session = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10 * quality),
        };
        _session.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ClientInfo.UserAgent);
        return _session;
    }

    /// <summary>
    /// Performs a web request with exponential-backoff retrying on connection
    /// problems and 5xx responses.
    /// </summary>
    public async Task<HttpResponseMessage> RequestAsync(
        HttpMethod method,
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        IReadOnlyDictionary<string, string>? form = null,
        string? jsonBody = null,
        DateTime? invalidateAfter = null)
    {
        HttpClient session = GetSession();
        TimeSpan sessionTimeout = session.Timeout;
        var backoff = new ExponentialBackoff(maximum: 3 * 60);
        while (true)
        {
            double delay = backoff.Next();
            if (Gui.CloseRequested)
                throw new ExitRequestException();
            if (invalidateAfter is not null
                // account for the expiration landing during the request
                && DateTime.UtcNow >= invalidateAfter.Value - sessionTimeout)
            {
                throw new RequestInvalidException();
            }
            try
            {
                LogTrace($"Request: {method} {url}");
                using var request = new HttpRequestMessage(method, url);
                if (headers is not null)
                {
                    foreach (var (key, value) in headers)
                        request.Headers.TryAddWithoutValidation(key, value);
                }
                if (form is not null)
                    request.Content = new FormUrlEncodedContent(form);
                else if (jsonBody is not null)
                    request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await session
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead)
                    .ConfigureAwait(false);
                if ((int)response.StatusCode < 500)
                {
                    // pre-read the response to avoid errors later on
                    await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                    return response;
                }
                response.Dispose();
                LogWarning($"Request: {method} {url} returned a 5xx, retrying in {Math.Round(delay)}s");
                Gui.Print($"Twitch is having issues, retrying in {Math.Round(delay)}s...");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                LogDebug($"Request connection problem: {method} {url} ({ex.GetType().Name}: {ex.Message})");
                if (backoff.Steps > 1)
                {
                    // quick retries that sometimes happen aren't shown
                    Gui.Print($"Connection problem, retrying in {Math.Round(delay)}s... ({url})");
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
        }
    }

    public async Task<JsonNode> GqlRequestAsync(GqlOperation operation)
    {
        JsonNode result = await GqlRequestCoreAsync(operation.ToJson(), isList: false).ConfigureAwait(false);
        return result;
    }

    public async Task<List<JsonNode>> GqlRequestAsync(IReadOnlyList<GqlOperation> operations)
    {
        var payload = new JsonArray([.. operations.Select(op => (JsonNode)op.ToJson())]);
        JsonNode result = await GqlRequestCoreAsync(payload, isList: true).ConfigureAwait(false);
        return [.. ((JsonArray)result).Select(n => n!)];
    }

    /// <summary>Sends a raw (non-persisted) GQL payload, e.g. the spade events mutation.</summary>
    public Task<JsonNode> GqlRequestRawAsync(JsonObject payload)
        => GqlRequestCoreAsync(payload, isList: false);

    private async Task<JsonNode> GqlRequestCoreAsync(JsonNode payload, bool isList)
    {
        if (payload is JsonArray batch)
            LogTrace($"GQL batch request ({batch.Count} ops): "
                + string.Join(", ", batch.Take(5).Select(op => op?["operationName"]?.GetValue<string>() ?? "raw")));
        else
            LogTrace($"GQL request: {payload["operationName"]?.GetValue<string>() ?? "raw mutation"}");
        var backoff = new ExponentialBackoff(maximum: 60);
        // retry the request a single time, if a specific set of errors is encountered
        bool singleRetry = true;
        while (true)
        {
            double delay = backoff.Next();
            JsonNode responseJson;
            using (await _gqlLimiter.EnterAsync().ConfigureAwait(false))
            {
                await Auth.ValidateAsync().ConfigureAwait(false);
                using HttpResponseMessage response = await RequestAsync(
                    HttpMethod.Post,
                    Constants.GqlUrl,
                    headers: Auth.Headers(userAgent: ClientInfo.UserAgent, gql: true),
                    jsonBody: payload.ToJsonString(Utils.MinifiedJson)).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // our access token has expired mid-session:
                    // invalidate it and let the next Auth.ValidateAsync re-login (refresh flow)
                    LogWarning("GQL request unauthorized, refreshing the session");
                    Auth.Invalidate();
                    await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
                    continue;
                }
                responseJson = JsonNode.Parse(
                    await response.Content.ReadAsStringAsync().ConfigureAwait(false))!;
            }
            List<JsonNode> responseList = responseJson is JsonArray arr
                ? [.. arr.Select(n => n!)]
                : [responseJson];
            bool forceRetry = false;
            foreach (JsonNode item in responseList)
            {
                if (item["errors"] is JsonArray errors)
                {
                    bool handled = false;
                    foreach (JsonNode? errorNode in errors)
                    {
                        string? message = errorNode?["message"]?.GetValue<string>();
                        if (message is null)
                            continue;
                        if (singleRetry && message is "service error" or "PersistedQueryNotFound")
                        {
                            LogError($"Retrying a '{message}' GQL error");
                            singleRetry = false;
                            if (delay < 5)
                                delay = 5;
                            forceRetry = true;
                            handled = true;
                            break;
                        }
                        if (message == "server error")
                        {
                            // nullify the key the error path points to
                            if (errorNode?["path"] is JsonArray path && path.Count > 0
                                && item["data"] is JsonNode dataNode)
                            {
                                JsonNode? current = dataNode;
                                for (int i = 0; i < path.Count - 1 && current is not null; i++)
                                    current = current[path[i]!.GetValue<string>()];
                                if (current is JsonObject currentObj)
                                    currentObj[path[^1]!.GetValue<string>()] = null;
                            }
                            handled = true;
                            break;
                        }
                        if (message is "service timeout" or "service unavailable" or "context deadline exceeded")
                        {
                            forceRetry = true;
                            handled = true;
                            break;
                        }
                    }
                    if (!handled)
                        throw new GqlException(errors.ToJsonString());
                    if (forceRetry)
                        break;
                }
                else if (item["error"] is JsonNode errorValue)
                {
                    throw new GqlException($"{errorValue}: {item["message"]}");
                }
            }
            if (!forceRetry)
                return responseJson;
            await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
        }
    }

    #endregion

    #region State management

    public void ChangeState(MinerState state)
    {
        if (_state != MinerState.Exit)
        {
            if (_state != state)
                LogDebug($"State change: {_state} -> {state}");
            // prevent state changing once we switch to the exit state
            _state = state;
        }
        _stateChange.Set();
    }

    /// <summary>Called when the application is requested to close by the user.</summary>
    public void Close()
    {
        ChangeState(MinerState.Exit);
        _watchingRestart.Set();
    }

    /// <summary>
    /// Logs the current account out: invalidates and deletes the stored token,
    /// then performs a full reload, which restarts the login flow.
    /// </summary>
    public void Logout()
    {
        if (!Auth.IsLoggedIn)
            return;
        Gui.Print("Logging out...");
        Auth.Invalidate(deleteToken: true);
        ChangeState(MinerState.Restart);
    }

    public DropsCampaign? GetCampaign(string id) => _campaigns.GetValueOrDefault(id);

    public void Save(bool force = false) => Settings.Save(force);

    /// <summary>0 is the highest priority; int.MaxValue is the lowest possible one.</summary>
    public int GetPriority(Channel channel)
    {
        Game? game = channel.Game;
        if (game is null)
            return int.MaxValue;
        int index = WantedGames.IndexOf(game);
        return index < 0 ? int.MaxValue : index;
    }

    #endregion

    #region Main loop

    public async Task RunAsync()
    {
        while (true)
        {
            try
            {
                await RunInnerAsync().ConfigureAwait(false);
                break;
            }
            catch (ReloadRequestException)
            {
                await ShutdownAsync().ConfigureAwait(false);
            }
            catch (ExitRequestException)
            {
                break;
            }
        }
    }

    private async Task RunInnerAsync()
    {
        await Auth.ValidateAsync().ConfigureAwait(false);
        await Websocket.StartAsync().ConfigureAwait(false);

        // NOTE: the watch task is explicitly restarted on each new run
        _watchingCts?.Cancel();
        _watchingCts = new CancellationTokenSource();
        _watchingTask = Task.Run(() => WatchLoopAsync(_watchingCts.Token));

        // add default topics
        Websocket.AddTopics([
            new WebsocketTopic("User", "Drops", Auth.UserId, ProcessDropsAsync),
            new WebsocketTopic("User", "Notifications", Auth.UserId, ProcessNotificationsAsync),
        ]);

        bool fullCleanup = false;
        ChangeState(MinerState.InventoryFetch);
        while (true)
        {
            switch (_state)
            {
                case MinerState.Idle:
                {
                    Gui.ChangeTrayIcon("idle");
                    Gui.SetStatus("Idle");
                    StopWatching();
                    NotifyStatus("Idle - nothing to mine right now.");
                    // clear the flag and wait until it's set again
                    _stateChange.Reset();
                    break;
                }
                case MinerState.InventoryFetch:
                {
                    Gui.ChangeTrayIcon("maint");
                    await Websocket.StartAsync().ConfigureAwait(false);
                    await FetchInventoryAsync().ConfigureAwait(false);
                    Gui.SetGames(
                        Inventory.Select(c => c.Game).Distinct().ToList(),
                        Inventory.Where(c => c.Linked).Select(c => c.Game.Name).ToHashSet());
                    // save state on every inventory fetch
                    Save();
                    ChangeState(MinerState.GamesUpdate);
                    break;
                }
                case MinerState.GamesUpdate:
                {
                    // claim drops from expired and active campaigns
                    foreach (DropsCampaign campaign in Inventory.Where(c => !c.Upcoming))
                    {
                        foreach (TimedDrop drop in campaign.Drops.Where(d => d.CanClaim))
                            await drop.ClaimAsync().ConfigureAwait(false);
                    }
                    // figure out which games we want, in mining order
                    WantedGames.Clear();
                    DateTime nextHour = DateTime.UtcNow + TimeSpan.FromHours(1);
                    foreach (DropsCampaign campaign in MiningOrder.Sort(Inventory, Settings))
                    {
                        Game game = campaign.Game;
                        if (!WantedGames.Contains(game)
                            && !Settings.IsExcluded(game.Name)
                            && MiningOrder.Includes(campaign, Settings)
                            && campaign.CanEarnWithin(nextHour))
                        {
                            WantedGames.Add(game);
                        }
                    }
                    fullCleanup = true;
                    RestartWatching();
                    ChangeState(MinerState.ChannelsCleanup);
                    break;
                }
                case MinerState.ChannelsCleanup:
                {
                    Gui.SetStatus("Cleaning up channels...");
                    List<Channel> toRemoveChannels;
                    if (WantedGames.Count == 0 || fullCleanup)
                    {
                        // no games selected or we're doing a full cleanup: remove everything
                        toRemoveChannels = [.. Channels.Values];
                    }
                    else
                    {
                        toRemoveChannels = [.. Channels.Values.Where(channel =>
                            !channel.AclBased
                            && (channel.Offline
                                || channel.Game is null
                                || !WantedGames.Contains(channel.Game)))];
                    }
                    fullCleanup = false;
                    if (toRemoveChannels.Count > 0)
                    {
                        var toRemoveTopics = new List<string>();
                        foreach (Channel channel in toRemoveChannels)
                        {
                            toRemoveTopics.Add(WebsocketTopics.AsString("Channel", "StreamState", channel.Id));
                            toRemoveTopics.Add(WebsocketTopics.AsString("Channel", "StreamUpdate", channel.Id));
                        }
                        Websocket.RemoveTopics(toRemoveTopics);
                        foreach (Channel channel in toRemoveChannels)
                        {
                            Channels.Remove(channel.Id);
                            channel.Remove();
                        }
                    }
                    if (WantedGames.Count > 0)
                    {
                        ChangeState(MinerState.ChannelsFetch);
                    }
                    else
                    {
                        Gui.Print("No active campaigns to mine drops for.");
                        ChangeState(MinerState.Idle);
                    }
                    break;
                }
                case MinerState.ChannelsFetch:
                {
                    Gui.SetStatus("Gathering channels...");
                    // start with all current channels; clear the memory and GUI
                    var newChannels = new HashSet<Channel>(Channels.Values);
                    Channels.Clear();
                    Gui.ClearChannels();
                    // gather and add ACL channels from campaigns that can be progressed
                    var noAcl = new HashSet<Game>();
                    var aclChannels = new HashSet<Channel>();
                    DateTime nextHour = DateTime.UtcNow + TimeSpan.FromHours(1);
                    foreach (DropsCampaign campaign in Inventory)
                    {
                        if (WantedGames.Contains(campaign.Game) && campaign.CanEarnWithin(nextHour))
                        {
                            if (campaign.AllowedChannels.Count > 0)
                                aclChannels.UnionWith(campaign.AllowedChannels);
                            else
                                noAcl.Add(campaign.Game);
                        }
                    }
                    // remove ACL channels that already exist from the other set
                    aclChannels.ExceptWith(newChannels);
                    // use the other set to set them online if possible
                    await BulkCheckOnlineAsync(aclChannels).ConfigureAwait(false);
                    newChannels.UnionWith(aclChannels);
                    foreach (Game game in noAcl)
                    {
                        // for every campaign without an ACL, add live channels with drops enabled
                        newChannels.UnionWith(
                            await GetLiveStreamsAsync(game).ConfigureAwait(false));
                    }
                    // Order: game priority, then ACL channels, then broadcast language tier
                    // (user's language, English, rest), and within each tier the most viewers.
                    // NOTE: the viewers sort also ensures ONLINE channels sort to the top
                    List<Channel> orderedChannels = [.. newChannels
                        .OrderBy(GetPriority)
                        .ThenByDescending(ch => ch.AclBased)
                        .ThenBy(ch => ch.LanguageTier)
                        .ThenByDescending(ch => ch.Viewers ?? -1)];
                    // ensure we don't end up with more channels than we can handle
                    // NOTE: we trim from the end, because that's where the non-priority,
                    // offline (or low-viewers) channels end up
                    List<Channel> trimmedChannels = [.. orderedChannels.Skip(Constants.MaxChannels)];
                    orderedChannels = [.. orderedChannels.Take(Constants.MaxChannels)];
                    if (trimmedChannels.Count > 0)
                    {
                        var toRemoveTopics = new List<string>();
                        foreach (Channel channel in trimmedChannels)
                        {
                            toRemoveTopics.Add(WebsocketTopics.AsString("Channel", "StreamState", channel.Id));
                            toRemoveTopics.Add(WebsocketTopics.AsString("Channel", "StreamUpdate", channel.Id));
                        }
                        Websocket.RemoveTopics(toRemoveTopics);
                    }
                    // set our new channel list
                    foreach (Channel channel in orderedChannels)
                    {
                        Channels[channel.Id] = channel;
                        Gui.AddChannel(channel);
                    }
                    LogInfo($"Channels gathered: {Channels.Count} tracked "
                        + $"({Channels.Values.Count(c => c.Online)} online, "
                        + $"{Channels.Values.Count(c => c.AclBased)} ACL-based), "
                        + $"wanted games: {WantedGames.Count}");
                    // subscribe to state updates of these channels
                    var toAddTopics = new List<WebsocketTopic>();
                    foreach (long channelId in Channels.Keys)
                    {
                        toAddTopics.Add(new WebsocketTopic(
                            "Channel", "StreamState", channelId, ProcessStreamStateAsync));
                        toAddTopics.Add(new WebsocketTopic(
                            "Channel", "StreamUpdate", channelId, ProcessStreamUpdateAsync));
                    }
                    Websocket.AddTopics(toAddTopics);
                    // relink the watching channel after cleanup,
                    // or stop watching it if it no longer qualifies
                    Channel? watchingChannel = WatchingChannel.GetWithDefault();
                    if (watchingChannel is not null)
                    {
                        Channel? newWatching = Channels.GetValueOrDefault(watchingChannel.Id);
                        if (newWatching is not null && CanWatch(newWatching))
                            Watch(newWatching, updateStatus: false);
                        else
                            StopWatching();
                    }
                    // pre-display the active drop with a subtracted minute
                    foreach (Channel channel in Channels.Values)
                    {
                        if (CanWatch(channel))
                        {
                            if (GetActiveCampaign(channel)?.FirstDrop is { } activeDrop)
                                activeDrop.Display(countdown: false, subone: true);
                            break;
                        }
                    }
                    ChangeState(MinerState.ChannelSwitch);
                    break;
                }
                case MinerState.ChannelSwitch:
                {
                    Gui.SetStatus("Switching the channel...");
                    // switch to the selected channel, stay in the watching channel,
                    // or select a new channel that meets the required conditions
                    Channel? newWatching = null;
                    Channel? selectedChannel = Gui.GetSelectedChannel();
                    if (selectedChannel is not null && CanWatch(selectedChannel))
                    {
                        // the selected channel is checked first, set it if we can watch it
                        newWatching = selectedChannel;
                    }
                    else
                    {
                        // other channels need a good reason for a switch
                        foreach (Channel channel in Channels.Values.OrderBy(GetPriority))
                        {
                            if (ShouldSwitch(channel))
                            {
                                newWatching = channel;
                                break;
                            }
                        }
                    }
                    Channel? currentWatching = WatchingChannel.GetWithDefault();
                    if (newWatching is not null)
                    {
                        Watch(newWatching);
                        // break the state change chain by clearing the flag
                        _stateChange.Reset();
                    }
                    else if (currentWatching is not null && CanWatch(currentWatching))
                    {
                        // continue watching what we had before
                        Gui.SetStatus($"Watching: {currentWatching.Name}");
                        _stateChange.Reset();
                    }
                    else
                    {
                        // not watching anything, and there isn't anything to watch either
                        Gui.Print("No available channels to watch.");
                        ChangeState(MinerState.Idle);
                    }
                    break;
                }
                case MinerState.Restart:
                    throw new ReloadRequestException();
                case MinerState.Exit:
                {
                    Gui.ChangeTrayIcon("cuckoo");
                    Gui.SetStatus("Exiting...");
                    // we've been requested to exit the application
                    return;
                }
            }
            await _stateChange.WaitAsync().ConfigureAwait(false);
        }
    }

    public async Task ShutdownAsync()
    {
        StopWatching();
        _watchingCts?.Cancel();
        _watchingCts = null;
        _watchingTask = null;
        _mntCts?.Cancel();
        _mntCts = null;
        _mntTask = null;
        await Websocket.StopAsync(clearTopics: true).ConfigureAwait(false);
        _session?.Dispose();
        _session = null;
        _drops.Clear();
        Channels.Clear();
        Inventory.Clear();
        _campaigns.Clear();
        Auth.Clear();
        WantedGames.Clear();
        _mntTriggers.Clear();
        Gui.ClearChannels();
        Gui.ClearInventory();
        // reset the state change flag so a fresh run starts cleanly
        _stateChange.Reset();
        _state = MinerState.Idle;
    }

    #endregion

    #region Watching

    public bool CanWatch(Channel channel)
    {
        if (!channel.Online)
            return false;
        foreach (DropsCampaign campaign in Inventory)
        {
            if (campaign.CanEarn(channel)
                && ((channel.Game is not null
                        && channel.DropsEnabled
                        && WantedGames.Contains(channel.Game))
                    // the campaign can ignore all channel-related checks
                    || campaign.Game.IsSpecial))
            {
                return true;
            }
        }
        return false;
    }

    public bool ShouldSwitch(Channel channel)
    {
        if (!CanWatch(channel))
            return false;
        Channel? watchingChannel = WatchingChannel.GetWithDefault();
        if (watchingChannel is null || !CanWatch(watchingChannel))
            return true;
        int channelOrder = GetPriority(channel);
        int watchingOrder = GetPriority(watchingChannel);
        return channelOrder < watchingOrder
            // or the order is the same, and this channel is ACL-based while the watching one isn't
            || (channelOrder == watchingOrder && channel.AclBased && !watchingChannel.AclBased);
    }

    public void Watch(Channel channel, bool updateStatus = true)
    {
        Gui.ChangeTrayIcon("active");
        Gui.SetWatching(channel);
        WatchingChannel.Set(channel);
        if (updateStatus)
        {
            string statusText = $"Watching: {channel.Name}";
            Gui.Print(statusText);
            Gui.SetStatus(statusText);
            NotifyStatus($"Now watching {channel.Name} ({channel.GameName}).");
        }
    }

    public void StopWatching()
    {
        Gui.ClearDrop();
        WatchingChannel.Clear();
        Gui.ClearWatching();
    }

    public void RestartWatching()
    {
        Gui.StopTimer();
        _watchingRestart.Set();
    }

    private async Task WatchSleepAsync(double delaySeconds, CancellationToken ct)
    {
        // a delay that RestartWatching can cut short
        _watchingRestart.Reset();
        if (delaySeconds <= 0)
            return;
        Task restart = _watchingRestart.WaitAsync();
        await Task.WhenAny(restart, Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    private async Task WatchLoopAsync(CancellationToken ct)
    {
        try
        {
            double interval = Constants.WatchInterval.TotalSeconds;
            while (!ct.IsCancellationRequested)
            {
                Channel channel = await WatchingChannel.GetAsync().WaitAsync(ct).ConfigureAwait(false);
                if (!channel.Online)
                {
                    // if the channel isn't online anymore, stop watching it
                    StopWatching();
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }
                bool succeeded = await channel.SendWatchAsync().ConfigureAwait(false);
                var lastSent = DateTime.UtcNow;
                LogTrace($"Watch payload sent to {channel.Name} via {Settings.WatchMethod}: {(succeeded ? "OK" : "FAILED")}");
                if (!succeeded)
                    LogInfo($"Watch request failed for channel: {channel.Name}");

                // wait ~20 seconds for a progress update
                await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                if (Gui.MinuteAlmostDone())
                {
                    // Twitch has temporarily stopped reporting drop progress. Use GQL to query
                    // for the current drop, or even "pretend" mining as a last resort option.
                    bool handled = false;

                    // Solution 1: use GQL to query for the currently mined drop status
                    JsonNode? dropData = null;
                    try
                    {
                        JsonNode context = await GqlRequestAsync(
                            GqlQueries.CurrentDrop.WithVariables(
                                new JsonObject { ["channelID"] = channel.Id.ToString() }))
                            .ConfigureAwait(false);
                        dropData = context["data"]?["currentUser"]?["dropCurrentSession"];
                    }
                    catch (GqlException)
                    {
                        dropData = null;
                    }
                    if (dropData is not null)
                    {
                        TimedDrop? gqlDrop = _drops.GetValueOrDefault(
                            dropData["dropID"]!.GetValue<string>());
                        if (gqlDrop is not null && gqlDrop.CanEarn(channel))
                        {
                            gqlDrop.UpdateMinutes(dropData["currentMinutesWatched"]!.GetValue<int>());
                            LogInfo(
                                $"Drop progress from GQL: {gqlDrop.Name} " +
                                $"({gqlDrop.Campaign.Game}, {gqlDrop.CurrentMinutes}/{gqlDrop.RequiredMinutes})");
                            handled = true;
                        }
                    }

                    // Solution 2: figure out which campaign we're most likely mining
                    // right now, and bump up the minutes on its drops
                    if (!handled)
                    {
                        if (GetActiveCampaign(channel) is { } activeCampaign)
                        {
                            activeCampaign.BumpMinutes(channel);
                            activeCampaign.FirstDrop?.Display();
                        }
                        else
                        {
                            LogInfo("No active drop could be determined");
                        }
                    }
                }
                double elapsed = (DateTime.UtcNow - lastSent).TotalSeconds;
                await WatchSleepAsync(interval - Math.Min(elapsed, interval), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ExitRequestException) { }
        catch (ReloadRequestException) { }
        catch (Exception ex)
        {
            // critical task: its death should trigger termination
            LogException("Watch loop (critical)", ex);
            Close();
        }
    }

    private async Task MaintenanceTaskAsync(CancellationToken ct)
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            DateTime nextPeriod = now + TimeSpan.FromHours(1);
            while (true)
            {
                now = DateTime.UtcNow;
                if (now >= nextPeriod)
                    break;
                DateTime nextTrigger = nextPeriod;
                while (_mntTriggers.Count > 0 && _mntTriggers.Peek() <= nextTrigger)
                    nextTrigger = _mntTriggers.Dequeue();
                string triggerType = nextTrigger == nextPeriod ? "Reload" : "Cleanup";
                LogInfo($"Maintenance task waiting until: {nextTrigger.ToLocalTime():T} ({triggerType})");
                await Task.Delay(nextTrigger - now, ct).ConfigureAwait(false);
                now = DateTime.UtcNow;
                if (now >= nextPeriod)
                    break;
                if (nextTrigger != nextPeriod)
                {
                    LogInfo("Maintenance task requests channels cleanup");
                    ChangeState(MinerState.ChannelsCleanup);
                }
            }
            // this triggers a reload every (up to) 60 minutes
            LogInfo("Maintenance task requests a reload");
            ChangeState(MinerState.InventoryFetch);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogException("Maintenance task (critical)", ex);
            Close();
        }
    }

    #endregion

    #region Topic processors

    private async Task ProcessStreamStateAsync(long channelId, JsonNode message)
    {
        string? msgType = message["type"]?.GetValue<string>();
        Channel? channel = Channels.GetValueOrDefault(channelId);
        if (channel is null)
        {
            LogError($"Stream state change for a non-existing channel: {channelId}");
            return;
        }
        switch (msgType)
        {
            case "viewcount":
                if (!channel.Online)
                {
                    // if it's not online for some reason, set it so
                    channel.CheckOnline();
                }
                else
                {
                    channel.Viewers = message["viewers"]?.GetValue<int>();
                    channel.Display();
                }
                break;
            case "stream-down":
                channel.SetOffline();
                break;
            case "stream-up":
                channel.CheckOnline();
                break;
            case "commercial":
                break;
            default:
                LogWarning($"Unknown stream state: {msgType}");
                break;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task ProcessStreamUpdateAsync(long channelId, JsonNode message)
    {
        Channel? channel = Channels.GetValueOrDefault(channelId);
        if (channel is null)
        {
            LogError($"Broadcast settings update for a non-existing channel: {channelId}");
            return;
        }
        // There's no tag information here, but this event is triggered when the tags change.
        // Use CheckOnline to introduce a delay, allowing multiple title/tag changes to settle.
        channel.CheckOnline();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Called by a Channel when its status is updated (ONLINE, OFFLINE, title/tags change).</summary>
    public void OnChannelUpdate(Channel channel, Stream? streamBefore, Stream? streamAfter)
    {
        if (streamBefore is null)
        {
            if (streamAfter is not null)
            {
                if (ShouldSwitch(channel))
                {
                    Gui.Print($"{channel.Name} goes ONLINE, switching...");
                    Watch(channel);
                }
                else
                {
                    LogInfo($"{channel.Name} goes ONLINE");
                }
            }
            else
            {
                LogInfo($"{channel.Name} stays OFFLINE");
            }
        }
        else
        {
            Channel? watchingChannel = WatchingChannel.GetWithDefault();
            if (watchingChannel is not null && watchingChannel.Equals(channel))
            {
                if (!CanWatch(channel))
                {
                    // we can't watch it anymore
                    if (streamAfter is null)
                        Gui.Print($"{channel.Name} goes OFFLINE, switching...");
                    else
                        LogInfo($"{channel.Name} status has been updated, switching...");
                    ChangeState(MinerState.ChannelSwitch);
                }
                // else: channel stays online and we can still watch it - no change
            }
            else if (streamAfter is null)
            {
                LogInfo($"{channel.Name} goes OFFLINE");
            }
            else
            {
                LogInfo($"{channel.Name} status has been updated");
                if (ShouldSwitch(channel))
                    Watch(channel);
            }
        }
        channel.Display();
    }

    private async Task ProcessDropsAsync(long userId, JsonNode message)
    {
        // Message examples:
        // {"type": "drop-progress", "data": {"current_progress_min": 3, "required_progress_min": 10}}
        // {"type": "drop-claim", "data": {"drop_instance_id": ...}}
        string? msgType = message["type"]?.GetValue<string>();
        if (msgType is not ("drop-progress" or "drop-claim"))
            return;
        string dropId = message["data"]!["drop_id"]!.GetValue<string>();
        TimedDrop? drop = _drops.GetValueOrDefault(dropId);
        Channel? watchingChannel = WatchingChannel.GetWithDefault();
        if (msgType == "drop-claim")
        {
            if (drop is null)
            {
                LogError(
                    $"Received a drop claim ID for a non-existing drop: {dropId}\n" +
                    $"Drop claim ID: {message["data"]!["drop_instance_id"]}");
                return;
            }
            drop.UpdateClaim(message["data"]!["drop_instance_id"]!.GetValue<string>());
            DropsCampaign campaign = drop.Campaign;
            await drop.ClaimAsync().ConfigureAwait(false);
            drop.Display();
            // About 4-20s after claiming, the next drop can be started by re-sending the watch
            // payload. Test for it by fetching the current drop via GQL and comparing drop ids.
            await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            if (watchingChannel is not null)
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    JsonNode context = await GqlRequestAsync(
                        GqlQueries.CurrentDrop.WithVariables(
                            new JsonObject { ["channelID"] = watchingChannel.Id.ToString() }))
                        .ConfigureAwait(false);
                    JsonNode? dropData = context["data"]?["currentUser"]?["dropCurrentSession"];
                    if (dropData is null || dropData["dropID"]?.GetValue<string>() != drop.Id)
                        break;
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
            }
            if (campaign.CanEarn(watchingChannel))
                RestartWatching();
            else
                ChangeState(MinerState.InventoryFetch);
            return;
        }
        // drop-progress
        if (drop is not null && drop.CanEarn(watchingChannel))
        {
            // the received payload is for the drop we expected
            drop.UpdateMinutes(message["data"]!["current_progress_min"]!.GetValue<int>());
        }
    }

    private async Task ProcessNotificationsAsync(long userId, JsonNode message)
    {
        if (message["type"]?.GetValue<string>() == "create-notification")
        {
            JsonNode data = message["data"]!["notification"]!;
            string? notificationType = data["type"]?.GetValue<string>();
            if (notificationType is "user_drop_reward_reminder_notification"
                or "quests_viewer_reward_campaign_earned_emote")
            {
                ChangeState(MinerState.InventoryFetch);
                await GqlRequestAsync(
                    GqlQueries.NotificationsDelete.WithVariables(new JsonObject
                    {
                        ["input"] = new JsonObject { ["id"] = data["id"]!.GetValue<string>() }
                    })).ConfigureAwait(false);
            }
        }
    }

    #endregion

    #region Inventory

    private static JsonObject MergeData(JsonObject primary, JsonObject secondary)
    {
        var merged = new JsonObject();
        foreach (string key in primary.Select(kv => kv.Key).Union(secondary.Select(kv => kv.Key)))
        {
            bool inPrimary = primary.ContainsKey(key);
            bool inSecondary = secondary.ContainsKey(key);
            if (inPrimary && inSecondary)
            {
                if (primary[key] is JsonObject po && secondary[key] is JsonObject so)
                    merged[key] = MergeData(po, so);
                else
                    merged[key] = primary[key]?.DeepClone();
            }
            else if (inPrimary)
            {
                merged[key] = primary[key]?.DeepClone();
            }
            else
            {
                merged[key] = secondary[key]?.DeepClone();
            }
        }
        return merged;
    }

    private async Task<JsonObject> FetchCampaignsChunkAsync(
        IReadOnlyList<KeyValuePair<string, JsonNode>> campaignsChunk)
    {
        var campaignIds = new JsonObject(
            campaignsChunk.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value.DeepClone())));
        List<JsonNode> responseList = await GqlRequestAsync([.. campaignsChunk.Select(kv =>
            GqlQueries.CampaignDetails.WithVariables(new JsonObject
            {
                ["channelLogin"] = Auth.UserId.ToString(),
                ["dropID"] = kv.Key,
            }))]).ConfigureAwait(false);
        var fetchedData = new JsonObject();
        foreach (JsonNode responseJson in responseList)
        {
            if (responseJson["data"]?["user"]?["dropCampaign"] is JsonObject campaignData)
                fetchedData[campaignData["id"]!.GetValue<string>()] = campaignData.DeepClone();
        }
        return MergeData(campaignIds, fetchedData);
    }

    private async Task FetchInventoryAsync()
    {
        Gui.SetStatus("Fetching inventory...");
        // fetch in-progress campaigns (inventory)
        JsonNode response = await GqlRequestAsync(GqlQueries.Inventory).ConfigureAwait(false);
        JsonNode? inventory = response["data"]?["currentUser"]?["inventory"];
        var ongoingCampaigns = inventory?["dropCampaignsInProgress"] as JsonArray ?? [];
        // this contains claimed benefit edge ids, not drop ids
        var claimedBenefits = new Dictionary<string, DateTime>();
        foreach (JsonNode? benefit in inventory?["gameEventDrops"] as JsonArray ?? [])
        {
            if (benefit is not null)
            {
                claimedBenefits[benefit["id"]!.GetValue<string>()] =
                    Utils.ParseTimestamp(benefit["lastAwardedAt"]!.GetValue<string>());
            }
        }
        var inventoryData = new JsonObject();
        foreach (JsonNode? c in ongoingCampaigns)
        {
            if (c is not null)
                inventoryData[c["id"]!.GetValue<string>()] = c.DeepClone();
        }
        // fetch generally available campaigns
        response = await GqlRequestAsync(GqlQueries.Campaigns).ConfigureAwait(false);
        var availableList = response["data"]?["currentUser"]?["dropCampaigns"] as JsonArray ?? [];
        var availableCampaigns = new List<KeyValuePair<string, JsonNode>>();
        foreach (JsonNode? c in availableList)
        {
            string? status = c?["status"]?.GetValue<string>();
            if (c is not null && status is "ACTIVE" or "UPCOMING")
                availableCampaigns.Add(KeyValuePair.Create(c["id"]!.GetValue<string>(), c));
        }
        // fetch detailed data for each campaign, in chunks
        Gui.SetStatus("Fetching campaigns...");
        var fetchTasks = Utils.Chunk(availableCampaigns, 20)
            .Select(chunk => Task.Run(() => FetchCampaignsChunkAsync(chunk)))
            .ToList();
        foreach (Task<JsonObject> task in fetchTasks)
        {
            JsonObject chunkData = await task.ConfigureAwait(false);
            // merge the inventory and campaigns data together
            inventoryData = MergeData(inventoryData, chunkData);
        }
        // filter out invalid campaigns
        foreach (string campaignId in inventoryData
            .Where(kv => kv.Value?["game"] is null)
            .Select(kv => kv.Key).ToList())
        {
            inventoryData.Remove(campaignId);
        }

        // use the merged data to create campaign objects
        List<DropsCampaign> campaigns = [.. inventoryData
            .Select(kv => new DropsCampaign(this, kv.Value!, claimedBenefits))
            .OrderByDescending(c => c.Eligible)
            .ThenBy(c => c.Upcoming ? c.StartsAt : c.EndsAt)
            .ThenByDescending(c => c.Active)];

        _drops.Clear();
        Gui.ClearInventory();
        Inventory.Clear();
        _campaigns.Clear();
        _completedNotified.Clear();
        _mntTriggers.Clear();
        var switchTriggers = new HashSet<DateTime>();
        DateTime nextHour = DateTime.UtcNow + TimeSpan.FromHours(1);
        foreach (DropsCampaign campaign in campaigns)
        {
            foreach (TimedDrop drop in campaign.Drops)
                _drops[drop.Id] = drop;
            if (campaign.CanEarnWithin(nextHour))
                switchTriggers.UnionWith(campaign.TimeTriggers);
            Inventory.Add(campaign);
            _campaigns[campaign.Id] = campaign;
        }
        if (Gui.CloseRequested)
            throw new ExitRequestException();
        // add all campaigns to the GUI in one batch; box art loads asynchronously afterwards
        Gui.SetStatus($"Adding campaigns to the GUI... ({campaigns.Count})");
        Gui.AddCampaigns(campaigns);
        LogInfo(
            $"Inventory loaded: {campaigns.Count} campaigns, {_drops.Count} drops, "
            + $"{campaigns.Count(c => c.Eligible)} eligible, {campaigns.Count(c => c.Active)} active");
        foreach (DateTime trigger in switchTriggers.Where(t => t > DateTime.UtcNow).Order())
            _mntTriggers.Enqueue(trigger);
        // NOTE: the maintenance task is restarted at the end of each inventory fetch
        _mntCts?.Cancel();
        _mntCts = new CancellationTokenSource();
        _mntTask = Task.Run(() => MaintenanceTaskAsync(_mntCts.Token));
    }

    public DropsCampaign? GetActiveCampaign(Channel? channel = null)
    {
        if (WantedGames.Count == 0)
            return null;
        Channel? watchingChannel = WatchingChannel.GetWithDefault(channel);
        if (watchingChannel is null)
        {
            // if we aren't watching anything, we can't earn any drops
            return null;
        }
        return Inventory
            .Where(c => c.CanEarn(watchingChannel))
            .OrderBy(c => c.RemainingMinutes)
            .FirstOrDefault();
    }

    /// <summary>
    /// Returns live channels for a game. With the language preference enabled, channels are
    /// gathered in tiers (user's language, then English, then any) and tagged with
    /// <see cref="Channel.LanguageTier"/>; the caller sorts by tier, then by viewers.
    /// NOTE: Twitch's directory response carries no language field, so the tiers come from
    /// separate filtered queries. Later tiers are skipped once we have enough channels.
    /// </summary>
    public async Task<List<Channel>> GetLiveStreamsAsync(Game game, int limit = 20, bool dropsEnabled = true)
    {
        if (!Settings.PreferOwnLanguage)
            return await FetchLiveStreamsAsync(game, limit, dropsEnabled, null, 0).ConfigureAwait(false);

        string preferred = Settings.EffectiveLanguage;
        var tiers = new List<(string? Language, int Tier)> { (preferred, 0) };
        if (!string.Equals(preferred, "en", StringComparison.OrdinalIgnoreCase))
            tiers.Add(("en", 1));
        tiers.Add((null, 2)); // unfiltered: whatever is left

        var channels = new List<Channel>();
        var seen = new HashSet<long>();
        foreach ((string? language, int tier) in tiers)
        {
            if (channels.Count >= limit)
                break; // already enough higher-preference channels
            List<Channel> fetched = await FetchLiveStreamsAsync(
                game, limit, dropsEnabled, language, tier).ConfigureAwait(false);
            foreach (Channel channel in fetched)
            {
                // keep the first (best) tier a channel appeared in
                if (seen.Add(channel.Id))
                    channels.Add(channel);
            }
        }
        return channels;
    }

    private async Task<List<Channel>> FetchLiveStreamsAsync(
        Game game, int limit, bool dropsEnabled, string? language, int tier)
    {
        var filters = new JsonArray();
        if (dropsEnabled)
            filters.Add("DROPS_ENABLED");
        var options = new JsonObject
        {
            ["includeRestricted"] = new JsonArray("SUB_ONLY_LIVE"),
            ["systemFilters"] = filters,
        };
        if (language is not null)
        {
            // "broadcasterLanguages" is a GraphQL enum (Language), so it takes the
            // enum literal: "de" -> "DE", "zh-cn" -> "ZHCN".
            options["broadcasterLanguages"] =
                new JsonArray(language.ToUpperInvariant().Replace("-", ""));
        }
        JsonNode response;
        try
        {
            response = await GqlRequestAsync(
                GqlQueries.GameDirectory.WithVariables(new JsonObject
                {
                    ["limit"] = limit,
                    ["slug"] = game.Slug,
                    ["options"] = options,
                })).ConfigureAwait(false);
        }
        catch (GqlException exc)
        {
            if (language is not null)
            {
                // an unsupported language code must not break mining:
                // skip this tier and let the remaining tiers cover the game
                LogWarning($"Directory [{game.Slug}] language '{language}' rejected: {exc.Message}");
                return [];
            }
            throw new MinerException($"Game: {game.Slug}", exc);
        }
        var channels = new List<Channel>();
        if (response["data"]?["game"]?["streams"]?["edges"] is JsonArray edges)
        {
            foreach (JsonNode? edge in edges)
            {
                JsonNode? node = edge?["node"];
                if (node?["broadcaster"] is not null)
                {
                    Channel channel = Channel.FromDirectory(this, node, dropsEnabled);
                    channel.LanguageTier = tier;
                    channels.Add(channel);
                }
            }
        }
        if (language is not null)
            LogDebug($"Directory [{game.Slug}] language '{language}': {channels.Count} channels");
        return channels;
    }

    /// <summary>
    /// Uses batch GQL requests to check the ONLINE status of many channels at once.
    /// Also handles the drops_enabled check (if enabled).
    /// </summary>
    public async Task BulkCheckOnlineAsync(IReadOnlyCollection<Channel> channels)
    {
        if (channels.Count == 0)
            return;
        var aclStreamsMap = new Dictionary<long, JsonNode>();
        var streamTasks = Utils.Chunk(channels.Select(ch => ch.StreamGql), 20)
            .Select(chunk => Task.Run(() => GqlRequestAsync(chunk)))
            .ToList();
        foreach (Task<List<JsonNode>> task in streamTasks)
        {
            foreach (JsonNode responseJson in await task.ConfigureAwait(false))
            {
                if (responseJson["data"]?["user"] is JsonNode channelData)
                    aclStreamsMap[long.Parse(channelData["id"]!.GetValue<string>())] = channelData;
            }
        }
        // for channels with an active stream, check the available drops as well
        var aclAvailableDropsMap = new Dictionary<long, JsonArray>();
        if (Settings.AvailableDropsCheck)
        {
            var availableOps = aclStreamsMap
                .Where(kv => kv.Value["stream"] is not null) // only ONLINE channels
                .Select(kv => GqlQueries.AvailableDrops.WithVariables(
                    new JsonObject { ["channelID"] = kv.Key.ToString() }))
                .ToList();
            var availableTasks = Utils.Chunk(availableOps, 20)
                .Select(chunk => Task.Run(() => GqlRequestAsync(chunk)))
                .ToList();
            foreach (Task<List<JsonNode>> task in availableTasks)
            {
                foreach (JsonNode responseJson in await task.ConfigureAwait(false))
                {
                    if (responseJson["data"]?["channel"] is JsonNode availableInfo)
                    {
                        aclAvailableDropsMap[long.Parse(availableInfo["id"]!.GetValue<string>())] =
                            availableInfo["viewerDropCampaigns"] as JsonArray ?? [];
                    }
                }
            }
        }
        foreach (Channel channel in channels)
        {
            if (!aclStreamsMap.TryGetValue(channel.Id, out JsonNode? channelData))
                continue;
            if (channelData["stream"] is null)
                continue;
            JsonArray availableDrops = aclAvailableDropsMap.GetValueOrDefault(channel.Id) ?? [];
            channel.ExternalUpdate(channelData, availableDrops);
        }
    }

    #endregion
}
