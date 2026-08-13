using Cuckoo.Models;
using Cuckoo.Services;

namespace Cuckoo.Core;

/// <summary>
/// Implements the mining order modes: which campaigns are considered for mining,
/// and in what order their games enter the wanted-games list.
/// </summary>
public static class MiningOrder
{
    /// <summary>Whether a campaign's game is allowed to be mined at all in the current mode.</summary>
    public static bool Includes(DropsCampaign campaign, Settings settings)
        => settings.MiningMode switch
        {
            MiningMode.PriorityOnly
                => settings.HasPriority(campaign.Game.Name),
            MiningMode.PriorityThenLinked or MiningMode.PriorityScored
                => settings.HasPriority(campaign.Game.Name) || campaign.Linked,
            MiningMode.Custom
                => settings.HasPriority(campaign.Game.Name) || settings.CustomIncludeNonPriority,
            // EndingSoonest / LowAvailabilityFirst consider every non-excluded game
            _ => true,
        };

    /// <summary>Orders the inventory by mining preference (most preferred first).</summary>
    public static List<DropsCampaign> Sort(IReadOnlyList<DropsCampaign> inventory, Settings settings)
        => settings.MiningMode switch
        {
            MiningMode.EndingSoonest =>
            [
                .. inventory
                    .OrderBy(c => settings.PriorityIndex(c.Game.Name))
                    .ThenBy(c => c.EndsAt),
            ],
            MiningMode.LowAvailabilityFirst =>
            [
                .. inventory
                    .OrderBy(c => settings.PriorityIndex(c.Game.Name))
                    .ThenBy(c => c.Availability),
            ],
            MiningMode.PriorityThenLinked => SortPriorityThenLinked(inventory, settings),
            MiningMode.PriorityScored => SortPriorityScored(inventory, settings),
            MiningMode.Custom => SortCustom(inventory, settings),
            _ =>
            [
                .. inventory
                    .OrderBy(c => settings.PriorityIndex(c.Game.Name)),
            ],
        };

    /// <summary>
    /// Priority list first (list order; ties from pattern entries break by end date),
    /// then linked games ordered by which campaign ends soonest.
    /// </summary>
    private static List<DropsCampaign> SortPriorityThenLinked(
        IReadOnlyList<DropsCampaign> inventory, Settings settings)
    {
        var priority = inventory
            .Where(c => settings.HasPriority(c.Game.Name))
            .OrderBy(c => settings.PriorityIndex(c.Game.Name))
            .ThenBy(c => c.EndsAt);
        var linked = inventory
            .Where(c => !settings.HasPriority(c.Game.Name) && c.Linked)
            .OrderBy(c => c.EndsAt);
        return [.. priority, .. linked];
    }

    /// <summary>
    /// Priority games get a combined score: up to 100 points for the list position
    /// (first entry scores highest) plus up to 100 points for ending soonest among
    /// the priority games. Linked non-priority games follow, ordered by end date.
    /// </summary>
    private static List<DropsCampaign> SortPriorityScored(
        IReadOnlyList<DropsCampaign> inventory, Settings settings)
    {
        var priorityCampaigns = inventory
            .Where(c => settings.HasPriority(c.Game.Name))
            .ToList();
        int entryCount = Math.Max(1, settings.Priority.Count);
        Dictionary<DropsCampaign, double> endScores = RankScores(priorityCampaigns, c => c.EndsAt.Ticks);
        var scored = priorityCampaigns
            .OrderByDescending(c =>
            {
                double positionScore =
                    100.0 * (entryCount - settings.PriorityIndex(c.Game.Name)) / entryCount;
                return positionScore + 100.0 * endScores[c];
            })
            .ThenBy(c => c.EndsAt);
        var linked = inventory
            .Where(c => !settings.HasPriority(c.Game.Name) && c.Linked)
            .OrderBy(c => c.EndsAt);
        return [.. scored, .. linked];
    }

    /// <summary>
    /// Fully user-defined scoring: each factor is normalized to 0..1 and multiplied
    /// by its configured weight; the summed score decides the order.
    /// </summary>
    private static List<DropsCampaign> SortCustom(
        IReadOnlyList<DropsCampaign> inventory, Settings settings)
    {
        var candidates = inventory.Where(c => Includes(c, settings)).ToList();
        int entryCount = Math.Max(1, settings.Priority.Count);
        Dictionary<DropsCampaign, double> endFactors = RankScores(candidates, c => c.EndsAt.Ticks);
        Dictionary<DropsCampaign, double> availabilityFactors = RankScores(candidates, c => c.Availability);
        return [.. candidates
            .OrderByDescending(c =>
            {
                int priorityIndex = settings.PriorityIndex(c.Game.Name);
                double positionFactor = priorityIndex == int.MaxValue
                    ? 0.0
                    : (double)(entryCount - priorityIndex) / entryCount;
                return settings.CustomWeightPriority * positionFactor
                    + settings.CustomWeightEndingSoon * endFactors[c]
                    + settings.CustomWeightLowAvailability * availabilityFactors[c];
            })
            .ThenBy(c => c.EndsAt)];
    }

    /// <summary>
    /// Rank-normalizes campaigns by a key: the campaign with the lowest key gets 1.0,
    /// the highest gets close to 0. Robust against outliers and infinite values.
    /// </summary>
    private static Dictionary<DropsCampaign, double> RankScores<TKey>(
        IReadOnlyList<DropsCampaign> campaigns, Func<DropsCampaign, TKey> key)
    {
        int count = Math.Max(1, campaigns.Count);
        var scores = new Dictionary<DropsCampaign, double>(campaigns.Count);
        int rank = 0;
        foreach (DropsCampaign campaign in campaigns.OrderBy(key))
            scores[campaign] = (double)(count - rank++) / count;
        return scores;
    }
}
