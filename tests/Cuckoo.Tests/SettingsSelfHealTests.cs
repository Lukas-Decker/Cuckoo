using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuckoo.Core;
using Cuckoo.Models;
using Cuckoo.Services;

namespace Cuckoo.Tests;

/// <summary>
/// settings.json is written by a long-running background app that can be killed at any
/// moment, so the load path has to survive a truncated, malformed or simply outdated
/// file without ever losing the user's game lists. These tests drive that path.
///
/// Settings resolves its own path from the executable directory, so the tests operate on
/// the real file in the test output folder. They live in one class on purpose: xUnit runs
/// tests within a class sequentially, which keeps them off each other's toes.
/// </summary>
public class SettingsSelfHealTests : IDisposable
{
    private static readonly string Path = Constants.SettingsPath;
    private static readonly string BakPath = Path + ".bak";
    private static readonly string CorruptPath = Path + ".corrupt";

    public SettingsSelfHealTests() => Cleanup();
    public void Dispose() => Cleanup();

    private static void Cleanup()
    {
        foreach (string file in new[] { Path, BakPath, CorruptPath, Path + ".new" })
            File.Delete(file);
    }

    private static void Write(string json) => File.WriteAllText(Path, json);

    private static string Json(Action<JsonObject> configure)
    {
        var obj = new JsonObject
        {
            ["config_version"] = Settings.CurrentConfigVersion,
            ["priority"] = new JsonArray("Rust", "Valheim"),
            ["exclude"] = new JsonArray("Just Chatting"),
            ["priority_mode"] = "ending_soonest",
            ["connection_quality"] = 3,
        };
        configure(obj);
        return obj.ToJsonString();
    }

    // ------------------------------------------------------------------ happy path

    [Fact]
    public void MissingFileYieldsStampedDefaults()
    {
        Settings settings = Settings.Load();

        Assert.Equal(Settings.CurrentConfigVersion, settings.ConfigVersion);
        Assert.Equal(MiningMode.PriorityOnly, settings.MiningMode);
        Assert.Empty(settings.Priority);
    }

    [Fact]
    public void SavedSettingsRoundTrip()
    {
        var original = new Settings
        {
            Priority = ["Rust", "EA Sports FC *"],
            Exclude = ["Just Chatting"],
            MiningMode = MiningMode.PriorityScored,
            WatchMethod = WatchMethod.Gql,
            ConnectionQuality = 4,
            PreferOwnLanguage = true,
            PreferredLanguage = "de",
        };
        original.Save(force: true);

        Settings reloaded = Settings.Load();

        Assert.Equal(original.Priority, reloaded.Priority);
        Assert.Equal(original.Exclude, reloaded.Exclude);
        Assert.Equal(MiningMode.PriorityScored, reloaded.MiningMode);
        Assert.Equal(WatchMethod.Gql, reloaded.WatchMethod);
        Assert.Equal(4, reloaded.ConnectionQuality);
        Assert.Equal("de", reloaded.EffectiveLanguage);
    }

    [Fact]
    public void SavingAlsoLeavesALastKnownGoodCopy()
    {
        new Settings { Priority = ["Rust"] }.Save(force: true);

        Assert.True(File.Exists(BakPath));
        Assert.Equal(File.ReadAllText(Path), File.ReadAllText(BakPath));
    }

    // ------------------------------------------------------------------ validation

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 6)]
    [InlineData(3, 3)]
    public void ConnectionQualityIsClamped(int stored, int expected)
    {
        Write(Json(o => o["connection_quality"] = stored));

        Assert.Equal(expected, Settings.Load().ConnectionQuality);
    }

    [Fact]
    public void CustomWeightsAreClamped()
    {
        Write(Json(o =>
        {
            o["custom_weight_priority"] = 5000;
            o["custom_weight_ending_soon"] = -20;
        }));

        Settings settings = Settings.Load();

        Assert.Equal(200, settings.CustomWeightPriority);
        Assert.Equal(0, settings.CustomWeightEndingSoon);
    }

    [Fact]
    public void BlankAndDuplicateGameEntriesAreDropped()
    {
        Write(Json(o => o["priority"] = new JsonArray("Rust", "", "Rust", "   ", "Valheim")));

        Assert.Equal(["Rust", "Valheim"], Settings.Load().Priority);
    }

    [Fact]
    public void AnUnknownEnumValueFallsBackToItsDefault()
    {
        // e.g. a mode that existed in a newer build, or a hand-edited typo
        Write(Json(o => o["priority_mode"] = "some_mode_from_the_future"));

        Assert.Equal(MiningMode.PriorityOnly, Settings.Load().MiningMode);
    }

    // ------------------------------------------------------------------ migration

    [Fact]
    public void AFileWithoutAVersionIsTreatedAsV1AndMigrated()
    {
        Write(Json(o => o.Remove("config_version")));

        Settings settings = Settings.Load();

        Assert.Equal(Settings.CurrentConfigVersion, settings.ConfigVersion);
        Assert.Equal(["Rust", "Valheim"], settings.Priority);
    }

    [Fact]
    public void AFileFromANewerBuildKeepsWhatThisBuildUnderstands()
    {
        Write(Json(o =>
        {
            o["config_version"] = Settings.CurrentConfigVersion + 5;
            o["some_future_setting"] = "whatever";
        }));

        Settings settings = Settings.Load();

        Assert.Equal(["Rust", "Valheim"], settings.Priority);
        Assert.Equal(MiningMode.EndingSoonest, settings.MiningMode);
    }

    // ------------------------------------------------------------------ self-heal

    [Fact]
    public void AWrongTypedFieldIsResetWhileTheRestSurvives()
    {
        // valid JSON, but connection_quality is a string: deserialization fails outright,
        // so the field-by-field salvage has to rescue the game lists.
        Write(Json(o => o["connection_quality"] = "not a number"));

        Settings settings = Settings.Load();

        Assert.Equal(["Rust", "Valheim"], settings.Priority);
        Assert.Equal(["Just Chatting"], settings.Exclude);
        Assert.Equal(MiningMode.EndingSoonest, settings.MiningMode);
        Assert.Equal(1, settings.ConnectionQuality); // back to the default
        Assert.True(settings.Altered);               // and queued to be rewritten
    }

    [Fact]
    public void ADamagedFileIsKeptAsideForInspection()
    {
        Write(Json(o => o["connection_quality"] = "not a number"));

        Settings.Load();

        Assert.True(File.Exists(CorruptPath));
    }

    [Fact]
    public void ATruncatedFileFallsBackToTheBackup()
    {
        // what a crash mid-write used to look like before the write-then-rename
        new Settings { Priority = ["Rust", "Valheim"] }.Save(force: true);
        Write("{\"priority\": [\"Rust\", \"Valhe");

        Settings settings = Settings.Load();

        Assert.Equal(["Rust", "Valheim"], settings.Priority);
    }

    [Fact]
    public void GarbageWithNoBackupStillStartsTheApp()
    {
        Write("this is not json at all");

        Settings settings = Settings.Load();

        Assert.Empty(settings.Priority);
        Assert.Equal(Settings.CurrentConfigVersion, settings.ConfigVersion);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreTolerated()
    {
        // hand-edited config files are a normal way to use this app
        Write("""
        {
            // mine these first
            "priority": ["Rust", "Valheim"],
            "connection_quality": 2,
        }
        """);

        Settings settings = Settings.Load();

        Assert.Equal(["Rust", "Valheim"], settings.Priority);
        Assert.Equal(2, settings.ConnectionQuality);
    }

    [Fact]
    public void SavingIsSkippedUntilSomethingActuallyChanges()
    {
        var settings = new Settings();
        settings.Save();
        Assert.False(File.Exists(Path));

        settings.Alter();
        settings.Save();
        Assert.True(File.Exists(Path));
    }

    // ------------------------------------------------------------------ list matching

    [Fact]
    public void PriorityIndexReflectsListOrderIncludingPatterns()
    {
        var settings = new Settings { Priority = ["Rust", "EA Sports FC *"] };

        Assert.Equal(0, settings.PriorityIndex("Rust"));
        Assert.Equal(1, settings.PriorityIndex("EA Sports FC 26"));
        Assert.Equal(int.MaxValue, settings.PriorityIndex("Elden Ring"));
        Assert.True(settings.HasPriority("EA Sports FC 26"));
        Assert.False(settings.HasPriority("Elden Ring"));
    }

    [Fact]
    public void ExclusionsMatchPatternsToo()
    {
        var settings = new Settings { Exclude = ["Just Chatting", "*Souls*"] };

        Assert.True(settings.IsExcluded("Just Chatting"));
        Assert.True(settings.IsExcluded("Dark Souls III"));
        Assert.False(settings.IsExcluded("Rust"));
    }

    [Fact]
    public void TheWrittenFileIsValidJson()
    {
        new Settings { Priority = ["Rust"], MiningMode = MiningMode.Custom }.Save(force: true);

        JsonNode? parsed = JsonNode.Parse(File.ReadAllText(Path));

        Assert.NotNull(parsed);
        Assert.Equal("custom", parsed!["priority_mode"]!.GetValue<string>());
    }
}
