using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AnimeSeasonCollections.Models;
using Jellyfin.Plugin.AnimeSeasonCollections.Services;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeSeasonCollections.Tasks;

/// <summary>
/// Scans Anime series and groups their non-special seasons into calendar-season collections.
/// </summary>
public sealed class RefreshAnimeSeasonCollectionsTask : IScheduledTask
{
    private const string OwnershipProviderKey = "AnimeSeasonCollections";
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ILogger<RefreshAnimeSeasonCollectionsTask> _logger;
    private readonly ArtworkService _artworkService;

    public RefreshAnimeSeasonCollectionsTask(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        ILogger<RefreshAnimeSeasonCollectionsTask> logger,
        ILogger<ArtworkService> artworkLogger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _logger = logger;
        _artworkService = new ArtworkService(artworkLogger);
    }

    public string Name => "Refresh Anime Season Collections";

    public string Description => "Creates and updates YYYY Winter/Spring/Summer/Autumn collections from non-special seasons of shows whose Genres contain Anime or Animation.";

    public string Category => "Library";

    public string Key => "AnimeSeasonCollections.Refresh";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);

        var config = Plugin.Instance?.Configuration;
        var includedLibraryIds = ParseLibraryIds(config?.IncludedLibraryIds);
        var excludedLibraryIds = ParseLibraryIds(config?.ExcludedLibraryIds);
        var includeParentSeriesForClientCompatibility =
            config?.IncludeParentSeriesForClientCompatibility ?? true;

        var seriesQuery = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            Recursive = true
        };

        if (includedLibraryIds.Length > 0)
        {
            seriesQuery.AncestorIds = includedLibraryIds;
        }

        var excludedSeriesIds = excludedLibraryIds.Length == 0
            ? new HashSet<Guid>()
            : _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Series],
                Recursive = true,
                AncestorIds = excludedLibraryIds
            }).Select(item => item.Id).ToHashSet();

        var series = _libraryManager.GetItemList(seriesQuery)
            .OfType<Series>()
            .Where(IsAnimeSeries)
            .Where(show => !excludedSeriesIds.Contains(show.Id))
            .ToArray();

        _logger.LogInformation(
            "Anime Season Collections scan found {Count} matching Anime/Animation series after library filtering. Included libraries: {IncludedCount}; excluded libraries: {ExcludedCount}",
            series.Length,
            includedLibraryIds.Length,
            excludedLibraryIds.Length);

        var buckets = new Dictionary<SeasonBucket, List<SeasonBucketEntry>>();
        var examined = 0;

        foreach (var show in series)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var seasons = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = show.Id,
                Recursive = false,
                IncludeItemTypes = [BaseItemKind.Season],
                IsSpecialSeason = false
            }).OfType<Season>();

            foreach (var season in seasons)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (season.IndexNumber == 0
                    || string.Equals(season.Name, "Specials", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var releaseDate = GetSeasonReleaseDate(season);
                if (releaseDate is null)
                {
                    _logger.LogWarning(
                        "Skipping {Series} / {Season}: no Season PremiereDate and no dated episode was found",
                        show.Name,
                        season.Name);
                    continue;
                }

                var bucket = SeasonBucket.FromDate(releaseDate.Value);
                if (!buckets.TryGetValue(bucket, out var members))
                {
                    members = [];
                    buckets[bucket] = members;
                }

                members.Add(new SeasonBucketEntry(season, show));
            }

            examined++;
            if (series.Length > 0)
            {
                progress.Report(Math.Min(50, examined * 50d / series.Length));
            }
        }

        var allCollections = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            Recursive = true
        }).OfType<BoxSet>().ToArray();

        var processed = 0;
        foreach (var pair in buckets.OrderBy(p => p.Key.Year).ThenBy(p => (int)p.Key.Quarter))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bucket = pair.Key;
            var desiredSeasons = pair.Value
                .Select(entry => entry.Season)
                .GroupBy(s => s.Id)
                .Select(g => g.First())
                .OrderBy(s => s.SortName ?? s.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var desiredSeries = pair.Value
                .Select(entry => entry.Series)
                .GroupBy(s => s.Id)
                .Select(g => g.First())
                .OrderBy(s => s.SortName ?? s.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var collection = FindOwnedCollection(allCollections, bucket);
            if (collection is null)
            {
                var collision = allCollections.FirstOrDefault(c => string.Equals(c.Name, bucket.DisplayName, StringComparison.OrdinalIgnoreCase));
                if (collision is not null)
                {
                    _logger.LogWarning(
                        "Not touching existing collection {Collection} because it is not stamped as owned by this plugin. Rename/remove it if you want this plugin to create that seasonal collection.",
                        collision.Name);
                    continue;
                }

                collection = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
                {
                    Name = bucket.DisplayName,
                    IsLocked = false,
                    ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [OwnershipProviderKey] = bucket.ProviderValue
                    },
                    ItemIdList = []
                }).ConfigureAwait(false);

                allCollections = [.. allCollections, collection];
                _logger.LogInformation("Created collection {Collection}", collection.Name);
            }

            var existingMembers = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = collection.Id,
                Recursive = true
            }).ToArray();

            var existingMemberIds = existingMembers.Select(item => item.Id).ToHashSet();

            var missingSeasonIds = desiredSeasons
                .Select(s => s.Id)
                .Where(id => !existingMemberIds.Contains(id))
                .ToArray();

            if (missingSeasonIds.Length > 0)
            {
                await _collectionManager.AddToCollectionAsync(collection.Id, missingSeasonIds).ConfigureAwait(false);
                _logger.LogInformation(
                    "Added {Count} new Season item(s) to {Collection}; {Existing} desired member(s) were already present",
                    missingSeasonIds.Length,
                    collection.Name,
                    desiredSeasons.Length - missingSeasonIds.Length);
            }
            else
            {
                _logger.LogDebug("No new Season items needed for {Collection}", collection.Name);
            }

            var existingSeriesIds = existingMembers
                .OfType<Series>()
                .Select(item => item.Id)
                .ToHashSet();

            if (includeParentSeriesForClientCompatibility)
            {
                var desiredSeriesIds = desiredSeries.Select(item => item.Id).ToHashSet();
                var missingSeriesIds = desiredSeriesIds
                    .Where(id => !existingMemberIds.Contains(id))
                    .ToArray();

                if (missingSeriesIds.Length > 0)
                {
                    await _collectionManager.AddToCollectionAsync(collection.Id, missingSeriesIds).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Added {Count} parent Series item(s) to {Collection} for TV-client compatibility",
                        missingSeriesIds.Length,
                        collection.Name);
                }

                var staleSeriesIds = existingSeriesIds
                    .Where(id => !desiredSeriesIds.Contains(id))
                    .ToArray();

                if (staleSeriesIds.Length > 0)
                {
                    await _collectionManager.RemoveFromCollectionAsync(collection.Id, staleSeriesIds).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Removed {Count} stale parent Series compatibility item(s) from {Collection}",
                        staleSeriesIds.Length,
                        collection.Name);
                }
            }
            else if (existingSeriesIds.Count > 0)
            {
                await _collectionManager.RemoveFromCollectionAsync(collection.Id, existingSeriesIds).ConfigureAwait(false);
                _logger.LogInformation(
                    "Removed {Count} parent Series compatibility item(s) from {Collection} because compatibility mode is disabled",
                    existingSeriesIds.Count,
                    collection.Name);
            }

            collection.PremiereDate = bucket.CanonicalDateUtc;
            collection.ProductionYear = bucket.Year;
            collection.ForcedSortName = bucket.ForcedSortName;
            collection.Overview = includeParentSeriesForClientCompatibility
                ? $"Anime seasons released in {bucket.Quarter} {bucket.Year}. Matching parent series are also included for TV-client compatibility. Generated and maintained by Anime Season Collections."
                : $"Anime seasons released in {bucket.Quarter} {bucket.Year}. Generated and maintained by Anime Season Collections.";
            collection.ProviderIds[OwnershipProviderKey] = bucket.ProviderValue;

            await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            await _artworkService.EnsureArtworkAsync(collection, bucket, desiredSeasons, cancellationToken).ConfigureAwait(false);

            processed++;
            progress.Report(50 + (processed * 50d / Math.Max(1, buckets.Count)));
        }

        progress.Report(100);
        _logger.LogInformation("Anime Season Collections finished. {BucketCount} seasonal buckets processed", processed);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks
            }
        ];
    }

    private static Guid[] ParseLibraryIds(IEnumerable<string>? configuredIds)
    {
        if (configuredIds is null)
        {
            return [];
        }

        return configuredIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
    }

    private static bool IsAnimeSeries(Series series)
        => series.Genres.Any(g =>
            string.Equals(g, "Anime", StringComparison.OrdinalIgnoreCase)
            || string.Equals(g, "Animation", StringComparison.OrdinalIgnoreCase));

    private DateTime? GetSeasonReleaseDate(Season season)
    {
        if (season.PremiereDate.HasValue)
        {
            return season.PremiereDate.Value;
        }

        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            ParentId = season.Id,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Episode]
        })
        .OfType<Episode>()
        .Where(e => e.PremiereDate.HasValue)
        .OrderBy(e => e.PremiereDate)
        .Select(e => e.PremiereDate)
        .FirstOrDefault();
    }

    private static BoxSet? FindOwnedCollection(IEnumerable<BoxSet> collections, SeasonBucket bucket)
    {
        return collections.FirstOrDefault(c =>
            c.ProviderIds.TryGetValue(OwnershipProviderKey, out var stamp)
            && string.Equals(stamp, bucket.ProviderValue, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record SeasonBucketEntry(Season Season, Series Series);
}
