using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Cuckoo.Core;

namespace Cuckoo.Models;

public sealed class Benefit
{
    public string Id { get; }
    public string Name { get; }
    public BenefitType Type { get; }
    public string ImageUrl { get; }

    public Benefit(JsonNode data)
    {
        var benefit = data["benefit"]!;
        Id = benefit["id"]!.GetValue<string>();
        Name = benefit["name"]!.GetValue<string>();
        Type = benefit["distributionType"]?.GetValue<string>() switch
        {
            "BADGE" => BenefitType.Badge,
            "EMOTE" => BenefitType.Emote,
            "DIRECT_ENTITLEMENT" => BenefitType.DirectEntitlement,
            _ => BenefitType.Unknown,
        };
        ImageUrl = benefit["imageAssetURL"]?.GetValue<string>() ?? "";
    }

    public bool IsBadgeOrEmote => Type is BenefitType.Badge or BenefitType.Emote;
}

/// <summary>A time-based drop within a campaign.</summary>
public sealed partial class TimedDrop : ObservableObject
{
    private readonly TwitchClient _twitch;

    public string Id { get; }
    public string Name { get; }
    public DropsCampaign Campaign { get; }
    public IReadOnlyList<Benefit> Benefits { get; }
    public DateTime StartsAt { get; }
    public DateTime EndsAt { get; }
    public IReadOnlyList<string> PreconditionDrops { get; }
    public string? ClaimId { get; private set; }
    public int RequiredMinutes { get; }

    public bool IsClaimed { get; private set; }
    public int RealCurrentMinutes { get; private set; }
    public int ExtraCurrentMinutes { get; private set; }

    public TimedDrop(DropsCampaign campaign, JsonNode data, IReadOnlyDictionary<string, DateTime> claimedBenefits)
    {
        _twitch = campaign.Client;
        Campaign = campaign;
        Id = data["id"]!.GetValue<string>();
        Name = data["name"]!.GetValue<string>();
        Benefits = [.. (data["benefitEdges"] as JsonArray ?? []).Select(b => new Benefit(b!))];
        StartsAt = Utils.ParseTimestamp(data["startAt"]!.GetValue<string>());
        EndsAt = Utils.ParseTimestamp(data["endAt"]!.GetValue<string>());
        if (data["self"] is JsonNode self)
        {
            ClaimId = self["dropInstanceID"]?.GetValue<string>();
            IsClaimed = self["isClaimed"]?.GetValue<bool>() ?? false;
            RealCurrentMinutes = self["currentMinutesWatched"]?.GetValue<int>() ?? 0;
        }
        else
        {
            // Without a self edge, use claimed benefits timestamps to determine
            // (with pretty good certainty) whether this drop has been claimed.
            var stamps = Benefits
                .Where(b => claimedBenefits.ContainsKey(b.Id))
                .Select(b => claimedBenefits[b.Id])
                .ToList();
            if (stamps.Count > 0 && stamps.All(dt => StartsAt <= dt && dt < EndsAt))
                IsClaimed = true;
        }
        PreconditionDrops = [.. (data["preconditionDrops"] as JsonArray ?? []).Select(d => d!["id"]!.GetValue<string>())];
        RequiredMinutes = data["requiredMinutesWatched"]!.GetValue<int>();
        if (IsClaimed)
        {
            // claimed drops may report inconsistent current minutes; overwrite them
            RealCurrentMinutes = RequiredMinutes;
        }
    }

    public bool PreconditionsMet
        => PreconditionDrops.All(pid => Campaign.TimedDrops[pid].IsClaimed);

    public int CurrentMinutes => RealCurrentMinutes + ExtraCurrentMinutes;
    public int RemainingMinutes => RequiredMinutes - CurrentMinutes;

    public int TotalRequiredMinutes => RequiredMinutes + PreconditionDrops
        .Select(pid => Campaign.TimedDrops[pid].TotalRequiredMinutes)
        .DefaultIfEmpty(0).Max();

    public int TotalRemainingMinutes => RemainingMinutes + PreconditionDrops
        .Select(pid => Campaign.TimedDrops[pid].TotalRemainingMinutes)
        .DefaultIfEmpty(0).Max();

    public double Progress
    {
        get
        {
            if (CurrentMinutes <= 0 || RequiredMinutes <= 0)
                return 0.0;
            if (CurrentMinutes >= RequiredMinutes)
                return 1.0;
            return (double)CurrentMinutes / RequiredMinutes;
        }
    }

    public double Availability
    {
        get
        {
            DateTime now = DateTime.UtcNow;
            if (RequiredMinutes > 0 && TotalRemainingMinutes > 0 && now < EndsAt)
                return (EndsAt - now).TotalMinutes / TotalRemainingMinutes;
            return double.PositiveInfinity;
        }
    }

    private bool BaseEarnConditions()
        => PreconditionsMet
            && !IsClaimed
            && (Benefits.Count > 0 || Campaign.PreconditionsChain().Contains(Id))
            && RequiredMinutes > 0
            && ExtraCurrentMinutes < Constants.MaxExtraMinutes;

    internal bool BaseCanEarn()
        => BaseEarnConditions()
            && StartsAt <= DateTime.UtcNow && DateTime.UtcNow < EndsAt;

    internal bool CanEarnWithinDrop(DateTime stamp)
        => BaseEarnConditions()
            && EndsAt > DateTime.UtcNow
            && StartsAt < stamp;

    public bool CanEarn(Channel? channel = null, bool ignoreChannelStatus = false)
        => BaseCanEarn() && Campaign.BaseCanEarn(channel, ignoreChannelStatus);

    /// <summary>
    /// Twitch allows claiming a drop until 24 hours after the campaign has ended.
    /// </summary>
    public bool CanClaim
        => ClaimId is not null
            && !IsClaimed
            && DateTime.UtcNow < Campaign.EndsAt + TimeSpan.FromHours(24);

    public void UpdateClaim(string claimId) => ClaimId = claimId;

    public string RewardsText(string delimiter = ", ")
        => string.Join(delimiter, Benefits.Select(b => b.Name));

    // Binding helpers
    public string ProgressText => $"{CurrentMinutes}/{RequiredMinutes} min";
    public string StatusText => IsClaimed ? "Claimed ✔" : CanClaim ? "Ready to claim" : "";

    private void OnStateChanged()
    {
        OnPropertyChanged(nameof(CurrentMinutes));
        OnPropertyChanged(nameof(RemainingMinutes));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(IsClaimed));
        OnPropertyChanged(nameof(StatusText));
        Campaign.RaiseProgressChanged();
    }

    internal void UpdateRealMinutes(int delta)
    {
        if (delta == 0 || RealCurrentMinutes + delta < 0)
            return;
        RealCurrentMinutes = Math.Min(RealCurrentMinutes + delta, RequiredMinutes);
        ExtraCurrentMinutes = 0;
        OnStateChanged();
    }

    /// <summary>Returns true if the extra minutes limit has been reached.</summary>
    internal bool BumpMinutes(Channel? channel)
    {
        if (CanEarn(channel))
        {
            ExtraCurrentMinutes++;
            OnStateChanged();
            if (ExtraCurrentMinutes >= Constants.MaxExtraMinutes)
                return true;
        }
        return false;
    }

    /// <summary>Sets the drop's progress based on a newly reported watched-minutes value.</summary>
    public void UpdateMinutes(int newMinutes)
    {
        int delta = newMinutes - RealCurrentMinutes;
        if (delta == 0)
            return;
        if (RealCurrentMinutes + delta < 0)
            delta = -RealCurrentMinutes;
        else if (RealCurrentMinutes + delta > RequiredMinutes)
            delta = RequiredMinutes - RealCurrentMinutes;
        Campaign.UpdateRealMinutes(delta);
    }

    public void Display(bool countdown = true, bool subone = false)
        => _twitch.Gui.DisplayDrop(this, countdown, subone);

    public async Task<bool> ClaimAsync()
    {
        bool result = await ClaimInnerAsync().ConfigureAwait(false);
        if (result)
        {
            IsClaimed = true;
            RealCurrentMinutes = RequiredMinutes;
            ExtraCurrentMinutes = 0;
            string claimText =
                $"{Campaign.Game.Name}: {RewardsText()} " +
                $"({Campaign.ClaimedDrops}/{Campaign.TotalDrops})";
            _twitch.Gui.Print($"Claimed drop: {claimText}");
            _twitch.Gui.TrayNotify(claimText, "Drop claimed");
            _twitch.NotifyDropClaimed(this);
            _twitch.NotifyCampaignMaybeCompleted(Campaign);
        }
        else
        {
            _twitch.LogError($"Drop claim has potentially failed! Drop ID: {Id}");
        }
        OnStateChanged();
        return result;
    }

    private async Task<bool> ClaimInnerAsync()
    {
        if (IsClaimed)
            return true;
        if (!CanClaim)
            return false;
        JsonNode response;
        try
        {
            response = await _twitch.GqlRequestAsync(
                GqlQueries.ClaimDrop.WithVariables(new JsonObject
                {
                    ["input"] = new JsonObject { ["dropInstanceID"] = ClaimId }
                })).ConfigureAwait(false);
        }
        catch (GqlException)
        {
            // regardless of the error, assume the claim has potentially failed
            return false;
        }
        JsonNode? data = response["data"];
        if (data is null)
            return false;
        if (data["errors"] is JsonArray { Count: > 0 })
            return false;
        string? status = data["claimDropRewards"]?["status"]?.GetValue<string>();
        return status is "ELIGIBLE_FOR_ALL" or "DROP_INSTANCE_ALREADY_CLAIMED";
    }
}

/// <summary>A drops campaign.</summary>
public sealed partial class DropsCampaign : ObservableObject
{
    private static readonly Regex DimsPattern = new(
        @"-\d+x\d+(?=\.(?:jpg|png|gif)$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public TwitchClient Client { get; }
    public string Id { get; }
    public string Name { get; }
    public Game Game { get; }
    public bool Linked { get; }
    public string LinkUrl { get; }
    public string ImageUrl { get; }
    public DateTime StartsAt { get; }
    public DateTime EndsAt { get; }
    private readonly bool _valid;
    public IReadOnlyList<Channel> AllowedChannels { get; }
    public IReadOnlyDictionary<string, TimedDrop> TimedDrops { get; }

    /// <summary>Box art bitmap; loaded asynchronously off the UI thread (frozen image).</summary>
    [ObservableProperty]
    private ImageSource? _boxArt;

    public DropsCampaign(TwitchClient client, JsonNode data, IReadOnlyDictionary<string, DateTime> claimedBenefits)
    {
        Client = client;
        Id = data["id"]!.GetValue<string>();
        Name = data["name"]!.GetValue<string>();
        Game = new Game(data["game"]!);
        Linked = data["self"]?["isAccountConnected"]?.GetValue<bool>() ?? false;
        LinkUrl = data["accountLinkURL"]?.GetValue<string>() ?? "";
        // the campaign's image comes from the game object; strip the dimensions part
        string boxArt = data["game"]!["boxArtURL"]?.GetValue<string>() ?? "";
        ImageUrl = DimsPattern.Replace(boxArt, "");
        StartsAt = Utils.ParseTimestamp(data["startAt"]!.GetValue<string>());
        EndsAt = Utils.ParseTimestamp(data["endAt"]!.GetValue<string>());
        _valid = data["status"]?.GetValue<string>() != "EXPIRED";
        var allow = data["allow"];
        bool aclEnabled = allow?["isEnabled"]?.GetValue<bool>() ?? true;
        AllowedChannels = allow?["channels"] is JsonArray channels && aclEnabled
            ? [.. channels.Select(c => Channel.FromAcl(client, c!))]
            : [];
        TimedDrops = (data["timeBasedDrops"] as JsonArray ?? [])
            .Select(d => new TimedDrop(this, d!, claimedBenefits))
            .ToDictionary(d => d.Id);
    }

    public IEnumerable<TimedDrop> Drops => TimedDrops.Values;

    /// <summary>Sorted drop list for the inventory view.</summary>
    public IReadOnlyList<TimedDrop> DropsList
        => [.. Drops.OrderBy(d => d.RequiredMinutes)];

    public IEnumerable<DateTime> TimeTriggers
        => Drops.SelectMany(d => new[] { d.StartsAt, d.EndsAt }).Append(StartsAt).Append(EndsAt);

    public bool Active => _valid && StartsAt <= DateTime.UtcNow && DateTime.UtcNow < EndsAt;
    public bool Upcoming => _valid && DateTime.UtcNow < StartsAt;
    public bool Expired => !_valid || EndsAt <= DateTime.UtcNow;
    public int TotalDrops => TimedDrops.Count;

    public bool Eligible => HasBadgeOrEmote ? Client.Settings.EnableBadgesEmotes : Linked;

    private bool? _hasBadgeOrEmote;
    public bool HasBadgeOrEmote
        => _hasBadgeOrEmote ??= Drops.Any(d => d.Benefits.Any(b => b.IsBadgeOrEmote));

    public bool Finished => Drops.All(d => d.IsClaimed || d.RequiredMinutes <= 0);
    public int ClaimedDrops => Drops.Count(d => d.IsClaimed);
    public int RemainingDrops => Drops.Count(d => !d.IsClaimed);
    public int RequiredMinutes => Drops.Max(d => d.TotalRequiredMinutes);
    public int RemainingMinutes => Drops.Max(d => d.TotalRemainingMinutes);
    public double Progress => Drops.Sum(d => d.Progress) / TotalDrops;
    public double Availability => Drops.Min(d => d.Availability);

    public TimedDrop? FirstDrop => Drops
        .Where(d => d.CanEarn())
        .OrderBy(d => d.RemainingMinutes)
        .FirstOrDefault();

    // Binding helpers for the inventory view
    public string StatusText => Upcoming ? "Upcoming" : Expired ? "Expired" : "Active";
    public string LinkedText => Linked ? "Linked ✔" : "Not linked";
    public string TimeText => Upcoming
        ? $"Starts {StartsAt.ToLocalTime():g}"
        : $"Ends {EndsAt.ToLocalTime():g}";
    public string CountText => $"{ClaimedDrops}/{TotalDrops} claimed";

    internal void RaiseProgressChanged()
    {
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ClaimedDrops));
        OnPropertyChanged(nameof(RemainingDrops));
        OnPropertyChanged(nameof(CountText));
    }

    internal void UpdateRealMinutes(int delta)
    {
        foreach (TimedDrop drop in Drops)
            drop.UpdateRealMinutes(delta);
        FirstDrop?.Display();
    }

    internal bool BaseCanEarn(Channel? channel = null, bool ignoreChannelStatus = false)
        => Eligible
            && Active
            && (channel is null || (
                (AllowedChannels.Count == 0 || AllowedChannels.Contains(channel))
                && (ignoreChannelStatus
                    || (channel.Game is not null && channel.Game.Equals(Game))
                    || Game.IsSpecial)));

    public TimedDrop? GetDrop(string dropId)
        => TimedDrops.GetValueOrDefault(dropId);

    /// <summary>IDs of drops that participate in an unclaimed preconditions chain.</summary>
    public IReadOnlySet<string> PreconditionsChain()
        => Drops.Where(d => !d.IsClaimed).SelectMany(d => d.PreconditionDrops).ToHashSet();

    public bool CanEarn(Channel? channel = null, bool ignoreChannelStatus = false)
        => BaseCanEarn(channel, ignoreChannelStatus) && Drops.Any(d => d.BaseCanEarn());

    /// <summary>Same as CanEarn, but ignores the channel and checks a future timestamp.</summary>
    public bool CanEarnWithin(DateTime stamp)
        => Eligible
            && _valid
            && EndsAt > DateTime.UtcNow
            && StartsAt < stamp
            && Drops.Any(d => d.CanEarnWithinDrop(stamp));

    /// <summary>
    /// Bumps the "pretend mining" extra minutes on all earnable drops.
    /// Used when Twitch stops reporting drop progress.
    /// </summary>
    public void BumpMinutes(Channel channel)
    {
        // build the full list first, so ALL drops are bumped before any short-circuit
        var bumped = Drops.Select(d => d.BumpMinutes(channel)).ToList();
        if (bumped.Any(b => b))
        {
            Client.LogWarning(
                $"At least one of the drops in campaign \"{Name} ({Game.Name})\" " +
                "has reached the maximum extra minutes limit!");
            Client.ChangeState(MinerState.ChannelSwitch);
        }
        FirstDrop?.Display();
    }

    public override string ToString() => $"Campaign({Game}, {Name}, {ClaimedDrops}/{TotalDrops})";
}
