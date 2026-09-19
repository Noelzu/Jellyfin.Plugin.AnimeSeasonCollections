using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AnimeSeasonCollections.Models;
using Jellyfin.Plugin.AnimeSeasonCollections.Services;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Dto;
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

        var buckets = new Dictionary<SeasonBucket, List<Season>>();
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

                members.Add(season);
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

            // Jellyfin stores modern BoxSet membership in LinkedChildren rather than as
            // ordinary folder children. Use the same linked-child view as Jellyfin's own
            // CollectionManager so membership checks reflect the collection's real stored links.
            var existingMemberIds = collection
                .GetLinkedChildren(new DtoOptions(false))
                .Select(item => item.Id)
                .ToHashSet();

            var missingSeasonIds = desiredSeasons
                .Select(s => s.Id)
                .Where(id => !existingMemberIds.Contains(id))
                .ToArray();

            // Apply collection metadata and artwork before adding new members. Jellyfin's
            // AddToCollectionAsync persists the BoxSet and queues a metadata refresh; keeping
            // our own repository/image writes before that call avoids immediately racing the
            // queued collection.xml save after an add.
            var desiredOverview = $"Anime seasons released in {bucket.Quarter} {bucket.Year}. Generated and maintained by Anime Season Collections.";
            var metadataChanged = collection.PremiereDate != bucket.CanonicalDateUtc
                || collection.ProductionYear != bucket.Year
                || !string.Equals(collection.ForcedSortName, bucket.ForcedSortName, StringComparison.Ordinal)
                || !string.Equals(collection.Overview, desiredOverview, StringComparison.Ordinal)
                || !collection.ProviderIds.TryGetValue(OwnershipProviderKey, out var ownershipStamp)
                || !string.Equals(ownershipStamp, bucket.ProviderValue, StringComparison.OrdinalIgnoreCase);

            collection.PremiereDate = bucket.CanonicalDateUtc;
            collection.ProductionYear = bucket.Year;
            collection.ForcedSortName = bucket.ForcedSortName;
            collection.Overview = desiredOverview;
            collection.ProviderIds[OwnershipProviderKey] = bucket.ProviderValue;

            if (metadataChanged)
            {
                await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }

            await _artworkService.EnsureArtworkAsync(collection, bucket, desiredSeasons, cancellationToken).ConfigureAwait(false);

            if (missingSeasonIds.Length > 0)
            {
                await _collectionManager.AddToCollectionAsync(collection.Id, missingSeasonIds).ConfigureAwait(false);
                _logger.LogInformation(
                    "Attempted to add {Count} new Season item(s) to {Collection}; {Existing} desired linked member(s) were already present",
                    missingSeasonIds.Length,
                    collection.Name,
                    desiredSeasons.Length - missingSeasonIds.Length);
            }
            else
            {
                _logger.LogDebug("No new Season items needed for {Collection}", collection.Name);
            }

            // Reload the BoxSet and verify the actual LinkedChildren after the add operation.
            // This is the authoritative collection membership representation used by Jellyfin.
            var collectionAfterAdd = _libraryManager.GetItemById(collection.Id) as BoxSet ?? collection;
            var linkedMemberIdsAfter = collectionAfterAdd
                .GetLinkedChildren(new DtoOptions(false))
                .Select(item => item.Id)
                .ToHashSet();

            var stillMissingSeasons = desiredSeasons
                .Where(season => !linkedMemberIdsAfter.Contains(season.Id))
                .ToArray();

            _logger.LogInformation(
                "{Collection}: desired Seasons={DesiredCount}, linked members before={BeforeCount}, attempted additions={AttemptedCount}, desired Seasons linked after={PresentAfterCount}, still missing={MissingCount}",
                collection.Name,
                desiredSeasons.Length,
                existingMemberIds.Count,
                missingSeasonIds.Length,
                desiredSeasons.Length - stillMissingSeasons.Length,
                stillMissingSeasons.Length);

            foreach (var missingSeason in stillMissingSeasons)
            {
                var parentSeries = _libraryManager.GetItemById(missingSeason.ParentId) as Series;
                _logger.LogWarning(
                    "{Collection}: Season still missing from LinkedChildren after add verification. Series={Series}; Season={Season}; SeasonId={SeasonId}; Genres={Genres}",
                    collection.Name,
                    parentSeries?.Name ?? "(unknown series)",
                    missingSeason.Name,
                    missingSeason.Id,
                    parentSeries is null ? "(unknown)" : string.Join(", ", parentSeries.Genres));
            }

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
}
