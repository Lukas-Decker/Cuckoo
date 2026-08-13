using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Cuckoo.Services;

public enum NotificationCategory
{
    DropClaimed,
    CampaignCompleted,
    MiningStatus,
    Errors,
}

/// <summary>
/// One-way push notifications to Discord (via webhook) and Telegram (via bot API).
/// Sends are fire-and-forget and never throw into the caller; failures are logged
/// through the supplied callback. Every message carries the instance identity
/// (logged-in account name), so multiple instances are distinguishable.
/// </summary>
public sealed class NotificationService(
    Settings settings, Action<string> logError, Func<string> instanceIdentity)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Lock _dedupeLock = new();
    private string _lastStatusMessage = "";

    public bool DiscordConfigured => !string.IsNullOrWhiteSpace(settings.DiscordWebhookUrl);

    public bool TelegramConfigured
        => !string.IsNullOrWhiteSpace(settings.TelegramBotToken)
            && !string.IsNullOrWhiteSpace(settings.TelegramChatId);

    public bool AnyConfigured => DiscordConfigured || TelegramConfigured;

    private bool IsEnabled(NotificationCategory category) => category switch
    {
        NotificationCategory.DropClaimed => settings.NotifyDropClaimed,
        NotificationCategory.CampaignCompleted => settings.NotifyCampaignCompleted,
        NotificationCategory.MiningStatus => settings.NotifyMiningStatus,
        NotificationCategory.Errors => settings.NotifyErrors,
        _ => false,
    };

    /// <summary>Queues a notification for the given category (no-op if disabled/unconfigured).</summary>
    public void Send(NotificationCategory category, string title, string message)
    {
        if (!IsEnabled(category) || !AnyConfigured)
            return;
        // collapse repeated identical status updates (channel re-links, etc.)
        if (category == NotificationCategory.MiningStatus)
        {
            lock (_dedupeLock)
            {
                if (_lastStatusMessage == message)
                    return;
                _lastStatusMessage = message;
            }
        }
        _ = Task.Run(() => DispatchAsync(title, message));
    }

    /// <summary>Sends a test message to every configured destination, ignoring category gates.</summary>
    public Task SendTestAsync()
        => DispatchAsync("Cuckoo", "Test notification - integration is working.");

    private async Task DispatchAsync(string title, string message)
    {
        string identity = SafeIdentity();
        if (DiscordConfigured)
            await SendDiscordAsync(title, message, identity).ConfigureAwait(false);
        if (TelegramConfigured)
            await SendTelegramAsync(title, message, identity).ConfigureAwait(false);
    }

    private string SafeIdentity()
    {
        try
        {
            return instanceIdentity();
        }
        catch (Exception)
        {
            return "";
        }
    }

    private async Task SendDiscordAsync(string title, string message, string identity)
    {
        try
        {
            var embed = new JsonObject
            {
                ["title"] = title,
                ["description"] = message,
                ["color"] = 0x9146FF, // Twitch purple
            };
            if (identity.Length > 0)
                embed["footer"] = new JsonObject { ["text"] = $"Account: {identity}" };
            var payload = new JsonObject
            {
                // sender name carries the account, so instances are told apart at a glance
                ["username"] = identity.Length > 0 ? $"Cuckoo ({identity})" : "Cuckoo",
                ["embeds"] = new JsonArray(embed),
            };
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Http
                .PostAsync(settings.DiscordWebhookUrl, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                logError($"Discord notification failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            logError($"Discord notification error: {ex.Message}");
        }
    }

    private async Task SendTelegramAsync(string title, string message, string identity)
    {
        try
        {
            string url = $"https://api.telegram.org/bot{settings.TelegramBotToken}/sendMessage";
            string text = $"*{Escape(title)}*\n{Escape(message)}";
            if (identity.Length > 0)
                text += $"\n_{Escape($"Account: {identity}")}_";
            var payload = new JsonObject
            {
                ["chat_id"] = settings.TelegramChatId,
                ["text"] = text,
                ["parse_mode"] = "MarkdownV2",
                ["disable_web_page_preview"] = true,
            };
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Http
                .PostAsync(url, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                logError($"Telegram notification failed: {(int)response.StatusCode} {body}");
            }
        }
        catch (Exception ex)
        {
            logError($"Telegram notification error: {ex.Message}");
        }
    }

    // MarkdownV2 requires these characters to be backslash-escaped.
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is '_' or '*' or '[' or ']' or '(' or ')' or '~' or '`' or '>' or '#'
                or '+' or '-' or '=' or '|' or '{' or '}' or '.' or '!' or '\\')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
