namespace Jellyfin.Plugin.AnimeSeasonCollections.Models;

/// <summary>
/// A calendar anime season bucket.
/// </summary>
public readonly record struct SeasonBucket(int Year, AnimeQuarter Quarter)
{
    public string DisplayName => $"{Year} {Quarter}";

    public string ProviderValue => $"{Year:D4}-{Quarter.ToString().ToLowerInvariant()}";

    public DateTime CanonicalDateUtc => Quarter switch
    {
        AnimeQuarter.Winter => new DateTime(Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        AnimeQuarter.Spring => new DateTime(Year, 4, 1, 0, 0, 0, DateTimeKind.Utc),
        AnimeQuarter.Summer => new DateTime(Year, 7, 1, 0, 0, 0, DateTimeKind.Utc),
        AnimeQuarter.Autumn => new DateTime(Year, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        _ => throw new ArgumentOutOfRangeException(nameof(Quarter))
    };

    public string ForcedSortName => $"{Year:D4}-{((int)Quarter):D2}";

    public static SeasonBucket FromDate(DateTime releaseDate)
    {
        var utc = releaseDate.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(releaseDate, DateTimeKind.Utc)
            : releaseDate.ToUniversalTime();

        // Anime-season assignment is based on the likely START of the cour, not
        // on the conventional meteorological/calendar quarter boundaries:
        //   Winter: December-February
        //   Spring: March-May
        //   Summer: June-August
        //   Autumn: September-November
        // December belongs to the following year's Winter label, so a premiere
        // on 2025-12-20 is assigned to "2026 Winter".
        if (utc.Month == 12)
        {
            return new SeasonBucket(utc.Year + 1, AnimeQuarter.Winter);
        }

        var quarter = utc.Month switch
        {
            <= 2 => AnimeQuarter.Winter,
            <= 5 => AnimeQuarter.Spring,
            <= 8 => AnimeQuarter.Summer,
            _ => AnimeQuarter.Autumn
        };

        return new SeasonBucket(utc.Year, quarter);
    }
}

/// <summary>
/// Calendar order is deliberately 1..4 so ForcedSortName sorts Winter, Spring, Summer, Autumn.
/// </summary>
public enum AnimeQuarter
{
    Winter = 1,
    Spring = 2,
    Summer = 3,
    Autumn = 4
}
