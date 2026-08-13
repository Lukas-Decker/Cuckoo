using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Cuckoo.Core;

/// <summary>A single sharded PubSub websocket connection.</summary>
public sealed class TwitchWebsocket
{
    private readonly WebsocketPool _pool;
    private readonly TwitchClient _twitch;
    private readonly int _index;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    private ClientWebSocket? _ws;
    private readonly AsyncManualResetEvent _connected = new();
    private readonly AsyncManualResetEvent _closed = new();
    private readonly AsyncManualResetEvent _reconnectRequested = new();
    private readonly AsyncManualResetEvent _topicsChanged = new();

    private DateTime _nextPing = DateTime.UtcNow;
    private DateTime _maxPong = DateTime.UtcNow + Constants.PingTimeout;

    private Task? _handleTask;

    public Dictionary<string, WebsocketTopic> Topics { get; } = [];
    private readonly HashSet<WebsocketTopic> _submitted = [];

    public TwitchWebsocket(WebsocketPool pool, int index)
    {
        _pool = pool;
        _twitch = pool.Client;
        _index = index;
        SetStatus("Disconnected");
    }

    public bool Connected => _connected.IsSet;

    private void SetStatus(string? status = null, bool refreshTopics = false)
        => _twitch.Gui.UpdateWebsocketStatus(_index, status, refreshTopics ? Topics.Count : null);

    public void RequestReconnect()
    {
        // reset the ping interval, so we send a PING right away after reconnecting
        _nextPing = DateTime.UtcNow;
        _reconnectRequested.Set();
    }

    public async Task StartAsync()
    {
        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            StartNoWaitLocked();
        }
        finally
        {
            _stateLock.Release();
        }
        await _connected.WaitAsync().ConfigureAwait(false);
    }

    public void StartNoWait()
    {
        _stateLock.Wait();
        try
        {
            StartNoWaitLocked();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private void StartNoWaitLocked()
    {
        if (_handleTask is null || _handleTask.IsCompleted)
        {
            _closed.Reset();
            _handleTask = Task.Run(HandleAsync);
        }
    }

    public async Task StopAsync(bool remove = false)
    {
        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_closed.IsSet)
            {
                _closed.Set();
                if (_connected.IsSet)
                    SetStatus("Disconnecting...");
                try
                {
                    _ws?.Abort();
                }
                catch (ObjectDisposedException) { }
                if (_handleTask is not null)
                {
                    try
                    {
                        await _handleTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    }
                    catch (TimeoutException) { }
                    _handleTask = null;
                }
            }
            if (remove)
            {
                Topics.Clear();
                _topicsChanged.Set();
                _twitch.Gui.RemoveWebsocketStatus(_index);
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public void StopNoWait(bool remove = false)
        => _ = Task.Run(async () =>
        {
            try
            {
                await StopAsync(remove).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _twitch.LogException($"Websocket[{_index}] stop", ex);
            }
        });

    private async Task HandleAsync()
    {
        try
        {
            // ensure we're logged in before connecting
            SetStatus("Initializing...");
            await _twitch.Auth.WaitUntilLoginAsync().ConfigureAwait(false);
            SetStatus("Connecting...");
            _twitch.LogInfo($"Websocket[{_index}] connecting...");

            var backoff = new ExponentialBackoff(maximum: 3 * 60);
            while (!_closed.IsSet)
            {
                ClientWebSocket ws;
                try
                {
                    ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = TimeSpan.Zero; // PubSub uses its own PING/PONG
                    if (!string.IsNullOrEmpty(_twitch.Settings.Proxy))
                        ws.Options.Proxy = new WebProxy(_twitch.Settings.Proxy);
                    await ws.ConnectAsync(new Uri(Constants.WebsocketUrl), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpRequestException)
                {
                    double delay = backoff.Next();
                    _twitch.LogInfo($"Websocket[{_index}] connection problem (sleep: {Math.Round(delay)}s)");
                    await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
                    continue;
                }

                _ws = ws;
                _connected.Set();
                _reconnectRequested.Reset();
                SetStatus("Connected");
                _twitch.LogInfo($"Websocket[{_index}] connected.");
                try
                {
                    await RunConnectionAsync(ws).ConfigureAwait(false);
                    backoff.Reset();
                }
                catch (WebsocketClosedException exc)
                {
                    if (exc.Received)
                    {
                        // server closed the connection, not us: reconnect
                        _twitch.LogWarning($"Websocket[{_index}] closed unexpectedly: {ws.CloseStatus}");
                    }
                    else if (_closed.IsSet)
                    {
                        // we closed it: exit
                        _twitch.LogInfo($"Websocket[{_index}] stopped.");
                        SetStatus("Disconnected");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _twitch.LogException($"Websocket[{_index}]", ex);
                }
                finally
                {
                    _connected.Reset();
                    _ws = null;
                    _submitted.Clear();
                    // let the next connection re-subscribe to the topics
                    _topicsChanged.Set();
                    ws.Dispose();
                }
                if (_closed.IsSet)
                {
                    SetStatus("Disconnected");
                    return;
                }
                SetStatus("Reconnecting...");
                _twitch.LogWarning($"Websocket[{_index}] reconnecting...");
            }
            SetStatus("Disconnected");
        }
        catch (Exception ex)
        {
            _twitch.LogException($"Websocket[{_index}] handler (critical)", ex);
            _twitch.Close();
        }
    }

    private async Task RunConnectionAsync(ClientWebSocket ws)
    {
        using var connectionCts = new CancellationTokenSource();
        Task receiveTask = ReceiveLoopAsync(ws, connectionCts.Token);
        try
        {
            while (!_reconnectRequested.IsSet && !_closed.IsSet)
            {
                await HandlePingAsync(ws).ConfigureAwait(false);
                await HandleTopicsAsync(ws).ConfigureAwait(false);
                Task finished = await Task.WhenAny(
                    receiveTask,
                    Task.Delay(500),
                    _reconnectRequested.WaitAsync(),
                    _closed.WaitAsync()).ConfigureAwait(false);
                if (finished == receiveTask)
                {
                    await receiveTask.ConfigureAwait(false); // propagate its exception
                    return;
                }
            }
        }
        finally
        {
            connectionCts.Cancel();
            try
            {
                ws.Abort();
            }
            catch (ObjectDisposedException) { }
            try
            {
                await receiveTask.ConfigureAwait(false);
            }
            catch (WebsocketClosedException) { }
            catch (OperationCanceledException) { }
        }
        // a reconnect was requested, or we're closing
        if (_closed.IsSet)
            throw new WebsocketClosedException(received: false);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var messageBytes = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                throw new WebsocketClosedException(received: false);
            }
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebsocketClosedException(received: true);
            messageBytes.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;
            string text = Encoding.UTF8.GetString(messageBytes.ToArray());
            messageBytes.SetLength(0);
            if (text.Length == 0)
                continue;
            try
            {
                HandleRawMessage(JsonNode.Parse(text)!);
            }
            catch (Exception ex)
            {
                _twitch.LogException($"Websocket[{_index}] message handling", ex);
            }
        }
    }

    private void HandleRawMessage(JsonNode message)
    {
        string? msgType = message["type"]?.GetValue<string>();
        switch (msgType)
        {
            case "MESSAGE":
                // request the assigned topic to process the message
                string topicId = message["data"]!["topic"]!.GetValue<string>();
                _twitch.LogTrace($"Websocket[{_index}] message for topic: {topicId}");
                if (Topics.TryGetValue(topicId, out WebsocketTopic? topic))
                {
                    JsonNode inner = JsonNode.Parse(message["data"]!["message"]!.GetValue<string>())!;
                    // use a task to not block the websocket
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await topic.ProcessAsync(inner).ConfigureAwait(false);
                        }
                        catch (ExitRequestException) { }
                        catch (ReloadRequestException) { }
                        catch (Exception ex)
                        {
                            _twitch.LogException($"Topic {topicId} processing", ex);
                        }
                    });
                }
                break;
            case "PONG":
                // move the pong deadline to something much later
                _maxPong = _nextPing;
                break;
            case "RESPONSE":
                // no special handling for these (for now)
                break;
            case "RECONNECT":
                _twitch.LogWarning($"Websocket[{_index}] requested reconnect.");
                RequestReconnect();
                break;
            default:
                _twitch.LogWarning($"Websocket[{_index}] received unknown payload: {message.ToJsonString()}");
                break;
        }
    }

    private async Task HandlePingAsync(ClientWebSocket ws)
    {
        DateTime now = DateTime.UtcNow;
        if (now >= _nextPing)
        {
            _nextPing = now + Constants.PingInterval;
            _maxPong = now + Constants.PingTimeout; // wait for a PONG for up to 10s
            await SendAsync(ws, new JsonObject { ["type"] = "PING" }).ConfigureAwait(false);
        }
        else if (now >= _maxPong)
        {
            // it's been more than 10s and there was no PONG
            _twitch.LogWarning($"Websocket[{_index}] didn't receive a PONG, reconnecting...");
            RequestReconnect();
        }
    }

    private async Task HandleTopicsAsync(ClientWebSocket ws)
    {
        if (!_topicsChanged.IsSet)
            return;
        _topicsChanged.Reset();
        SetStatus(refreshTopics: true);
        string authToken = _twitch.Auth.AccessToken
            ?? throw new MinerException("Websocket topics change without an access token");

        HashSet<WebsocketTopic> current;
        lock (Topics)
            current = [.. Topics.Values];
        // handle removed topics
        var removed = _submitted.Where(t => !current.Contains(t)).ToList();
        if (removed.Count > 0)
        {
            _twitch.LogDebug($"Websocket[{_index}] unlistening {removed.Count} topics");
            foreach (var topicsChunk in Utils.Chunk(removed.Select(t => t.Id), 20))
            {
                await SendAsync(ws, new JsonObject
                {
                    ["type"] = "UNLISTEN",
                    ["data"] = new JsonObject
                    {
                        ["topics"] = new JsonArray([.. topicsChunk.Select(t => JsonValue.Create(t))]),
                        ["auth_token"] = authToken,
                    }
                }).ConfigureAwait(false);
            }
            _submitted.ExceptWith(removed);
        }
        // handle added topics
        var added = current.Where(t => !_submitted.Contains(t)).ToList();
        if (added.Count > 0)
        {
            _twitch.LogDebug($"Websocket[{_index}] listening to {added.Count} new topics");
            foreach (var topicsChunk in Utils.Chunk(added.Select(t => t.Id), 20))
            {
                await SendAsync(ws, new JsonObject
                {
                    ["type"] = "LISTEN",
                    ["data"] = new JsonObject
                    {
                        ["topics"] = new JsonArray([.. topicsChunk.Select(t => JsonValue.Create(t))]),
                        ["auth_token"] = authToken,
                    }
                }).ConfigureAwait(false);
            }
            _submitted.UnionWith(added);
        }
    }

    /// <summary>Takes as many topics from the set as this connection can hold.</summary>
    public void AddTopics(ISet<WebsocketTopic> topicsSet)
    {
        bool changed = false;
        lock (Topics)
        {
            while (topicsSet.Count > 0 && Topics.Count < Constants.WsTopicsLimit)
            {
                WebsocketTopic topic = topicsSet.First();
                topicsSet.Remove(topic);
                Topics[topic.Id] = topic;
                changed = true;
            }
        }
        if (changed)
            _topicsChanged.Set();
    }

    public void RemoveTopics(ISet<string> topicsSet)
    {
        lock (Topics)
        {
            var existing = topicsSet.Where(Topics.ContainsKey).ToList();
            if (existing.Count == 0)
                return;
            topicsSet.ExceptWith(existing);
            foreach (string topic in existing)
                Topics.Remove(topic);
        }
        _topicsChanged.Set();
    }

    private async Task SendAsync(ClientWebSocket ws, JsonObject message)
    {
        if (message["type"]?.GetValue<string>() != "PING")
            message["nonce"] = Utils.CreateNonce(Utils.CharsAscii, 30);
        byte[] payload = Encoding.UTF8.GetBytes(message.ToJsonString(Utils.MinifiedJson));
        try
        {
            await ws.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            throw new WebsocketClosedException(received: false);
        }
    }
}

/// <summary>Manages up to 8 sharded websocket connections.</summary>
public sealed class WebsocketPool(TwitchClient client)
{
    public TwitchClient Client { get; } = client;

    private bool _running;
    public List<TwitchWebsocket> Websockets { get; } = [];

    public async Task StartAsync()
    {
        _running = true;
        await Task.WhenAll(Websockets.Select(ws => ws.StartAsync())).ConfigureAwait(false);
    }

    public async Task StopAsync(bool clearTopics = false)
    {
        _running = false;
        await Task.WhenAll(Websockets.Select(ws => ws.StopAsync(remove: clearTopics))).ConfigureAwait(false);
        if (clearTopics)
            Websockets.Clear();
    }

    public void AddTopics(IEnumerable<WebsocketTopic> topics)
    {
        // ensure no topics end up duplicated
        var topicsSet = new HashSet<WebsocketTopic>(topics);
        if (topicsSet.Count == 0)
            return;
        foreach (TwitchWebsocket ws in Websockets)
            topicsSet.ExceptWith(ws.Topics.Values);
        if (topicsSet.Count == 0)
            return;
        for (int wsIdx = 0; wsIdx < Constants.MaxWebsockets; wsIdx++)
        {
            TwitchWebsocket ws;
            if (wsIdx < Websockets.Count)
            {
                ws = Websockets[wsIdx];
            }
            else
            {
                ws = new TwitchWebsocket(this, wsIdx);
                if (_running)
                    ws.StartNoWait();
                Websockets.Add(ws);
            }
            // ask the websocket to take any topics it can; this modifies the set in place
            ws.AddTopics(topicsSet);
            if (topicsSet.Count == 0)
                return;
        }
        // there were leftover topics after filling up all websockets
        throw new MinerException("Maximum topics limit has been reached");
    }

    public void RemoveTopics(IEnumerable<string> topics)
    {
        var topicsSet = new HashSet<string>(topics);
        if (topicsSet.Count == 0)
            return;
        foreach (TwitchWebsocket ws in Websockets)
            ws.RemoveTopics(topicsSet);
        // if we have more websockets connected than needed,
        // stop the last one and recycle topics from it
        var recycledTopics = new List<WebsocketTopic>();
        while (Websockets.Count > 0)
        {
            int count = Websockets.Sum(ws => ws.Topics.Count);
            if (count <= (Websockets.Count - 1) * Constants.WsTopicsLimit)
            {
                TwitchWebsocket ws = Websockets[^1];
                Websockets.RemoveAt(Websockets.Count - 1);
                recycledTopics.AddRange(ws.Topics.Values);
                ws.StopNoWait(remove: true);
            }
            else
            {
                break;
            }
        }
        if (recycledTopics.Count > 0)
            AddTopics(recycledTopics);
    }
}
