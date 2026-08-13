using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuckoo.Core;

/// <summary>
/// Holds the login session state and performs the OAuth device-code login flow.
/// Ported from the original miner's _AuthState, with the cookie jar replaced by
/// an auth.json token store.
/// </summary>
public sealed class AuthState(TwitchClient twitch)
{
    private sealed class StoredAuth
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public string? DeviceId { get; set; }
        public string? ClientId { get; set; }
        public long UserId { get; set; }
        public string? UserName { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly AsyncManualResetEvent _loggedIn = new();

    public long UserId { get; private set; }
    public string UserName { get; private set; } = "";
    public string? AccessToken { get; private set; }
    private string? _refreshToken;
    public string DeviceId { get; private set; } = "";
    public string SessionId { get; private set; } = "";

    public bool IsLoggedIn => _loggedIn.IsSet;
    public Task WaitUntilLoginAsync() => _loggedIn.WaitAsync();

    public void Invalidate(bool deleteToken = false)
    {
        AccessToken = null;
        UserId = 0;
        _loggedIn.Reset();
        if (deleteToken)
        {
            // full logout: also forget the refresh token,
            // so we can't silently re-login into the same account
            _refreshToken = null;
            File.Delete(Constants.AuthPath);
        }
    }

    public void Clear()
    {
        AccessToken = null;
        UserId = 0;
        UserName = "";
        SessionId = "";
        _loggedIn.Reset();
    }

    public Dictionary<string, string> Headers(string userAgent = "", bool gql = false)
    {
        ClientInfo clientInfo = twitch.ClientInfo;
        var headers = new Dictionary<string, string>
        {
            ["Accept"] = "*/*",
            ["Accept-Language"] = "en-US",
            ["Pragma"] = "no-cache",
            ["Cache-Control"] = "no-cache",
            ["Client-Id"] = clientInfo.ClientId,
        };
        if (!string.IsNullOrEmpty(userAgent))
            headers["User-Agent"] = userAgent;
        if (!string.IsNullOrEmpty(SessionId))
            headers["Client-Session-Id"] = SessionId;
        if (!string.IsNullOrEmpty(DeviceId))
            headers["X-Device-Id"] = DeviceId;
        if (gql)
        {
            headers["Origin"] = clientInfo.ClientUrl;
            headers["Referer"] = clientInfo.ClientUrl;
            headers["Authorization"] = $"OAuth {AccessToken}";
        }
        return headers;
    }

    public async Task ValidateAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await ValidateInnerAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task ValidateInnerAsync()
    {
        if (string.IsNullOrEmpty(SessionId))
            SessionId = Utils.CreateNonce(Utils.CharsHexLower, 16);

        if (AccessToken is not null && UserId != 0)
        {
            _loggedIn.Set();
            return;
        }

        ClientInfo clientInfo = twitch.ClientInfo;
        StoredAuth? stored = LoadStored();
        if (string.IsNullOrEmpty(DeviceId))
        {
            DeviceId = !string.IsNullOrEmpty(stored?.DeviceId)
                ? stored!.DeviceId!
                : Utils.CreateNonce(Utils.CharsHexLower, 32);
        }

        twitch.Gui.LoginUpdate("Logging in...");
        // restore the previous session's tokens, unless they belong to a different client id
        if (stored?.ClientId == clientInfo.ClientId)
        {
            twitch.LogDebug(
                $"Auth: restoring stored session (token: {stored?.AccessToken is not null}, "
                + $"refresh: {stored?.RefreshToken is not null})");
            AccessToken ??= stored?.AccessToken;
            _refreshToken ??= stored?.RefreshToken;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (AccessToken is null)
            {
                // try refreshing the expired session first, before the full device flow
                if (_refreshToken is not null)
                {
                    twitch.LogInfo("Refreshing the expired session");
                    if (!await TryRefreshTokensAsync().ConfigureAwait(false))
                        _refreshToken = null;
                }
                AccessToken ??= await OAuthDeviceLoginAsync().ConfigureAwait(false);
            }

            // validate the auth token by obtaining the user id
            using HttpResponseMessage response = await twitch.RequestAsync(
                HttpMethod.Get,
                "https://id.twitch.tv/oauth2/validate",
                headers: new Dictionary<string, string> { ["Authorization"] = $"OAuth {AccessToken}" }
            ).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // the token we have is invalid: discard it and reauthenticate
                twitch.LogInfo("Restored session is invalid");
                AccessToken = null;
                File.Delete(Constants.AuthPath);
                continue;
            }
            response.EnsureSuccessStatusCode();
            JsonNode validation = JsonNode.Parse(
                await response.Content.ReadAsStringAsync().ConfigureAwait(false))!;
            if (validation["client_id"]?.GetValue<string>() != clientInfo.ClientId)
            {
                twitch.LogInfo("Stored token client ID mismatch");
                AccessToken = null;
                File.Delete(Constants.AuthPath);
                continue;
            }
            UserId = long.Parse(validation["user_id"]!.GetValue<string>());
            UserName = validation["login"]!.GetValue<string>();
            break;
        }
        if (AccessToken is null || UserId == 0)
            throw new LoginException("Login verification failure");

        twitch.LogInfo($"Login successful, user: {UserName} ({UserId})");
        twitch.Gui.LoginUpdate("Logged in", UserId, UserName);
        SaveStored();
        _loggedIn.Set();
    }

    /// <summary>
    /// OAuth device-code flow: obtains a code the user enters on twitch.tv/activate,
    /// then polls for the resulting access token.
    /// </summary>
    private async Task<string> OAuthDeviceLoginAsync()
    {
        ClientInfo clientInfo = twitch.ClientInfo;
        var headers = new Dictionary<string, string>
        {
            ["Accept"] = "application/json",
            ["Accept-Language"] = "en-US",
            ["Cache-Control"] = "no-cache",
            ["Client-Id"] = clientInfo.ClientId,
            ["Origin"] = clientInfo.ClientUrl,
            ["Pragma"] = "no-cache",
            ["Referer"] = clientInfo.ClientUrl,
            ["User-Agent"] = clientInfo.UserAgent,
            ["X-Device-Id"] = DeviceId,
        };

        while (true)
        {
            try
            {
                twitch.LogInfo("Auth: starting the device-code login flow");
                DateTime now = DateTime.UtcNow;
                JsonNode deviceJson;
                using (HttpResponseMessage response = await twitch.RequestAsync(
                    HttpMethod.Post,
                    "https://id.twitch.tv/oauth2/device",
                    headers: headers,
                    form: new Dictionary<string, string>
                    {
                        ["client_id"] = clientInfo.ClientId,
                        ["scopes"] = "",
                    }).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    deviceJson = JsonNode.Parse(
                        await response.Content.ReadAsStringAsync().ConfigureAwait(false))!;
                }

                string deviceCode = deviceJson["device_code"]!.GetValue<string>();
                string userCode = deviceJson["user_code"]!.GetValue<string>();
                int interval = deviceJson["interval"]!.GetValue<int>();
                string verificationUri = deviceJson["verification_uri"]!.GetValue<string>();
                DateTime expiresAt = now + TimeSpan.FromSeconds(deviceJson["expires_in"]!.GetValue<int>());

                // show the code to the user so they can enter it on the activate page
                twitch.Gui.ShowDeviceCode(verificationUri, userCode);
                // notify remote channels so login can be completed away from the machine
                twitch.NotifyError(
                    $"Login required. Open {verificationUri} and enter code: {userCode}");

                var tokenForm = new Dictionary<string, string>
                {
                    ["client_id"] = clientInfo.ClientId,
                    ["device_code"] = deviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                };
                while (true)
                {
                    // sleep first; the user won't enter the code *that* fast
                    await Task.Delay(TimeSpan.FromSeconds(interval)).ConfigureAwait(false);
                    using HttpResponseMessage response = await twitch.RequestAsync(
                        HttpMethod.Post,
                        "https://id.twitch.tv/oauth2/token",
                        headers: headers,
                        form: tokenForm,
                        invalidateAfter: expiresAt).ConfigureAwait(false);
                    // 200 means success, 400 means the user hasn't entered the code yet
                    if (!response.IsSuccessStatusCode)
                        continue;
                    JsonNode tokenJson = JsonNode.Parse(
                        await response.Content.ReadAsStringAsync().ConfigureAwait(false))!;
                    _refreshToken = tokenJson["refresh_token"]?.GetValue<string>();
                    return tokenJson["access_token"]!.GetValue<string>();
                }
            }
            catch (RequestInvalidException)
            {
                // the device code has expired: request a new one
            }
        }
    }

    /// <summary>
    /// Attempts to exchange the stored refresh token for a fresh token pair.
    /// Returns false if the refresh token got rejected.
    /// </summary>
    private async Task<bool> TryRefreshTokensAsync()
    {
        using HttpResponseMessage response = await twitch.RequestAsync(
            HttpMethod.Post,
            "https://id.twitch.tv/oauth2/token",
            headers: new Dictionary<string, string>
            {
                ["Client-Id"] = twitch.ClientInfo.ClientId,
                ["User-Agent"] = twitch.ClientInfo.UserAgent,
                ["X-Device-Id"] = DeviceId,
            },
            form: new Dictionary<string, string>
            {
                ["client_id"] = twitch.ClientInfo.ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _refreshToken!,
            }).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return false;
        JsonNode tokenJson = JsonNode.Parse(
            await response.Content.ReadAsStringAsync().ConfigureAwait(false))!;
        AccessToken = tokenJson["access_token"]?.GetValue<string>();
        _refreshToken = tokenJson["refresh_token"]?.GetValue<string>() ?? _refreshToken;
        return AccessToken is not null;
    }

    private static StoredAuth? LoadStored()
    {
        try
        {
            if (File.Exists(Constants.AuthPath))
                return JsonSerializer.Deserialize<StoredAuth>(
                    File.ReadAllText(Constants.AuthPath), JsonOptions);
        }
        catch (JsonException) { }
        return null;
    }

    private void SaveStored()
    {
        var stored = new StoredAuth
        {
            AccessToken = AccessToken,
            RefreshToken = _refreshToken,
            DeviceId = DeviceId,
            ClientId = twitch.ClientInfo.ClientId,
            UserId = UserId,
            UserName = UserName,
        };
        File.WriteAllText(Constants.AuthPath, JsonSerializer.Serialize(stored, JsonOptions));
    }
}
