using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuckoo.Core;
using Cuckoo.Models;
using Cuckoo.Services;

namespace Cuckoo.ViewModels;

public sealed partial class WebsocketStatusViewModel : ObservableObject
{
    public int Index { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private string _status = "Disconnected";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private int _topics;

    public string DisplayText => $"Websocket #{Index + 1}: {Status} (topics: {Topics})";
}

public sealed partial class GameEntryViewModel(string name, bool linked) : ObservableObject
{
    public string Name { get; } = name;
    public bool Linked { get; } = linked;
    public string DisplayText => Linked ? Name : $"{Name} (not linked)";
}

/// <summary>
/// The main window's view model. Also implements the GUI surface used by the client
/// (IMinerGui), marshaling all updates onto the WPF dispatcher.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IMinerGui
{
    private const int AlmostDoneSeconds = 10;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly ImageCache _imageCache = new();
    private int _seconds;
    private bool _timerRunning;
    private TimedDrop? _currentDrop;

    public Settings Settings { get; }
    public TwitchClient Client { get; private set; } = null!;

    public static string BrandText { get; } =
        $"Cuckoo v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)} · formerly Twitch Drops Miner";

    public event Action<string, string>? TrayNotification;

    public MainViewModel(Settings settings)
    {
        Settings = settings;
        _dispatcher = Application.Current.Dispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += (_, _) =>
        {
            _seconds--;
            UpdateTime();
            if (_seconds <= 0)
            {
                _timer.Stop();
                _timerRunning = false;
            }
        };
        MiningModes =
        [
            new(MiningMode.PriorityOnly, "Priority list only"),
            new(MiningMode.EndingSoonest, "Priority list, then all games ending soonest"),
            new(MiningMode.LowAvailabilityFirst, "Priority list, then all games by low availability"),
            new(MiningMode.PriorityThenLinked, "Priority list, then linked games ending soonest"),
            new(MiningMode.PriorityScored, "Scored: list position + ending soonest, then linked games"),
            new(MiningMode.Custom, "Custom scoring (configure below)"),
        ];
        _selectedMiningMode = MiningModes.First(m => m.Mode == Settings.MiningMode);
        _customWeightPriority = Settings.CustomWeightPriority;
        _customWeightEndingSoon = Settings.CustomWeightEndingSoon;
        _customWeightLowAvailability = Settings.CustomWeightLowAvailability;
        _customIncludeNonPriority = Settings.CustomIncludeNonPriority;
        _trayNotificationsEnabled = Settings.TrayNotifications;
        _autoRestartOnError = Settings.AutoRestartOnError;
        _availableDropsCheckEnabled = Settings.AvailableDropsCheck;
        _badgesEmotesEnabled = Settings.EnableBadgesEmotes;
        // reflect the actual OS state; prefer the live entry's method over the stored preference
        _applyingAutostart = true;
        AutostartMethod? activeMethod = AutostartService.CurrentMethod();
        _autostartEnabled = activeMethod is not null;
        _selectedAutostartMethod = AutostartMethods.First(
            o => o.Method == (activeMethod ?? Settings.AutostartMethod));
        _autostartTrayEnabled = Settings.AutostartTray;
        _autostartDelaySeconds = Settings.AutostartDelaySeconds;
        _applyingAutostart = false;
        _proxyText = Settings.Proxy;
        _preferOwnLanguage = Settings.PreferOwnLanguage;
        _selectedLanguage = Languages.FirstOrDefault(
            l => l.Code.Equals(Settings.PreferredLanguage, StringComparison.OrdinalIgnoreCase))
            ?? Languages[0];
        _connectionQuality = Settings.ConnectionQuality;
        _selectedWatchMethod = WatchMethods.First(o => o.Method == Settings.WatchMethod);
        _selectedLogVerbosity = LogVerbosities.First(o => o.Verbosity == Settings.LogVerbosity);
        _selectedMiniBar = MiniBarOptions.First(o => o.Source == Settings.MiniBar);
        _miniShowRemaining = Settings.MiniShowRemaining;
        _miniShowPercent = Settings.MiniShowPercent;
        _miniAlwaysOnTop = Settings.MiniAlwaysOnTop;
        _discordWebhookUrl = Settings.DiscordWebhookUrl;
        _telegramBotToken = Settings.TelegramBotToken;
        _telegramChatId = Settings.TelegramChatId;
        _notifyDropClaimed = Settings.NotifyDropClaimed;
        _notifyCampaignCompleted = Settings.NotifyCampaignCompleted;
        _notifyMiningStatus = Settings.NotifyMiningStatus;
        _notifyErrors = Settings.NotifyErrors;
        PriorityList = [.. Settings.Priority];
        ExcludeList = [.. Settings.Exclude];
        CampaignsView = CollectionViewSource.GetDefaultView(Campaigns);
        CampaignsView.Filter = FilterCampaign;
        ChannelsView = CollectionViewSource.GetDefaultView(Channels);
        ChannelsView.Filter = item => item is Channel channel
            && (MatchesSearch(channel.Name, ChannelSearchText)
                || MatchesSearch(channel.GameName, ChannelSearchText));
        AvailableGamesView = CollectionViewSource.GetDefaultView(AvailableGames);
        AvailableGamesView.Filter = item => item is GameEntryViewModel game
            && MatchesSearch(game.Name, AvailableGamesSearchText);
        PriorityView = CollectionViewSource.GetDefaultView(PriorityList);
        PriorityView.Filter = item => item is string entry
            && MatchesSearch(entry, PrioritySearchText);
        ExcludeView = CollectionViewSource.GetDefaultView(ExcludeList);
        ExcludeView.Filter = item => item is string entry
            && MatchesSearch(entry, ExcludeSearchText);
        ClearDropDisplay();
    }

    private static bool MatchesSearch(string value, string search)
        => string.IsNullOrWhiteSpace(search)
            || value.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);

    public void AttachClient(TwitchClient client) => Client = client;

    private void OnUi(Action action)
    {
        // a failing UI update must never take the mining core down with it
        void Guarded()
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Logger.Instance.Exception("UI update", ex);
            }
        }
        if (_dispatcher.CheckAccess())
            Guarded();
        else
            _dispatcher.BeginInvoke((Action)Guarded);
    }

    #region Window / tray state

    public bool CloseRequested { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDotBrush))]
    [NotifyPropertyChangedFor(nameof(MiniTitleText))]
    private string _trayIconState = "cuckoo";

    private static readonly Dictionary<string, Brush> StatusDotBrushes = new()
    {
        ["active"] = new SolidColorBrush(Color.FromRgb(0x37, 0xB2, 0x4D)),
        ["maint"] = new SolidColorBrush(Color.FromRgb(0xE8, 0xB8, 0x30)),
        ["error"] = new SolidColorBrush(Color.FromRgb(0xE0, 0x31, 0x31)),
        ["idle"] = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x98)),
        ["cuckoo"] = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x98)),
    };

    static MainViewModel()
    {
        foreach (Brush brush in StatusDotBrushes.Values)
            brush.Freeze();
    }

    public Brush StatusDotBrush
        => StatusDotBrushes.GetValueOrDefault(TrayIconState, StatusDotBrushes["idle"]);

    public string MiniTitleText => TrayIconState switch
    {
        "active" => "Farming",
        "maint" => "Working",
        "error" => "Error",
        _ => "Idle",
    };

    [ObservableProperty]
    private string _trayToolTip = "Cuckoo";

    public void ChangeTrayIcon(string state) => OnUi(() => TrayIconState = state);

    private void UpdateTrayTitle(TimedDrop? drop)
    {
        TrayToolTip = drop is null
            ? "Cuckoo"
            : $"Cuckoo\nMining: {drop.RewardsText()} ({drop.Campaign.Game.Name}, {drop.Progress:P1})";
    }

    public void TrayNotify(string message, string title)
    {
        if (Settings.TrayNotifications)
            OnUi(() => TrayNotification?.Invoke(message, title));
    }

    #endregion

    #region Status, output, websockets

    [ObservableProperty]
    private string _statusText = "Starting...";

    [ObservableProperty]
    private string _outputText = "";

    public void SetStatus(string status) => OnUi(() => StatusText = status);

    public void Print(string message) => OnUi(() =>
    {
        string line = $"{DateTime.Now:HH:mm:ss}: {message}";
        OutputText = OutputText.Length == 0 ? line : $"{OutputText}\n{line}";
        // keep the log from growing indefinitely
        if (OutputText.Length > 100_000)
            OutputText = OutputText[^80_000..];
    });

    public ObservableCollection<WebsocketStatusViewModel> WebsocketStatuses { get; } = [];

    public void UpdateWebsocketStatus(int index, string? status, int? topics) => OnUi(() =>
    {
        while (WebsocketStatuses.Count <= index)
            WebsocketStatuses.Add(new WebsocketStatusViewModel { Index = WebsocketStatuses.Count });
        if (status is not null)
            WebsocketStatuses[index].Status = status;
        if (topics is not null)
            WebsocketStatuses[index].Topics = topics.Value;
    });

    public void RemoveWebsocketStatus(int index) => OnUi(() =>
    {
        if (index == WebsocketStatuses.Count - 1)
            WebsocketStatuses.RemoveAt(index);
        else if (index < WebsocketStatuses.Count)
        {
            WebsocketStatuses[index].Status = "Disconnected";
            WebsocketStatuses[index].Topics = 0;
        }
    });

    #endregion

    #region Login

    [ObservableProperty]
    private string _loginStatus = "Not logged in";

    [ObservableProperty]
    private string _userText = "";

    [ObservableProperty]
    private bool _isLoggedIn;

    [ObservableProperty]
    private bool _showLoginPanel;

    [ObservableProperty]
    private string _deviceCode = "";

    private string _verificationUri = "";

    public void ShowDeviceCode(string verificationUri, string userCode) => OnUi(() =>
    {
        _verificationUri = verificationUri;
        DeviceCode = userCode;
        ShowLoginPanel = true;
        IsLoggedIn = false;
        UserText = "";
        LoginStatus = "Waiting for you to authorize the device...";
        // no automatic browser opening: the user opens the page
        // via the "Open activation page" button when ready
        Print($"Login: open {verificationUri} and enter code: {userCode}");
    });

    public void LoginUpdate(string status, long? userId = null, string? userName = null) => OnUi(() =>
    {
        LoginStatus = status;
        if (userId is not null)
        {
            UserText = $"{userName} ({userId})";
            ShowLoginPanel = false;
            IsLoggedIn = true;
        }
    });

    [RelayCommand]
    private void Logout()
    {
        if (!IsLoggedIn)
            return;
        IsLoggedIn = false;
        UserText = "";
        LoginStatus = "Logging out...";
        Client.Logout();
    }

    [RelayCommand]
    private void OpenLoginPage()
    {
        if (!string.IsNullOrEmpty(_verificationUri))
            OpenUrl(_verificationUri);
    }

    [RelayCommand]
    private void CopyCode()
    {
        if (!string.IsNullOrEmpty(DeviceCode))
            Clipboard.SetText(DeviceCode);
    }

    private static void OpenUrl(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    #endregion

    #region Campaign progress panel

    [ObservableProperty]
    private string _progressGameName = "...";

    [ObservableProperty]
    private string _progressCampaignName = "...";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MiniPercentText))]
    private string _campaignPercentText = "-%";

    [ObservableProperty]
    private string _campaignRemainingText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MiniProgressValue))]
    private double _campaignProgressValue;

    [ObservableProperty]
    private string _dropRewardsText = "...";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MiniPercentText))]
    private string _dropPercentText = "-%";

    [ObservableProperty]
    private string _dropRemainingText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MiniProgressValue))]
    private double _dropProgressValue;

    /// <summary>Campaign of the currently displayed drop; drives the mini-mode box art.</summary>
    [ObservableProperty]
    private DropsCampaign? _currentCampaign;

    public void DisplayDrop(TimedDrop? drop, bool countdown = true, bool subone = false) => OnUi(() =>
    {
        _currentDrop = drop;
        StopTimerUi();
        UpdateTrayTitle(drop);
        CurrentCampaign = drop?.Campaign;
        if (drop is null)
        {
            ClearDropDisplay();
            return;
        }
        DropRewardsText = drop.RewardsText();
        DropProgressValue = drop.Progress;
        DropPercentText = $"{drop.Progress:P1}";
        DropsCampaign campaign = drop.Campaign;
        ProgressCampaignName = campaign.Name;
        ProgressGameName = campaign.Game.Name;
        CampaignProgressValue = campaign.Progress;
        CampaignPercentText = $"{campaign.Progress:P1} ({campaign.ClaimedDrops}/{campaign.TotalDrops})";
        if (countdown)
        {
            // restart the seconds countdown timer
            StartTimerUi();
        }
        else if (subone)
        {
            // display the remaining time at 0 seconds (after subtracting a minute);
            // the watch loop subtracts this minute right after the first watch payload
            UpdateTime(0);
        }
        else
        {
            // display the full time with no subtracting
            UpdateTime(60);
        }
    });

    public void ClearDrop() => OnUi(() =>
    {
        _currentDrop = null;
        StopTimerUi();
        UpdateTrayTitle(null);
        CurrentCampaign = null;
        ClearDropDisplay();
    });

    private void ClearDropDisplay()
    {
        DropRewardsText = "...";
        DropProgressValue = 0;
        DropPercentText = "-%";
        ProgressCampaignName = "...";
        ProgressGameName = "...";
        CampaignProgressValue = 0;
        CampaignPercentText = "-%";
        UpdateTime(0);
    }

    private void StartTimerUi()
    {
        if (_timerRunning)
            return;
        if (_currentDrop is null || _currentDrop.RemainingMinutes <= 0)
        {
            // starting the timer at 0 drop minutes: a single instant update at 60 seconds,
            // to avoid subtracting a minute from the campaign minutes
            UpdateTime(60);
        }
        else
        {
            _seconds = 60;
            _timerRunning = true;
            UpdateTime();
            _timer.Start();
        }
    }

    private void StopTimerUi()
    {
        _timer.Stop();
        _timerRunning = false;
    }

    public void StopTimer() => OnUi(StopTimerUi);

    public bool MinuteAlmostDone()
        => !_timerRunning || _seconds <= AlmostDoneSeconds;

    private (int Hours, int Minutes) DivMod(int minutes)
    {
        if (_seconds < 60 && minutes > 0)
            minutes--;
        return (minutes / 60, minutes % 60);
    }

    private void UpdateTime(int? seconds = null)
    {
        if (seconds is not null)
            _seconds = seconds.Value;
        int dropMinutes = _currentDrop?.RemainingMinutes ?? 0;
        int campaignMinutes = _currentDrop?.Campaign.RemainingMinutes ?? 0;
        int dSeconds = _seconds % 60;
        var (hours, minutes) = DivMod(dropMinutes);
        DropRemainingText = $"{hours,2}:{minutes:00}:{dSeconds:00} remaining";
        (hours, minutes) = DivMod(campaignMinutes);
        CampaignRemainingText = $"{hours,2}:{minutes:00}:{dSeconds:00} remaining";
    }

    #endregion

    #region Channels

    public ObservableCollection<Channel> Channels { get; } = [];

    /// <summary>Filtered view of the channels, bound by the channel list.</summary>
    public ICollectionView ChannelsView { get; }

    [ObservableProperty]
    private string _channelSearchText = "";

    partial void OnChannelSearchTextChanged(string value) => ChannelsView.Refresh();

    [ObservableProperty]
    private Channel? _selectedChannel;

    public void AddChannel(Channel channel) => OnUi(() => Channels.Add(channel));

    public void ClearChannels() => OnUi(() =>
    {
        Channels.Clear();
        SelectedChannel = null;
    });

    public void RemoveChannel(Channel channel) => OnUi(() =>
    {
        Channels.Remove(channel);
        if (SelectedChannel == channel)
            SelectedChannel = null;
    });

    public void SetWatching(Channel channel) => OnUi(() =>
    {
        foreach (Channel ch in Channels)
            ch.IsWatching = ch.Equals(channel);
        channel.IsWatching = true;
    });

    public void ClearWatching() => OnUi(() =>
    {
        foreach (Channel ch in Channels)
            ch.IsWatching = false;
    });

    public Channel? GetSelectedChannel() => SelectedChannel;

    public void ClearSelectedChannel() => OnUi(() => SelectedChannel = null);

    /// <summary>Double-click on a channel row: switch to that channel.</summary>
    [RelayCommand]
    private void SwitchToSelected()
    {
        if (SelectedChannel is not null)
            Client.ChangeState(MinerState.ChannelSwitch);
    }

    [RelayCommand]
    private void Reload() => Client.ChangeState(MinerState.InventoryFetch);

    #endregion

    #region Inventory

    public ObservableCollection<DropsCampaign> Campaigns { get; } = [];

    /// <summary>Filtered view of the campaigns, bound by the inventory tab.</summary>
    public ICollectionView CampaignsView { get; }

    [ObservableProperty]
    private bool _showUpcomingCampaigns = true;

    [ObservableProperty]
    private bool _showExpiredCampaigns;

    [ObservableProperty]
    private bool _showFinishedCampaigns = true;

    [ObservableProperty]
    private string _campaignSearchText = "";

    partial void OnShowUpcomingCampaignsChanged(bool value) => CampaignsView.Refresh();
    partial void OnShowExpiredCampaignsChanged(bool value) => CampaignsView.Refresh();
    partial void OnShowFinishedCampaignsChanged(bool value) => CampaignsView.Refresh();
    partial void OnCampaignSearchTextChanged(string value) => CampaignsView.Refresh();

    private bool FilterCampaign(object item)
        => item is DropsCampaign campaign
            && (ShowUpcomingCampaigns || !campaign.Upcoming)
            && (ShowExpiredCampaigns || !campaign.Expired)
            && (ShowFinishedCampaigns || !campaign.Finished)
            && (MatchesSearch(campaign.Game.Name, CampaignSearchText)
                || MatchesSearch(campaign.Name, CampaignSearchText)
                || campaign.Drops.Any(d => MatchesSearch(d.RewardsText(), CampaignSearchText)));

    public void ClearInventory() => OnUi(Campaigns.Clear);

    public void AddCampaigns(IReadOnlyList<DropsCampaign> campaigns)
    {
        // single dispatcher hop for the whole batch, so the UI doesn't churn
        OnUi(() =>
        {
            foreach (DropsCampaign campaign in campaigns)
                Campaigns.Add(campaign);
        });
        // load all box art images off the UI thread (disk-cached, decoded, frozen)
        _ = Task.Run(async () =>
        {
            foreach (DropsCampaign campaign in campaigns)
                campaign.BoxArt = await _imageCache.GetAsync(campaign.ImageUrl, decodeWidth: 120)
                    .ConfigureAwait(false);
        });
    }

    [RelayCommand]
    private void OpenCampaignLink(DropsCampaign? campaign)
    {
        if (campaign is not null && !string.IsNullOrEmpty(campaign.LinkUrl))
            OpenUrl(campaign.LinkUrl);
    }

    #endregion

    #region Settings tab

    public sealed record MiningModeOption(MiningMode Mode, string Label);

    public IReadOnlyList<MiningModeOption> MiningModes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomMode))]
    private MiningModeOption _selectedMiningMode;

    public bool IsCustomMode => SelectedMiningMode.Mode == MiningMode.Custom;

    partial void OnSelectedMiningModeChanged(MiningModeOption value)
    {
        Settings.MiningMode = value.Mode;
        Settings.Alter();
        Settings.Save();
    }

    // custom scoring weights (0-200 each)
    [ObservableProperty]
    private double _customWeightPriority;

    [ObservableProperty]
    private double _customWeightEndingSoon;

    [ObservableProperty]
    private double _customWeightLowAvailability;

    [ObservableProperty]
    private bool _customIncludeNonPriority;

    partial void OnCustomWeightPriorityChanged(double value)
        => SaveCustomWeights();

    partial void OnCustomWeightEndingSoonChanged(double value)
        => SaveCustomWeights();

    partial void OnCustomWeightLowAvailabilityChanged(double value)
        => SaveCustomWeights();

    partial void OnCustomIncludeNonPriorityChanged(bool value)
        => SaveCustomWeights();

    private void SaveCustomWeights()
    {
        Settings.CustomWeightPriority = (int)Math.Round(CustomWeightPriority);
        Settings.CustomWeightEndingSoon = (int)Math.Round(CustomWeightEndingSoon);
        Settings.CustomWeightLowAvailability = (int)Math.Round(CustomWeightLowAvailability);
        Settings.CustomIncludeNonPriority = CustomIncludeNonPriority;
        Settings.Alter();
        Settings.Save();
    }

    [ObservableProperty]
    private bool _trayNotificationsEnabled;

    partial void OnTrayNotificationsEnabledChanged(bool value)
    {
        Settings.TrayNotifications = value;
        Settings.Alter();
        Settings.Save();
    }

    [ObservableProperty]
    private bool _availableDropsCheckEnabled;

    partial void OnAvailableDropsCheckEnabledChanged(bool value)
    {
        Settings.AvailableDropsCheck = value;
        Settings.Alter();
        Settings.Save();
    }

    [ObservableProperty]
    private bool _badgesEmotesEnabled;

    partial void OnBadgesEmotesEnabledChanged(bool value)
    {
        Settings.EnableBadgesEmotes = value;
        Settings.Alter();
        Settings.Save();
    }

    [ObservableProperty]
    private bool _autoRestartOnError;

    partial void OnAutoRestartOnErrorChanged(bool value)
    {
        Settings.AutoRestartOnError = value;
        Settings.Alter();
        Settings.Save();
    }

    public sealed record AutostartMethodOption(AutostartMethod Method, string Label);

    public IReadOnlyList<AutostartMethodOption> AutostartMethods { get; } =
    [
        new(AutostartMethod.TaskScheduler, "Task Scheduler (recommended, per-instance)"),
        new(AutostartMethod.Registry, "Registry Run key (per-instance)"),
    ];

    [ObservableProperty]
    private AutostartMethodOption _selectedAutostartMethod;

    [ObservableProperty]
    private bool _autostartEnabled;

    [ObservableProperty]
    private bool _autostartTrayEnabled;

    [ObservableProperty]
    private int _autostartDelaySeconds;

    private bool _applyingAutostart;

    partial void OnAutostartEnabledChanged(bool value) => ApplyAutostart();
    partial void OnSelectedAutostartMethodChanged(AutostartMethodOption value) => ApplyAutostart();
    partial void OnAutostartDelaySecondsChanged(int value) => ApplyAutostart();

    partial void OnAutostartTrayEnabledChanged(bool value)
    {
        Settings.AutostartTray = value;
        Settings.Alter();
        Settings.Save();
        ApplyAutostart();
    }

    /// <summary>Persists the autostart preferences and reconciles the OS entries.</summary>
    private void ApplyAutostart()
    {
        if (_applyingAutostart)
            return; // avoid re-entrancy while syncing the checkbox back from the OS
        Settings.AutostartMethod = SelectedAutostartMethod.Method;
        Settings.AutostartDelaySeconds = Math.Clamp(AutostartDelaySeconds, 0, 3600);
        Settings.Alter();
        Settings.Save();
        AutostartService.Apply(
            AutostartEnabled, SelectedAutostartMethod.Method, AutostartTrayEnabled, Settings.AutostartDelaySeconds);
    }

    public sealed record LanguageOption(string Code, string Label);

    /// <summary>Twitch broadcaster languages; empty code means "detect from Windows".</summary>
    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new("", "Auto (system language)"),
        new("en", "English (en)"),
        new("de", "German (de)"),
        new("es", "Spanish (es)"),
        new("fr", "French (fr)"),
        new("it", "Italian (it)"),
        new("pt", "Portuguese (pt)"),
        new("nl", "Dutch (nl)"),
        new("pl", "Polish (pl)"),
        new("ru", "Russian (ru)"),
        new("uk", "Ukrainian (uk)"),
        new("cs", "Czech (cs)"),
        new("hu", "Hungarian (hu)"),
        new("ro", "Romanian (ro)"),
        new("sv", "Swedish (sv)"),
        new("no", "Norwegian (no)"),
        new("da", "Danish (da)"),
        new("fi", "Finnish (fi)"),
        new("tr", "Turkish (tr)"),
        new("ar", "Arabic (ar)"),
        new("ja", "Japanese (ja)"),
        new("ko", "Korean (ko)"),
        new("zh-cn", "Chinese (zh-cn)"),
        new("th", "Thai (th)"),
        new("vi", "Vietnamese (vi)"),
        new("id", "Indonesian (id)"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LanguageHint))]
    private bool _preferOwnLanguage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LanguageHint))]
    private LanguageOption _selectedLanguage;

    /// <summary>Explains the resulting order, resolving "Auto" to the detected code.</summary>
    public string LanguageHint
    {
        get
        {
            if (!PreferOwnLanguage)
                return "  Off: channels are ordered by viewers only.";
            string code = string.IsNullOrEmpty(SelectedLanguage.Code)
                ? Settings.EffectiveLanguage
                : SelectedLanguage.Code;
            return code.Equals("en", StringComparison.OrdinalIgnoreCase)
                ? "  Order: English first, then the rest (most viewers first within each group)."
                : $"  Order: {code}, then English, then the rest (most viewers first within each group).";
        }
    }

    partial void OnPreferOwnLanguageChanged(bool value) => SaveLanguageSettings();
    partial void OnSelectedLanguageChanged(LanguageOption value) => SaveLanguageSettings();

    private void SaveLanguageSettings()
    {
        Settings.PreferOwnLanguage = PreferOwnLanguage;
        Settings.PreferredLanguage = SelectedLanguage.Code;
        Settings.Alter();
        Settings.Save();
    }

    public sealed record WatchMethodOption(WatchMethod Method, string Label);

    public IReadOnlyList<WatchMethodOption> WatchMethods { get; } =
    [
        new(WatchMethod.Spade, "Spade (website telemetry, default)"),
        new(WatchMethod.Gql, "GQL mutation (legacy)"),
    ];

    [ObservableProperty]
    private WatchMethodOption _selectedWatchMethod;

    partial void OnSelectedWatchMethodChanged(WatchMethodOption value)
    {
        if (Settings.WatchMethod == value.Method)
            return;
        Settings.WatchMethod = value.Method;
        Settings.Alter();
        Settings.Save();
        Print($"Watch method switched to {value.Label}; applies from the next watch tick.");
    }

    public sealed record LogVerbosityOption(LogVerbosity Verbosity, string Label);

    public IReadOnlyList<LogVerbosityOption> LogVerbosities { get; } =
    [
        new(LogVerbosity.Off, "Off (no debug log)"),
        new(LogVerbosity.Normal, "Normal"),
        new(LogVerbosity.Verbose, "Verbose (full tracing)"),
    ];

    [ObservableProperty]
    private LogVerbosityOption _selectedLogVerbosity;

    partial void OnSelectedLogVerbosityChanged(LogVerbosityOption value)
    {
        Settings.LogVerbosity = value.Verbosity;
        Logger.Instance.Verbosity = value.Verbosity; // applies immediately
        Settings.Alter();
        Settings.Save();
    }

    public IReadOnlyList<int> ConnectionQualities { get; } = [1, 2, 3, 4, 5, 6];

    [ObservableProperty]
    private int _connectionQuality;

    partial void OnConnectionQualityChanged(int value)
    {
        Settings.ConnectionQuality = value;
        Settings.Alter();
        Settings.Save();
    }

    [ObservableProperty]
    private string _proxyText;

    [RelayCommand]
    private void ApplyProxy()
    {
        Settings.Proxy = ProxyText.Trim();
        Settings.Alter();
        Settings.Save();
        Print("Proxy setting saved. Use Reload for it to take effect.");
    }

    // ---- Mini mode ----

    public sealed record MiniBarOption(MiniBarSource Source, string Label);

    public IReadOnlyList<MiniBarOption> MiniBarOptions { get; } =
    [
        new(MiniBarSource.Drop, "Drop progress"),
        new(MiniBarSource.Campaign, "Campaign progress"),
        new(MiniBarSource.None, "No bar"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MiniProgressValue))]
    [NotifyPropertyChangedFor(nameof(MiniPercentText))]
    [NotifyPropertyChangedFor(nameof(MiniBarVisible))]
    private MiniBarOption _selectedMiniBar;

    partial void OnSelectedMiniBarChanged(MiniBarOption value)
    {
        Settings.MiniBar = value.Source;
        Settings.Alter();
        Settings.Save();
    }

    [ObservableProperty]
    private bool _miniShowRemaining;

    [ObservableProperty]
    private bool _miniShowPercent;

    [ObservableProperty]
    private bool _miniAlwaysOnTop;

    partial void OnMiniShowRemainingChanged(bool value) => SaveMiniSettings();
    partial void OnMiniShowPercentChanged(bool value) => SaveMiniSettings();
    partial void OnMiniAlwaysOnTopChanged(bool value) => SaveMiniSettings();

    private void SaveMiniSettings()
    {
        Settings.MiniShowRemaining = MiniShowRemaining;
        Settings.MiniShowPercent = MiniShowPercent;
        Settings.MiniAlwaysOnTop = MiniAlwaysOnTop;
        Settings.Alter();
        Settings.Save();
    }

    public double MiniProgressValue => SelectedMiniBar.Source switch
    {
        MiniBarSource.Drop => DropProgressValue,
        MiniBarSource.Campaign => CampaignProgressValue,
        _ => 0,
    };

    public string MiniPercentText => SelectedMiniBar.Source switch
    {
        MiniBarSource.Drop => DropPercentText,
        MiniBarSource.Campaign => CampaignPercentText,
        _ => "",
    };

    public bool MiniBarVisible => SelectedMiniBar.Source != MiniBarSource.None;

    // ---- Discord / Telegram notifications ----

    [ObservableProperty]
    private string _discordWebhookUrl;

    [ObservableProperty]
    private string _telegramBotToken;

    [ObservableProperty]
    private string _telegramChatId;

    [ObservableProperty]
    private bool _notifyDropClaimed;

    [ObservableProperty]
    private bool _notifyCampaignCompleted;

    [ObservableProperty]
    private bool _notifyMiningStatus;

    [ObservableProperty]
    private bool _notifyErrors;

    partial void OnNotifyDropClaimedChanged(bool value) => SaveNotificationSettings();
    partial void OnNotifyCampaignCompletedChanged(bool value) => SaveNotificationSettings();
    partial void OnNotifyMiningStatusChanged(bool value) => SaveNotificationSettings();
    partial void OnNotifyErrorsChanged(bool value) => SaveNotificationSettings();

    private void SaveNotificationSettings()
    {
        Settings.DiscordWebhookUrl = DiscordWebhookUrl.Trim();
        Settings.TelegramBotToken = TelegramBotToken.Trim();
        Settings.TelegramChatId = TelegramChatId.Trim();
        Settings.NotifyDropClaimed = NotifyDropClaimed;
        Settings.NotifyCampaignCompleted = NotifyCampaignCompleted;
        Settings.NotifyMiningStatus = NotifyMiningStatus;
        Settings.NotifyErrors = NotifyErrors;
        Settings.Alter();
        Settings.Save();
    }

    [RelayCommand]
    private async Task SaveAndTestNotificationsAsync()
    {
        SaveNotificationSettings();
        if (!Client.Notifications.AnyConfigured)
        {
            Print("Notifications: set a Discord webhook URL and/or a Telegram bot token + chat ID first.");
            return;
        }
        Print("Notifications: settings saved, sending a test message...");
        await Client.Notifications.SendTestAsync();
        Print("Notifications: test message sent (check Discord/Telegram, and the log for any errors).");
    }

    // game lists
    private readonly List<GameEntryViewModel> _gamesCatalog = [];

    public ObservableCollection<GameEntryViewModel> AvailableGames { get; } = [];
    public ObservableCollection<string> PriorityList { get; }
    public ObservableCollection<string> ExcludeList { get; }

    // filtered views + search texts for the three game lists
    public ICollectionView AvailableGamesView { get; }
    public ICollectionView PriorityView { get; }
    public ICollectionView ExcludeView { get; }

    [ObservableProperty]
    private string _availableGamesSearchText = "";

    [ObservableProperty]
    private string _prioritySearchText = "";

    [ObservableProperty]
    private string _excludeSearchText = "";

    partial void OnAvailableGamesSearchTextChanged(string value) => AvailableGamesView.Refresh();
    partial void OnPrioritySearchTextChanged(string value) => PriorityView.Refresh();
    partial void OnExcludeSearchTextChanged(string value) => ExcludeView.Refresh();

    [ObservableProperty]
    private GameEntryViewModel? _selectedAvailableGame;

    [ObservableProperty]
    private string? _selectedPriorityEntry;

    [ObservableProperty]
    private string? _selectedExcludeEntry;

    [ObservableProperty]
    private string _patternText = "";

    public void SetGames(IReadOnlyCollection<Game> games, IReadOnlySet<string> linkedGameNames) => OnUi(() =>
    {
        _gamesCatalog.Clear();
        _gamesCatalog.AddRange(games
            .Select(g => new GameEntryViewModel(g.Name, linkedGameNames.Contains(g.Name)))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase));
        RefreshAvailableGames();
    });

    private void RefreshAvailableGames()
    {
        AvailableGames.Clear();
        foreach (GameEntryViewModel game in _gamesCatalog)
        {
            if (!Settings.Priority.Contains(game.Name) && !Settings.Exclude.Contains(game.Name))
                AvailableGames.Add(game);
        }
    }

    private void SaveGameLists()
    {
        Settings.Priority = [.. PriorityList];
        Settings.Exclude = [.. ExcludeList];
        Settings.Alter();
        Settings.Save();
        RefreshAvailableGames();
    }

    [RelayCommand]
    private void AddToPriority()
    {
        if (SelectedAvailableGame is { } game && !PriorityList.Contains(game.Name))
        {
            PriorityList.Add(game.Name);
            SaveGameLists();
        }
    }

    [RelayCommand]
    private void AddToExclude()
    {
        if (SelectedAvailableGame is { } game && !ExcludeList.Contains(game.Name))
        {
            ExcludeList.Add(game.Name);
            SaveGameLists();
        }
    }

    [RelayCommand]
    private void RemoveFromPriority()
    {
        if (SelectedPriorityEntry is { } entry)
        {
            PriorityList.Remove(entry);
            SaveGameLists();
        }
    }

    [RelayCommand]
    private void RemoveFromExclude()
    {
        if (SelectedExcludeEntry is { } entry)
        {
            ExcludeList.Remove(entry);
            SaveGameLists();
        }
    }

    [RelayCommand]
    private void MovePriorityUp() => MovePriority(-1);

    [RelayCommand]
    private void MovePriorityDown() => MovePriority(+1);

    private void MovePriority(int amount)
    {
        if (SelectedPriorityEntry is not { } entry)
            return;
        int index = PriorityList.IndexOf(entry);
        int newIndex = index + amount;
        if (index < 0 || newIndex < 0 || newIndex >= PriorityList.Count)
            return;
        PriorityList.Move(index, newIndex);
        SelectedPriorityEntry = entry;
        SaveGameLists();
    }

    [RelayCommand]
    private void AddPatternToPriority()
    {
        string pattern = PatternText.Trim();
        if (pattern.Length > 0 && !PriorityList.Contains(pattern))
        {
            PriorityList.Add(pattern);
            PatternText = "";
            SaveGameLists();
        }
    }

    [RelayCommand]
    private void AddPatternToExclude()
    {
        string pattern = PatternText.Trim();
        if (pattern.Length > 0 && !ExcludeList.Contains(pattern))
        {
            ExcludeList.Add(pattern);
            PatternText = "";
            SaveGameLists();
        }
    }

    #endregion
}
