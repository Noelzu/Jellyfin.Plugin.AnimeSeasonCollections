# Anime Season Collections for Jellyfin 12

This Plugin was vibecoded using ChatGPT. I also dont plan to check this repo to open so some bugs could remain unresolved for a bit.

A Jellyfin 12 / .NET 10 plugin that creates calendar-season collections from **Season items** belonging to shows whose Jellyfin **Genres** include `Anime` **or** `Animation`.

## What it does

- Scans `Series` whose `Genres` contains either `Anime` or `Animation` (case-insensitive), limited by the plugin's optional library include/exclude settings.
- Reads each regular Season; Specials/Season 0 are excluded.
- Uses the Season's own `PremiereDate`. If that is empty, the earliest dated episode is used as a fallback.
- Maps dates to:
  - December-February -> `YYYY Winter` (December is assigned to the **following year's** Winter label)
  - March-May -> `YYYY Spring`
  - June-August -> `YYYY Summer`
  - September-November -> `YYYY Autumn`
- Example: a Season dated `2025-12-20`, `2026-01-10`, or `2026-02-28` is placed into `2026 Winter`. This intentionally follows the likely **start of the anime cour** rather than where most of its episodes air.
- Creates real native Jellyfin collections containing the **Season items**, not the parent Series.
- Gives every generated collection a canonical release date at the first day of its bucket, e.g. `2026 Winter` = `2026-01-01`.
- Sets ProductionYear and a chronological ForcedSortName (`YYYY-01` through `YYYY-04`).
- Reads each generated collection's existing members first and calls Jellyfin's `ICollectionManager` only for Season IDs that are actually missing; already-added Seasons are not re-submitted on later task runs.
- Stamps generated collections in `ProviderIds` so the plugin knows which collections it owns. It deliberately refuses to modify an unstamped user collection with the same name.
- Generates:
  - a 1500x2250 (2:3) Primary collage using all readable Primary images on the Season items;
  - a transparent 1600x600 season-coloured Logo showing the year and season name.
- Regenerates artwork only when the desired Season membership or source Season poster files change.
- Adds `Refresh Anime Season Collections` under **Dashboard -> Scheduled Tasks -> Library**.
- Default schedule: every 24 hours. Jellyfin's Scheduled Tasks UI can change or disable that schedule.
- Adds a plugin configuration page with per-library **Include** and **Exclude** controls.
  - No Include selections = all libraries are eligible.
  - One or more Include selections = only those libraries are scanned.
  - Exclude always wins and removes that library from the scan.

## Important behavior

This first build is **additive**. If a Season's date is later corrected so it belongs to a different bucket, the next run adds it to the new bucket but does not automatically remove it from the old generated bucket. That avoids destructive edits in the initial version. Removal/reconciliation can be added once tested on your server.

## Build

Requires the .NET 10 SDK.

### Windows PowerShell

```powershell
./build.ps1
```

### Linux

```bash
chmod +x build.sh
./build.sh
```

The scripts publish into `dist/plugin` and create `dist/AnimeSeasonCollections_12.0.0.8.zip`.

## Manual install

1. Stop Jellyfin.
2. Create a plugin folder, for example:
   `/config/data/plugins/Anime Season Collections_12.0.0.8/`
3. Copy the contents of `dist/plugin` into it.
4. Start Jellyfin.
5. Open the plugin settings page and choose any library Include/Exclude rules you want.
6. Run `Dashboard -> Scheduled Tasks -> Library -> Refresh Anime Season Collections` once manually.

## Compatibility

- Jellyfin Server: 12.0.x
- Target framework: `net10.0`
- Jellyfin API packages: `12.0.0`
- SkiaSharp: `3.119.4`, matching Jellyfin 12's runtime line

### Jellyfin 12 / SkiaSharp packaging note

The release ZIP intentionally contains only `Jellyfin.Plugin.AnimeSeasonCollections.dll` (plus optional `.pdb`/`.xml` diagnostics). Jellyfin 12 already provides SkiaSharp 3.119.4 and the Jellyfin framework assemblies. Platform-specific `runtimes/*/native/libSkiaSharp.dll` files must **not** be placed in the plugin directory because Jellyfin can try to load them as managed plugin assemblies and disable the plugin with `BadImageFormatException`.
