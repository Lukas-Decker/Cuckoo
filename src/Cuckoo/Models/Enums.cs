namespace Cuckoo.Models;

/// <summary>Main state machine states, mirroring the original miner's State enum.</summary>
public enum MinerState
{
    Idle,
    InventoryFetch,
    GamesUpdate,
    ChannelsFetch,
    ChannelsCleanup,
    ChannelSwitch,
    Restart,
    Exit,
}

/// <summary>
/// Mining order modes: decide which games get mined, and in what order.
/// Extends the original miner's PriorityMode with scored and custom modes.
/// </summary>
public enum MiningMode
{
    /// <summary>Mine only games in the priority list; all other games are skipped entirely.</summary>
    PriorityOnly = 0,
    /// <summary>Mine all non-excluded games; prefer campaigns whose end date is soonest.</summary>
    EndingSoonest = 1,
    /// <summary>Mine all non-excluded games; prefer campaigns with the lowest availability ratio.</summary>
    LowAvailabilityFirst = 2,
    /// <summary>
    /// Priority list first (in list order), then games with a linked account,
    /// ordered by which campaign ends soonest.
    /// </summary>
    PriorityThenLinked = 3,
    /// <summary>
    /// Priority list games are scored: list position gives up to 100 points and ending
    /// soonest (among the priority games) gives up to 100 points; the summed score decides
    /// the order. After those, linked games ordered by ending soonest.
    /// </summary>
    PriorityScored = 4,
    /// <summary>User-defined scoring built from configurable factor weights.</summary>
    Custom = 5,
}

public enum BenefitType
{
    Unknown,
    Badge,
    Emote,
    DirectEntitlement,
}

/// <summary>What the mini-mode progress bar displays.</summary>
public enum MiniBarSource
{
    Drop = 0,
    Campaign = 1,
    None = 2,
}

/// <summary>How the "minute watched" event is delivered to Twitch.</summary>
public enum WatchMethod
{
    /// <summary>POST to the per-channel Spade telemetry endpoint (website behavior).</summary>
    Spade = 0,
    /// <summary>The sendSpadeEvents GQL mutation.</summary>
    Gql = 1,
}

/// <summary>Mechanism used to start the app with Windows.</summary>
public enum AutostartMethod
{
    /// <summary>HKCU Run registry value. Simple, fires on interactive logon.</summary>
    Registry = 0,
    /// <summary>
    /// A per-instance scheduled task (at logon). More robust, supports a startup delay,
    /// and is the recommended option on servers.
    /// </summary>
    TaskScheduler = 1,
}
