using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cuckoo.Core;
using Cuckoo.Models;

namespace Cuckoo.Services;

/// <summary>
/// User preferences persisted to settings.json (next to the executable).
/// Ported from the original miner, minus its CLI-argument override layer.
///
/// The config is versioned and self-healing:
///  - every successful save also writes settings.json.bak (last known good)
///  - a corrupted file is backed up as .corrupt and salvaged field-by-field,
///    falling back to the .bak, then to defaults
///  - all values are validated/clamped after loading
///  - older config versions are migrated forward on load
/// </summary>
public sealed class Settings
{
    /// <summary>Bump when the config format changes; add a migration step below.</summary>
    public const int CurrentConfigVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Format version of this config file. Defaults to 1, so files from before
    /// versioning (no config_version key) are correctly treated as v1 and migrated.
    /// Fresh instances get stamped with the current version in Load().
    /// </summary>
    public int ConfigVersion { get; set; } = 1;

    /// <summary>App version that last wrote this file (diagnostics only).</summary>
    public string AppVersion { get; set; } = "";

    public List<string> Priority { get; set; } = [];
    public List<string> Exclude { get; set; } = [];

    /// <summary>Mining order mode. Kept under the legacy "priority_mode" JSON key.</summary>
    [JsonPropertyName("priority_mode")]
    public MiningMode MiningMode { get; set; } = MiningMode.PriorityOnly;

    // weights for MiningMode.Custom (0-200 each)
    public int CustomWeightPriority { get; set; } = 100;
    public int CustomWeightEndingSoon { get; set; } = 50;
    public int CustomWeightLowAvailability { get; set; } = 0;
    /// <summary>Whether the custom mode also mines games that aren't on the priority list.</summary>
    public bool CustomIncludeNonPriority { get; set; } = true;
    public bool TrayNotifications { get; set; } = true;
    public bool AutostartTray { get; set; }
    public bool Autostart { get; set; }

    /// <summary>Preferred autostart mechanism when "Start with Windows" is enabled.</summary>
    public AutostartMethod AutostartMethod { get; set; } = AutostartMethod.TaskScheduler;

    /// <summary>Optional startup delay in seconds (Task Scheduler method only).</summary>
    public int AutostartDelaySeconds { get; set; }
    public int ConnectionQuality { get; set; } = 1;
    public bool AvailableDropsCheck { get; set; }
    public bool EnableBadgesEmotes { get; set; }
    public string Proxy { get; set; } = "";

    /// <summary>
    /// Prefer channels broadcasting in <see cref="PreferredLanguage"/>, then English,
    /// then any language. Costs extra directory requests per game when enabled.
    /// </summary>
    public bool PreferOwnLanguage { get; set; }

    /// <summary>Twitch broadcaster language code (e.g. "de"). Empty = detect from Windows.</summary>
    public string PreferredLanguage { get; set; } = "";

    /// <summary>The language code actually used: the configured one, or the OS language.</summary>
    [JsonIgnore]
    public string EffectiveLanguage => string.IsNullOrWhiteSpace(PreferredLanguage)
        ? System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant()
        : PreferredLanguage.Trim().ToLowerInvariant();

    /// <summary>Self-heal: automatically restart the mining core after fatal errors.</summary>
    public bool AutoRestartOnError { get; set; } = true;

    /// <summary>
    /// How watch events are sent. Twitch occasionally breaks one of the delivery paths;
    /// this can be switched on the fly (applies from the next watch tick).
    /// </summary>
    public WatchMethod WatchMethod { get; set; } = WatchMethod.Spade;

    /// <summary>Debug log verbosity: Off (no debug.log), Normal, or Verbose (full tracing).</summary>
    public LogVerbosity LogVerbosity { get; set; } = LogVerbosity.Normal;

    // Mini mode (compact farming widget)
    public bool MiniMode { get; set; }
    public MiniBarSource MiniBar { get; set; } = MiniBarSource.Drop;
    public bool MiniShowRemaining { get; set; } = true;
    public bool MiniShowPercent { get; set; } = true;
    public bool MiniAlwaysOnTop { get; set; }
    public double? MiniLeft { get; set; }
    public double? MiniTop { get; set; }

    // Discord / Telegram notifications (one-way). Empty destination = that channel is off.
    public string DiscordWebhookUrl { get; set; } = "";
    public string TelegramBotToken { get; set; } = "";
    public string TelegramChatId { get; set; } = "";
    // per-event opt-ins (all default off)
    public bool NotifyDropClaimed { get; set; }
    public bool NotifyCampaignCompleted { get; set; }
    public bool NotifyMiningStatus { get; set; }
    public bool NotifyErrors { get; set; }

    [JsonIgnore]
    public bool Altered { get; private set; }

    public static Settings Load()
    {
        string path = Constants.SettingsPath;
        string bakPath = path + ".bak";
        Settings? loaded = null;

        if (File.Exists(path))
        {
            try
            {
                loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions);
            }
            catch (Exception ex)
            {
                Logger.Instance.Warning($"Config damaged, starting self-heal ({ex.Message})");
                BackupCorrupt(path);
                loaded = Salvage(path);
            }
        }
        if (loaded is null && File.Exists(bakPath))
        {
            try
            {
                loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(bakPath), JsonOptions);
                if (loaded is not null)
                    Logger.Instance.Warning("Config restored from the last-known-good backup");
            }
            catch (Exception ex)
            {
                Logger.Instance.Warning($"Config backup is also unreadable ({ex.Message})");
            }
        }
        if (loaded is null)
        {
            if (File.Exists(path))
                Logger.Instance.Warning("Config could not be recovered, using defaults");
            loaded = new Settings { ConfigVersion = CurrentConfigVersion };
        }
        loaded.Migrate();
        loaded.Validate();
        return loaded;
    }

    private static void BackupCorrupt(string path)
    {
        try
        {
            File.Copy(path, path + ".corrupt", overwrite: true);
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Field-by-field recovery: reads whatever keys are still parseable from a
    /// damaged config and applies them over the defaults.
    /// </summary>
    private static Settings? Salvage(string path)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (Exception)
        {
            return null; // not even valid JSON: nothing to salvage
        }
        if (root is not JsonObject obj)
            return null;
        var settings = new Settings();
        int recovered = 0, skipped = 0;
        foreach (PropertyInfo prop in typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.SetMethod?.IsPublic != true || prop.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
                continue;
            string key = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(prop.Name);
            if (!obj.TryGetPropertyValue(key, out JsonNode? valueNode))
                continue;
            try
            {
                prop.SetValue(settings, valueNode.Deserialize(prop.PropertyType, JsonOptions));
                recovered++;
            }
            catch (Exception)
            {
                skipped++; // this key keeps its default
            }
        }
        Logger.Instance.Warning(
            $"Config self-heal: recovered {recovered} settings, reset {skipped} to defaults");
        settings.Altered = true; // persist the healed file soon
        return settings;
    }

    /// <summary>Migrates older config versions forward. Add a step per version bump.</summary>
    private void Migrate()
    {
        if (ConfigVersion == CurrentConfigVersion)
            return;
        if (ConfigVersion > CurrentConfigVersion)
        {
            // config written by a newer app version: keep what we understood
            Logger.Instance.Warning(
                $"Config version {ConfigVersion} is newer than this app supports "
                + $"({CurrentConfigVersion}); unknown settings were ignored");
        }
        else
        {
            Logger.Instance.Info($"Config migrated: v{ConfigVersion} -> v{CurrentConfigVersion}");
            // v1 -> v2: config versioning introduced; no structural changes needed.
            // Future migrations go here, e.g.:
            // if (ConfigVersion < 3) { ...rename/convert keys... }
        }
        ConfigVersion = CurrentConfigVersion;
        Altered = true;
    }

    /// <summary>Clamps and sanitizes all values, healing anything out of range.</summary>
    private void Validate()
    {
        ConnectionQuality = Math.Clamp(ConnectionQuality, 1, 6);
        CustomWeightPriority = Math.Clamp(CustomWeightPriority, 0, 200);
        CustomWeightEndingSoon = Math.Clamp(CustomWeightEndingSoon, 0, 200);
        CustomWeightLowAvailability = Math.Clamp(CustomWeightLowAvailability, 0, 200);
        Priority = [.. (Priority ?? []).Where(e => !string.IsNullOrWhiteSpace(e)).Distinct()];
        Exclude = [.. (Exclude ?? []).Where(e => !string.IsNullOrWhiteSpace(e)).Distinct()];
        Proxy ??= "";
        PreferredLanguage ??= "";
        DiscordWebhookUrl ??= "";
        TelegramBotToken ??= "";
        TelegramChatId ??= "";
        AppVersion ??= "";
        if (!Enum.IsDefined(MiningMode))
            MiningMode = MiningMode.PriorityOnly;
        if (!Enum.IsDefined(WatchMethod))
            WatchMethod = WatchMethod.Spade;
        if (!Enum.IsDefined(AutostartMethod))
            AutostartMethod = AutostartMethod.TaskScheduler;
        AutostartDelaySeconds = Math.Clamp(AutostartDelaySeconds, 0, 3600);
        if (!Enum.IsDefined(MiniBar))
            MiniBar = MiniBarSource.Drop;
        if (!Enum.IsDefined(LogVerbosity))
            LogVerbosity = LogVerbosity.Normal;
        if (MiniLeft is not null && !double.IsFinite(MiniLeft.Value))
            MiniLeft = null;
        if (MiniTop is not null && !double.IsFinite(MiniTop.Value))
            MiniTop = null;
    }

    public void Alter() => Altered = true;

    public void Save(bool force = false)
    {
        if (!Altered && !force)
            return;
        try
        {
            AppVersion = typeof(Settings).Assembly.GetName().Version?.ToString(3) ?? "";
            // write-then-rename, so a crash mid-write can't corrupt the settings file
            string tempPath = Constants.SettingsPath + ".new";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tempPath, Constants.SettingsPath, overwrite: true);
            // keep a last-known-good copy for the self-heal path
            File.Copy(Constants.SettingsPath, Constants.SettingsPath + ".bak", overwrite: true);
            Altered = false;
        }
        catch (Exception ex)
        {
            Logger.Instance.Exception("Settings save", ex);
        }
    }

    /// <summary>Index of the first priority entry matching the name, or int.MaxValue.</summary>
    public int PriorityIndex(string name)
    {
        for (int i = 0; i < Priority.Count; i++)
        {
            if (Utils.MatchEntry(Priority[i], name))
                return i;
        }
        return int.MaxValue;
    }

    public bool HasPriority(string name) => PriorityIndex(name) < int.MaxValue;

    public bool IsExcluded(string name) => Exclude.Any(entry => Utils.MatchEntry(entry, name));
}
