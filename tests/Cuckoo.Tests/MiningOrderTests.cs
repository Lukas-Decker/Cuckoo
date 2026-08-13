using Cuckoo.Core;
using Cuckoo.Models;
using Cuckoo.Services;

namespace Cuckoo.Tests;

/// <summary>
/// Covers the two questions <see cref="MiningOrder"/> answers: which campaigns are
/// eligible at all in the current mode, and in what order they should be mined.
/// </summary>
public class MiningOrderTests
{
    private static List<string> Games(IEnumerable<DropsCampaign> campaigns)
        => [.. campaigns.Select(c => c.Game.Name)];

    // ------------------------------------------------------------------ eligibility

    [Fact]
    public void PriorityOnlyIgnoresEverythingOffTheList()
    {
        var settings = new Settings { MiningMode = MiningMode.PriorityOnly, Priority = ["Rust"] };
        var client = TestFactory.Client(settings);
        DropsCampaign onList = TestFactory.Campaign(client, "Rust", endsInMinutes: 1000);
        DropsCampaign offList = TestFactory.Campaign(client, "Elden Ring", endsInMinutes: 100);

        Assert.True(MiningOrder.Includes(onList, settings));
        Assert.False(MiningOrder.Includes(offList, settings));
    }

    [Fact]
    public void PriorityThenLinkedAlsoAcceptsLinkedGames()
    {
        var settings = new Settings { MiningMode = MiningMode.PriorityThenLinked, Priority = ["Rust"] };
        var client = TestFactory.Client(settings);

        Assert.True(MiningOrder.Includes(
            TestFactory.Campaign(client, "Elden Ring", 100, linked: true), settings));
        Assert.False(MiningOrder.Includes(
            TestFactory.Campaign(client, "Elden Ring", 100, linked: false), settings));
    }

    [Fact]
    public void CustomModeRespectsTheNonPriorityToggle()
    {
        var settings = new Settings
        {
            MiningMode = MiningMode.Custom,
            Priority = ["Rust"],
            CustomIncludeNonPriority = false,
        };
        var client = TestFactory.Client(settings);
        DropsCampaign other = TestFactory.Campaign(client, "Elden Ring", 100);

        Assert.False(MiningOrder.Includes(other, settings));

        settings.CustomIncludeNonPriority = true;
        Assert.True(MiningOrder.Includes(other, settings));
    }

    [Fact]
    public void EndingSoonestConsidersEveryGame()
    {
        var settings = new Settings { MiningMode = MiningMode.EndingSoonest };
        var client = TestFactory.Client(settings);

        Assert.True(MiningOrder.Includes(TestFactory.Campaign(client, "Anything", 100), settings));
    }

    // ------------------------------------------------------------------ ordering

    [Fact]
    public void PriorityOnlyKeepsTheUsersListOrder()
    {
        var settings = new Settings
        {
            MiningMode = MiningMode.PriorityOnly,
            Priority = ["Rust", "Elden Ring", "Valheim"],
        };
        var client = TestFactory.Client(settings);
        // deliberately fed in the wrong order, and with end dates that would flip it
        List<DropsCampaign> inventory =
        [
            TestFactory.Campaign(client, "Valheim", 10),
            TestFactory.Campaign(client, "Rust", 10_000),
            TestFactory.Campaign(client, "Elden Ring", 100),
        ];

        Assert.Equal(["Rust", "Elden Ring", "Valheim"], Games(MiningOrder.Sort(inventory, settings)));
    }

    [Fact]
    public void EndingSoonestPutsTheClosestDeadlineFirst()
    {
        var settings = new Settings { MiningMode = MiningMode.EndingSoonest };
        var client = TestFactory.Client(settings);
        List<DropsCampaign> inventory =
        [
            TestFactory.Campaign(client, "Late", 10_000),
            TestFactory.Campaign(client, "Soon", 60),
            TestFactory.Campaign(client, "Middle", 1_000),
        ];

        Assert.Equal(["Soon", "Middle", "Late"], Games(MiningOrder.Sort(inventory, settings)));
    }

    [Fact]
    public void PriorityListStillWinsInsideEndingSoonest()
    {
        // the priority list is not ignored by the date-driven modes, it just becomes
        // the primary key with the date as the tiebreaker
        var settings = new Settings { MiningMode = MiningMode.EndingSoonest, Priority = ["Late"] };
        var client = TestFactory.Client(settings);
        List<DropsCampaign> inventory =
        [
            TestFactory.Campaign(client, "Soon", 60),
            TestFactory.Campaign(client, "Late", 10_000),
        ];

        Assert.Equal(["Late", "Soon"], Games(MiningOrder.Sort(inventory, settings)));
    }

    [Fact]
    public void LowAvailabilityFirstPrefersTheCampaignHardestToFinish()
    {
        var settings = new Settings { MiningMode = MiningMode.LowAvailabilityFirst };
        var client = TestFactory.Client(settings);
        // availability is roughly (time left) / (minutes still needed)
        List<DropsCampaign> inventory =
        [
            TestFactory.Campaign(client, "Comfortable", endsInMinutes: 10_000, requiredMinutes: 60),
            TestFactory.Campaign(client, "Tight", endsInMinutes: 120, requiredMinutes: 60),
        ];

        Assert.Equal(["Tight", "Comfortable"], Games(MiningOrder.Sort(inventory, settings)));
    }

    [Fact]
    public void PriorityThenLinkedAppendsLinkedGamesAfterTheList()
    {
        var settings = new Settings { MiningMode = MiningMode.PriorityThenLinked, Priority = ["Rust"] };
        var client = TestFactory.Client(settings);
        List<DropsCampaign> inventory =
        [
            TestFactory.Campaign(client, "LinkedLate", 10_000, linked: true),
            TestFactory.Campaign(client, "LinkedSoon", 60, linked: true),
            TestFactory.Campaign(client, "Rust", 50_000),
            TestFactory.Campaign(client, "Unlinked", 10, linked: false),
        ];

        // Rust first because it is on the list despite ending last, then linked games
        // by end date; the unlinked game is dropped entirely.
        Assert.Equal(["Rust", "LinkedSoon", "LinkedLate"], Games(MiningOrder.Sort(inventory, settings)));
    }

    [Fact]
    public void PriorityScoredLetsAnEndDateOutweighOneListPosition()
    {
        // Three entries, so each list position is worth ~33 points, while the end-date
        // component spans the full 100. The second entry ending far sooner should overtake
        // the first, but a game three positions down should not.
        var settings = new Settings
        {
            MiningMode = MiningMode.PriorityScored,
            Priority = ["First", "Second", "Third"],
        };
        var client = TestFactory.Client(settings);
        List<DropsCampaign> inventory =
        [
            TestFactory.Campaign(client, "First", 10_000),
            TestFactory.Campaign(client, "Second", 60),
            TestFactory.Campaign(client, "Third", 5_000),
        ];

        List<string> order = Games(MiningOrder.Sort(inventory, settings));
        Assert.Equal("Second", order[0]);
        Assert.Equal(["First", "Third"], order[1..]);
    }

    [Fact]
    public void CustomModeWithOnlyThePriorityWeightBehavesLikePriorityOnly()
    {
        var settings = new Settings
        {
            MiningMode = MiningMode.Custom,
            Priority = ["Rust", "Valheim"],
            CustomWeightPriority = 100,
            CustomWeightEndingSoon = 0,
            CustomWeightLowAvailability = 0,
            CustomIncludeNonPriority = false,
        };
        var client = TestFactory.Client(settings);
        List<DropsCampaign> inventory =
        [
            TestFactory.Campaign(client, "Valheim", 60),
            TestFactory.Campaign(client, "Rust", 10_000),
        ];

        Assert.Equal(["Rust", "Valheim"], Games(MiningOrder.Sort(inventory, settings)));
    }

    [Fact]
    public void CustomModeWithOnlyTheEndDateWeightIgnoresTheList()
    {
        var settings = new Settings
        {
            MiningMode = MiningMode.Custom,
            Priority = ["Rust"],
            CustomWeightPriority = 0,
            CustomWeightEndingSoon = 100,
            CustomWeightLowAvailability = 0,
            CustomIncludeNonPriority = true,
        };
        var client = TestFactory.Client(settings);
        List<DropsCampaign> inventory =
        [
            TestFactory.Campaign(client, "Rust", 10_000),
            TestFactory.Campaign(client, "Valheim", 60),
        ];

        Assert.Equal(["Valheim", "Rust"], Games(MiningOrder.Sort(inventory, settings)));
    }

    [Fact]
    public void CustomScoringIsRankBasedSoOneOutlierCannotDominate()
    {
        // "Rust" is first on the list but ends absurdly far out. Because the end-date
        // factor is rank-normalised rather than scaled by the raw gap, that outlier
        // cannot swamp the priority weight.
        var settings = new Settings
        {
            MiningMode = MiningMode.Custom,
            Priority = ["Rust"],
            CustomWeightPriority = 100,
            CustomWeightEndingSoon = 50,
            CustomWeightLowAvailability = 0,
            CustomIncludeNonPriority = true,
        };
        var client = TestFactory.Client(settings);
        List<DropsCampaign> inventory =
        [
            TestFactory.Campaign(client, "Rust", 5_000_000),
            TestFactory.Campaign(client, "Valheim", 60),
        ];

        Assert.Equal(["Rust", "Valheim"], Games(MiningOrder.Sort(inventory, settings)));
    }

    [Fact]
    public void GlobPriorityEntriesOrderByTheirListPosition()
    {
        var settings = new Settings
        {
            MiningMode = MiningMode.PriorityOnly,
            Priority = ["EA Sports FC *", "Rust"],
        };
        var client = TestFactory.Client(settings);
        List<DropsCampaign> inventory =
        [
            TestFactory.Campaign(client, "Rust", 60),
            TestFactory.Campaign(client, "EA Sports FC 26", 10_000),
        ];

        Assert.Equal(["EA Sports FC 26", "Rust"], Games(MiningOrder.Sort(inventory, settings)));
    }

    [Fact]
    public void SortingAnEmptyInventoryIsHarmless()
    {
        var settings = new Settings { MiningMode = MiningMode.Custom };
        Assert.Empty(MiningOrder.Sort([], settings));
    }
}
