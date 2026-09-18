using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.AnimeSeasonCollections.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.AnimeSeasonCollections.Services;

/// <summary>
/// Creates the generated collection poster and logo.
/// </summary>
public sealed class ArtworkService
{
    private const int PosterWidth = 1500;
    private const int PosterHeight = 2250;
    private const int LogoWidth = 1600;
    private const int LogoHeight = 600;
    private static readonly HttpClient HttpClient = new();

    private readonly ILogger<ArtworkService> _logger;

    public ArtworkService(ILogger<ArtworkService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Regenerates art only when membership or source season posters changed.
    /// </summary>
    public async Task EnsureArtworkAsync(
        BoxSet collection,
        SeasonBucket bucket,
        IReadOnlyCollection<Season> seasons,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(collection.Path))
        {
            _logger.LogWarning("Collection {Collection} has no writable Path; artwork skipped", collection.Name);
            return;
        }

        Directory.CreateDirectory(collection.Path);

        var posterPath = Path.Combine(collection.Path, "poster.png");
        var logoPath = Path.Combine(collection.Path, "logo.png");
        var hashPath = Path.Combine(collection.Path, ".anime-season-collections-art.sha256");

        var desiredHash = BuildArtworkHash(bucket, seasons);
        if (File.Exists(posterPath)
            && File.Exists(logoPath)
            && File.Exists(hashPath)
            && string.Equals(await File.ReadAllTextAsync(hashPath, cancellationToken).ConfigureAwait(false), desiredHash, StringComparison.Ordinal))
        {
            return;
        }

        var bitmaps = new List<SKBitmap>();
        try
        {
            foreach (var season in seasons.OrderBy(s => s.SortName ?? s.Name, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = season.PrimaryImagePath;
                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    _logger.LogDebug("Season {Season} has no own Primary image and is omitted from the collage", season.Name);
                    continue;
                }

                var bitmap = await TryLoadBitmapAsync(sourcePath, cancellationToken).ConfigureAwait(false);
                if (bitmap is not null)
                {
                    bitmaps.Add(bitmap);
                }
            }

            using var poster = RenderPoster(bucket, bitmaps);
            SavePng(poster, posterPath);

            using var logo = RenderLogo(bucket);
            SavePng(logo, logoPath);

            collection.SetImage(new ItemImageInfo
            {
                Path = posterPath,
                Type = ImageType.Primary,
                DateModified = File.GetLastWriteTimeUtc(posterPath),
                Width = PosterWidth,
                Height = PosterHeight
            }, 0);

            collection.SetImage(new ItemImageInfo
            {
                Path = logoPath,
                Type = ImageType.Logo,
                DateModified = File.GetLastWriteTimeUtc(logoPath),
                Width = LogoWidth,
                Height = LogoHeight
            }, 0);

            await collection.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(hashPath, desiredHash, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Generated Primary collage and Logo for {Collection}", collection.Name);
        }
        finally
        {
            foreach (var bitmap in bitmaps)
            {
                bitmap.Dispose();
            }
        }
    }

    private static string BuildArtworkHash(SeasonBucket bucket, IEnumerable<Season> seasons)
    {
        var sb = new StringBuilder(bucket.ProviderValue);
        foreach (var season in seasons.OrderBy(s => s.Id))
        {
            sb.Append('|').Append(season.Id.ToString("N"));
            sb.Append('|').Append(season.PrimaryImagePath ?? string.Empty);

            if (!string.IsNullOrWhiteSpace(season.PrimaryImagePath) && File.Exists(season.PrimaryImagePath))
            {
                sb.Append('|').Append(File.GetLastWriteTimeUtc(season.PrimaryImagePath).Ticks);
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private async Task<SKBitmap?> TryLoadBitmapAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                using var response = await HttpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return SKBitmap.Decode(stream);
            }

            if (!File.Exists(path))
            {
                _logger.LogDebug("Season poster does not exist at {Path}", path);
                return null;
            }

            await using var fs = File.OpenRead(path);
            return SKBitmap.Decode(fs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read season poster {Path}", path);
            return null;
        }
    }

    private static SKBitmap RenderPoster(SeasonBucket bucket, IReadOnlyList<SKBitmap> sourcePosters)
    {
        var bitmap = new SKBitmap(PosterWidth, PosterHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);

        var (dark, light) = Palette(bucket.Quarter);
        using (var background = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(PosterWidth, PosterHeight),
                new[] { dark, light },
                null,
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawRect(0, 0, PosterWidth, PosterHeight, background);
        }

        if (sourcePosters.Count > 0)
        {
            var columns = (int)Math.Ceiling(Math.Sqrt(sourcePosters.Count * (PosterWidth / (double)PosterHeight) * 1.5));
            columns = Math.Max(1, columns);
            var rows = (int)Math.Ceiling(sourcePosters.Count / (double)columns);
            var cellWidth = PosterWidth / (float)columns;
            var cellHeight = PosterHeight / (float)rows;

            for (var i = 0; i < sourcePosters.Count; i++)
            {
                var row = i / columns;
                var col = i % columns;
                var rowCount = Math.Min(columns, sourcePosters.Count - (row * columns));
                var xOffset = row == rows - 1 && rowCount < columns
                    ? (PosterWidth - (rowCount * cellWidth)) / 2f
                    : 0f;

                var dest = new SKRect(
                    xOffset + (col * cellWidth),
                    row * cellHeight,
                    xOffset + ((col + 1) * cellWidth),
                    (row + 1) * cellHeight);

                DrawCenterCropped(canvas, sourcePosters[i], dest);
            }

            using var tint = new SKPaint { Color = new SKColor(0, 0, 0, 28) };
            canvas.DrawRect(0, 0, PosterWidth, PosterHeight, tint);
        }

        using var frame = new SKPaint
        {
            Color = light.WithAlpha(220),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 12,
            IsAntialias = true
        };
        canvas.DrawRect(8, 8, PosterWidth - 16, PosterHeight - 16, frame);

        return bitmap;
    }

    private static SKBitmap RenderLogo(SeasonBucket bucket)
    {
        var bitmap = new SKBitmap(LogoWidth, LogoHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        var (dark, light) = Palette(bucket.Quarter);
        using var preferredTypeface = SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Bold)
            ?? SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
        var typeface = preferredTypeface ?? SKTypeface.Default;
        using var yearFont = new SKFont(typeface, 130);
        using var seasonFont = new SKFont(typeface, 270);

        using var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 170),
            IsAntialias = true
        };
        using var outline = new SKPaint
        {
            Color = dark.WithAlpha(235),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 14,
            StrokeJoin = SKStrokeJoin.Round
        };
        using var fill = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(200, 120),
                new SKPoint(1400, 480),
                new[] { light, SKColors.White },
                null,
                SKShaderTileMode.Clamp)
        };

        var seasonText = bucket.Quarter.ToString().ToUpperInvariant();
        var x = LogoWidth / 2f;

        canvas.DrawText(bucket.Year.ToString(), x + 8, 175 + 8, SKTextAlign.Center, yearFont, shadow);
        canvas.DrawText(seasonText, x + 10, 455 + 10, SKTextAlign.Center, seasonFont, shadow);
        canvas.DrawText(bucket.Year.ToString(), x, 175, SKTextAlign.Center, yearFont, outline);
        canvas.DrawText(seasonText, x, 455, SKTextAlign.Center, seasonFont, outline);
        canvas.DrawText(bucket.Year.ToString(), x, 175, SKTextAlign.Center, yearFont, fill);
        canvas.DrawText(seasonText, x, 455, SKTextAlign.Center, seasonFont, fill);

        using var rule = new SKPaint
        {
            Color = light.WithAlpha(220),
            StrokeWidth = 8,
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true
        };
        canvas.DrawLine(440, 225, 1160, 225, rule);

        return bitmap;
    }

    private static void DrawCenterCropped(SKCanvas canvas, SKBitmap source, SKRect destination)
    {
        var sourceAspect = source.Width / (float)source.Height;
        var destAspect = destination.Width / destination.Height;
        SKRect sourceRect;

        if (sourceAspect > destAspect)
        {
            var wantedWidth = source.Height * destAspect;
            var left = (source.Width - wantedWidth) / 2f;
            sourceRect = new SKRect(left, 0, left + wantedWidth, source.Height);
        }
        else
        {
            var wantedHeight = source.Width / destAspect;
            var top = (source.Height - wantedHeight) / 2f;
            sourceRect = new SKRect(0, top, source.Width, top + wantedHeight);
        }

        canvas.DrawBitmap(source, sourceRect, destination);
    }

    private static void SavePng(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static (SKColor Dark, SKColor Light) Palette(AnimeQuarter quarter) => quarter switch
    {
        AnimeQuarter.Winter => (new SKColor(32, 81, 122), new SKColor(154, 224, 255)),
        AnimeQuarter.Spring => (new SKColor(114, 48, 88), new SKColor(255, 157, 194)),
        AnimeQuarter.Summer => (new SKColor(142, 72, 24), new SKColor(255, 195, 72)),
        AnimeQuarter.Autumn => (new SKColor(100, 47, 29), new SKColor(237, 137, 62)),
        _ => (new SKColor(45, 45, 45), new SKColor(220, 220, 220))
    };
}
