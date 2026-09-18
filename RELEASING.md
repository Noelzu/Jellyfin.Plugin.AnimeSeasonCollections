# Releasing Anime Season Collections

This document contains maintainer-facing release and manifest instructions.

## Public access requirement

For normal Jellyfin repository installation, both the GitHub repository manifest and the GitHub Release ZIP must be accessible without GitHub authentication.

The repository therefore needs to remain public, or the manifest and release assets must be hosted at other publicly reachable URLs. Jellyfin does not authenticate to GitHub when downloading the raw manifest or release ZIP.

## Automated release workflow

The repository includes:

```text
manifest.json
.github/workflows/release.yml
```

The **Build Jellyfin Release** GitHub Actions workflow handles release packaging and manifest maintenance.

To publish the current plugin version:

1. Open the repository on GitHub.
2. Go to **Actions**.
3. Select **Build Jellyfin Release**.
4. Click **Run workflow** and run it against `main`.

The workflow:

- installs the .NET 10 SDK;
- compiles the plugin;
- creates the stripped Jellyfin plugin ZIP;
- excludes foreign/runtime DLLs such as native `libSkiaSharp.dll`;
- calculates the ZIP's MD5 checksum;
- creates or updates the matching GitHub Release;
- uploads the plugin ZIP to the release;
- updates the matching entry in `manifest.json` with the release URL, checksum and timestamp;
- commits the updated manifest back to `main`.

## Versioning

Before publishing a new version, update the plugin version consistently in the project and build scripts.

For the current release, version `12.0.0.8`, the release asset is:

```text
https://github.com/Noelzu/Jellyfin.Plugin.AnimeSeasonCollections/releases/download/v12.0.0.8/AnimeSeasonCollections_12.0.0.8.zip
```

The Jellyfin repository URL is:

```text
https://raw.githubusercontent.com/Noelzu/Jellyfin.Plugin.AnimeSeasonCollections/main/manifest.json
```

## Packaging note

The release ZIP should contain only the plugin's managed DLL, plus optional diagnostics/documentation files such as `.pdb` and `.xml`.

Do not ship platform-specific runtime/native DLLs such as:

```text
runtimes/win-arm64/native/libSkiaSharp.dll
```

Jellyfin may try to load native DLLs from the plugin directory as managed assemblies and disable the plugin with `BadImageFormatException`.

The included build scripts and release workflow deliberately prevent these files from being packaged.
