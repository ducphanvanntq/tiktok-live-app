#!/usr/bin/env bash
# Assemble the Windows release package: Unity player + sidecar media + Rust server.
#
# Usage:
#   scripts/package-windows.sh [SEARCH_ROOT] [OUT_DIR]
#
# SEARCH_ROOT is scanned for the built player and the server binary (default:
# repository root, which covers the local build.bat output under Build/ and
# server/target/release/, as well as the artifact folders CI downloads into the
# workspace). OUT_DIR receives the staged folder and the ZIP.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SEARCH_ROOT="${1:-$REPO_ROOT}"
OUT_DIR="${2:-$REPO_ROOT/dist}"
EXE_NAME="TikTokBarGame.exe"
SERVER_EXE_NAME="tiktok-server.exe"

# Newest match wins: a stale player from an earlier build sitting next to the
# fresh one used to be picked at random by `find -print -quit`, which silently
# shipped the wrong binary. The prunes keep the scan off the multi-GB trees.
find_newest() {
    local name="$1"
    find "$SEARCH_ROOT" \
        \( -path '*/UnityProject/Library' -o -path '*/UnityProject/Temp' \
           -o -path '*/server/target/debug' -o -path '*/node_modules' \
           -o -path "$OUT_DIR" \) -prune -o \
        -type f -name "$name" -printf '%T@\t%p\n' 2>/dev/null \
        | sort -rn | head -1 | cut -f2-
}

player_exe="$(find_newest "$EXE_NAME")"
if [ -z "$player_exe" ]; then
    echo "ERROR: khong tim thay $EXE_NAME duoi $SEARCH_ROOT" >&2
    echo "       Chay build.bat (hoac job Unity tren CI) truoc." >&2
    exit 1
fi
player_dir="$(cd "$(dirname "$player_exe")" && pwd)"

server_exe="$(find_newest "$SERVER_EXE_NAME")"
if [ -z "$server_exe" ]; then
    echo "ERROR: khong tim thay $SERVER_EXE_NAME duoi $SEARCH_ROOT" >&2
    echo "       Chay build.bat, hoac: cargo build --release --manifest-path server/Cargo.toml" >&2
    exit 1
fi

# Version sources, most specific first. A tagged CI run passes VERSION so the
# ZIP name matches the GitHub Release; otherwise fall back to the tag on HEAD,
# then to whatever ProjectSettings says.
version="${VERSION:-}"
if [ -z "$version" ]; then
    version="$(git -C "$REPO_ROOT" describe --tags --exact-match 2>/dev/null || true)"
fi
if [ -z "$version" ]; then
    version="$(sed -n 's/^  bundleVersion: *//p' "$REPO_ROOT/UnityProject/ProjectSettings/ProjectSettings.asset" | head -1)"
fi
version="${version#v}"
version="${version:-0.0}"

name="WangnguenBrigde-Live-Windows-v$version"
stage="$OUT_DIR/$name"

echo "Player  : $player_dir"
echo "Server  : $server_exe"
echo "Version : $version"
echo "Output  : $OUT_DIR/$name.zip"

rm -rf "$stage"
mkdir -p "$stage/Build" "$stage/Server"

# Copy the player by whitelist rather than wholesale: Unity never cleans its
# output folder, so a development Build/ also holds players from every past
# rename, hand-made backup folders and the operator's own music. Copying the
# directory shipped a 448 MB ZIP of which 300 MB was one stale build.
# Root-level .dll files are matched too — that is where Unity puts native
# plugins next to UnityPlayer.dll.
keep_data="${EXE_NAME%.exe}_Data"
for entry in "$player_dir"/*; do
    base="$(basename "$entry")"
    case "$base" in
        "$EXE_NAME"|"$keep_data"|UnityCrashHandler64.exe|MonoBleedingEdge|D3D12|*.dll)
            cp -r "$entry" "$stage/Build/"
            ;;
        *_BurstDebugInformation_DoNotShip|*_BackUpThisFolder_ButDontShipItWithYourGame)
            # Debug symbols, not runtime files.
            ;;
        *)
            echo "  bo qua (khong phai output Unity): $base"
            ;;
    esac
done

# The player looks for media in the directory holding the executable, falling
# back two levels up so the Editor can find the repo copies. Only the first
# path exists inside a distributed ZIP.
#   DJ_MUSIC -> MusicPlaylistPlayer.cs:163 (VOLUME.txt at :188)
#   DJ_VIDEO -> DjVideoScreen.cs:176, ClubBeatClock.cs:33 (BPM.txt)
cp -r "$REPO_ROOT/DJ_MUSIC" "$stage/Build/"
cp -r "$REPO_ROOT/DJ_VIDEO" "$stage/Build/"

# The server resolves config/, public/ and assets/ against its working
# directory (config.rs:155 app_root), which run.bat sets to this folder.
cp "$server_exe" "$stage/Server/"
cp -r "$REPO_ROOT/server/config" "$stage/Server/"
cp -r "$REPO_ROOT/server/public" "$stage/Server/"

# Gift GIFs still live under TikTokBridge/ while the Node bridge remains in the
# repo as a developer-side fallback; inside the package they sit next to the
# binary, which is the server's default assets/ location.
cp -r "$REPO_ROOT/TikTokBridge/assets" "$stage/Server/"

# .env.example points ASSETS_DIR at the Node tree, which does not exist here.
# Comment it out so the packaged default (./assets) applies.
sed 's|^ASSETS_DIR=.*|# ASSETS_DIR: goi phat hanh dung ./assets canh binary.|' \
    "$REPO_ROOT/server/.env.example" > "$stage/Server/.env.example"

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
