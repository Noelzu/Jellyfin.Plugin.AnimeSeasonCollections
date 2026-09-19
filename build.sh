#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIST="$ROOT/dist"
PUBLISH="$DIST/publish"
PLUGIN="$DIST/plugin"
ZIP="$DIST/AnimeSeasonCollections_12.0.0.9.zip"
ASSEMBLY="Jellyfin.Plugin.AnimeSeasonCollections"

command -v dotnet >/dev/null 2>&1 || { echo '.NET 10 SDK was not found in PATH.' >&2; exit 1; }
command -v zip >/dev/null 2>&1 || { echo 'zip was not found in PATH.' >&2; exit 1; }

rm -rf "$DIST"
mkdir -p "$PUBLISH" "$PLUGIN"

dotnet publish "$ROOT/Jellyfin.Plugin.AnimeSeasonCollections.csproj" -c Release -o "$PUBLISH"

test -f "$PUBLISH/$ASSEMBLY.dll" || { echo "Expected plugin DLL was not produced." >&2; exit 1; }
cp "$PUBLISH/$ASSEMBLY.dll" "$PLUGIN/"
for ext in pdb xml; do
  test ! -f "$PUBLISH/$ASSEMBLY.$ext" || cp "$PUBLISH/$ASSEMBLY.$ext" "$PLUGIN/"
done

# Never ship NuGet runtime/native DLLs. Jellyfin scans plugin DLLs as managed
# assemblies and native libSkiaSharp.dll files will disable the plugin.
if find "$PLUGIN" -type f -name '*.dll' ! -name "$ASSEMBLY.dll" -print -quit | grep -q .; then
  echo 'Packaging error: foreign DLL found in plugin staging.' >&2
  find "$PLUGIN" -type f -name '*.dll' -print >&2
  exit 1
fi

(
  cd "$PLUGIN"
  zip -qr "$ZIP" .
)

echo "Built: $ZIP"
echo 'Packaged DLLs:'
find "$PLUGIN" -maxdepth 1 -type f -name '*.dll' -printf '  %f\n'
