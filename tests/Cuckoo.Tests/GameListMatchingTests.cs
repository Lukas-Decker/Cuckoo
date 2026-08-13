using Cuckoo.Core;

namespace Cuckoo.Tests;

/// <summary>
/// The priority and exclusion lists accept both literal game names and glob patterns.
/// Getting this wrong either silently mines nothing or mines everything, and neither
/// failure is obvious from the UI, so it is worth pinning down.
/// </summary>
public class GameListMatchingTests
{
    [Theory]
    [InlineData("Rust", "Rust", true)]
    [InlineData("Rust", "rust", false)]          // matching is case-sensitive
    [InlineData("Rust", "Rust 2", false)]
    [InlineData("Dead by Daylight", "Dead by Daylight", true)]
    public void LiteralEntriesMatchExactly(string entry, string game, bool expected)
        => Assert.Equal(expected, Utils.MatchEntry(entry, game));

    [Theory]
    [InlineData("EA Sports FC *", "EA Sports FC 25", true)]
    [InlineData("EA Sports FC *", "EA Sports FC 26", true)]
    [InlineData("EA Sports FC *", "EA Sports FC", false)]   // '*' needs something after it
    [InlineData("EA Sports FC*", "EA Sports FC", true)]
    [InlineData("*Souls*", "Dark Souls III", true)]
    [InlineData("*Souls*", "Elden Ring", false)]
    public void StarMatchesAnyRun(string entry, string game, bool expected)
        => Assert.Equal(expected, Utils.MatchEntry(entry, game));

    [Theory]
    [InlineData("Fallout ?", "Fallout 4", true)]
    [InlineData("Fallout ?", "Fallout 76", false)]          // '?' is exactly one character
    [InlineData("Fallout ??", "Fallout 76", true)]
    public void QuestionMarkMatchesOneCharacter(string entry, string game, bool expected)
        => Assert.Equal(expected, Utils.MatchEntry(entry, game));

    [Theory]
    [InlineData("Battlefield [12]", "Battlefield 1", true)]
    [InlineData("Battlefield [12]", "Battlefield 2", true)]
    [InlineData("Battlefield [12]", "Battlefield 3", false)]
    [InlineData("Battlefield [!12]", "Battlefield 3", true)]
    [InlineData("Battlefield [!12]", "Battlefield 1", false)]
    public void CharacterClassesWork(string entry, string game, bool expected)
        => Assert.Equal(expected, Utils.MatchEntry(entry, game));

    [Fact]
    public void RegexMetacharactersInLiteralsAreNotTreatedAsRegex()
    {
        // "Tom Clancy's Rainbow Six Siege" and friends contain characters that would
        // blow up or silently over-match if the entry went into Regex unescaped.
        Assert.True(Utils.MatchEntry("S.T.A.L.K.E.R. 2", "S.T.A.L.K.E.R. 2"));
        Assert.False(Utils.MatchEntry("S.T.A.L.K.E.R. 2", "SxTxAxLxKxExRx 2"));
        Assert.True(Utils.MatchEntry("S.T.A.L.K.E.R.*", "S.T.A.L.K.E.R. 2"));
        Assert.False(Utils.MatchEntry("S.T.A.L.K.E.R.*", "SxTxAxLxKxExRx 2"));
    }

    [Fact]
    public void UnclosedBracketIsTreatedAsALiteralBracket()
    {
        // a typo in the game list should not throw, it should just not match anything else
        Assert.True(Utils.MatchEntry("Half-Life [2", "Half-Life [2"));
        Assert.False(Utils.MatchEntry("Half-Life [2", "Half-Life 2"));
    }

    [Theory]
    [InlineData("Rust", false)]
    [InlineData("EA Sports FC *", true)]
    [InlineData("Fallout ?", true)]
    [InlineData("Battlefield [12]", true)]
    public void PatternDetectionDrivesTheMatchingMode(string entry, bool expected)
        => Assert.Equal(expected, Utils.IsPatternEntry(entry));
}
