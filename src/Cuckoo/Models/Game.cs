using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cuckoo.Models;

public sealed partial class Game : IEquatable<Game>
{
    private static readonly HashSet<long> SpecialGameIds = [509663, 509672];

    public long Id { get; }
    public string Name { get; }

    private string? _slug;

    public Game(JsonNode data)
    {
        Id = long.Parse(data["id"]!.GetValue<string>());
        Name = data["displayName"]?.GetValue<string>() ?? data["name"]!.GetValue<string>();
        _slug = data["slug"]?.GetValue<string>();
    }

    /// <summary>Game slug usable with the GQL directory API, derived from the name if not provided.</summary>
    public string Slug => _slug ??= Slugify(Name);

    private static string Slugify(string name)
    {
        string slug = ApostropheRegex().Replace(name.ToLowerInvariant(), "");
        slug = NonWordRegex().Replace(slug, "-");
        slug = DashCollapseRegex().Replace(slug.Trim('-'), "-");
        return slug;
    }

    public bool IsSpecial => SpecialGameIds.Contains(Id);

    public override string ToString() => Name;
    public bool Equals(Game? other) => other is not null && Id == other.Id;
    public override bool Equals(object? obj) => obj is Game other && Equals(other);
    public override int GetHashCode() => Id.GetHashCode();

    [GeneratedRegex("'")]
    private static partial Regex ApostropheRegex();
    [GeneratedRegex(@"\W+")]
    private static partial Regex NonWordRegex();
    [GeneratedRegex("-{2,}")]
    private static partial Regex DashCollapseRegex();
}
