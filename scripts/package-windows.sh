#!/usr/bin/env bash
# Assemble the Windows release package: Unity player + sidecar media + Node bridge.
#
# Usage:
#   scripts/package-windows.sh [SEARCH_ROOT] [OUT_DIR]
#
# SEARCH_ROOT is scanned for the built player (default: repository root, which
# covers both the CI output under UnityProject/build/ and the local Build/
# folder produced by build.bat). OUT_DIR receives the staged folder and the ZIP.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SEARCH_ROOT="${1:-$REPO_ROOT}"
OUT_DIR="${2:-$REPO_ROOT/dist}"
EXE_NAME="OngChuMMO_Live.exe"

player_exe="$(find "$SEARCH_ROOT" -type f -name "$EXE_NAME" -print -quit)"
if [ -z "$player_exe" ]; then
    echo "ERROR: khong tim thay $EXE_NAME duoi $SEARCH_ROOT" >&2
    echo "       Chay build.bat (hoac job Unity tren CI) truoc." >&2
    exit 1
fi
player_dir="$(cd "$(dirname "$player_exe")" && pwd)"

version="$(sed -n 's/^  bundleVersion: *//p' "$REPO_ROOT/UnityProject/ProjectSettings/ProjectSettings.asset" | head -1)"
version="${version:-0.0}"
name="OngChuMMO-Live-Windows-v$version"
stage="$OUT_DIR/$name"

echo "Player  : $player_dir"
echo "Version : $version"
echo "Output  : $OUT_DIR/$name.zip"

rm -rf "$stage"
mkdir -p "$stage/Build"

cp -r "$player_dir/." "$stage/Build/"

# Unity emits these next to the player; they are debug symbols, not runtime files.
find "$stage/Build" -maxdepth 1 \
    \( -name '*_BurstDebugInformation_DoNotShip' -o -name '*_BackUpThisFolder_ButDontShipItWithYourGame' \) \
    -exec rm -rf {} +

# The player looks for media in the directory holding the executable, falling
# back two levels up so the Editor can find the repo copies. Only the first
# path exists inside a distributed ZIP.
#   DJ_MUSIC -> MusicPlaylistPlayer.cs:163 (VOLUME.txt at :188)
#   DJ_VIDEO -> DjVideoScreen.cs:176, ClubBeatClock.cs:33 (BPM.txt)
cp -r "$REPO_ROOT/DJ_MUSIC" "$stage/Build/"
cp -r "$REPO_ROOT/DJ_VIDEO" "$stage/Build/"

# A clean checkout carries no node_modules and no .env; run.bat runs `npm ci`
# on first launch. Guard anyway so local runs cannot leak a developer .env.
cp -r "$REPO_ROOT/TikTokBridge" "$stage/"
rm -rf "$stage/TikTokBridge/node_modules" "$stage/TikTokBridge/.env"

cp "$REPO_ROOT/run.bat" "$stage/"
cp "$REPO_ROOT/README.md" "$REPO_ROOT/README-BAT-DAU.txt" "$REPO_ROOT/LICENSE" "$stage/"

# `zip` ships with the CI runners but not with Git Bash on Windows.
archive() {
    if command -v zip >/dev/null; then
        ( cd "$OUT_DIR" && zip -rq "$name.zip" "$name" )
        return
    fi
    for py in python3 python; do
        if command -v "$py" >/dev/null; then
            "$py" -c "import shutil,sys; shutil.make_archive(sys.argv[1], 'zip', sys.argv[2], sys.argv[3])" \
                "$OUT_DIR/$name" "$OUT_DIR" "$name"
            return
        fi
    done
    echo "ERROR: can khong zip hoac python de nen goi." >&2
    exit 1
}
rm -f "$OUT_DIR/$name.zip"
archive

echo
echo "Package : $OUT_DIR/$name.zip"
( cd "$OUT_DIR" && sha256sum "$name.zip" )
