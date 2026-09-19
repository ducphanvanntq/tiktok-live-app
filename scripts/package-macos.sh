#!/usr/bin/env bash
# Assemble the macOS release package: Unity .app + sidecar media + Rust server.
#
# Usage:
#   scripts/package-macos.sh [SEARCH_ROOT] [OUT_DIR]
#
# Must run on macOS: the archive step uses `ditto`, which is the only reliable
# way to zip an .app bundle without losing symlinks and executable bits.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SEARCH_ROOT="${1:-$REPO_ROOT}"
OUT_DIR="${2:-$REPO_ROOT/dist}"
APP_NAME="TikTokBarGame.app"
SERVER_BIN_NAME="tiktok-server"

if [ "$(uname -s)" != "Darwin" ]; then
    echo "ERROR: script nay phai chay tren macOS (can ditto de nen .app)." >&2
    exit 1
fi

app_bundle="$(find "$SEARCH_ROOT" -type d -name "$APP_NAME" -prune -print 2>/dev/null | head -1)"
if [ -z "$app_bundle" ]; then
    echo "ERROR: khong tim thay $APP_NAME duoi $SEARCH_ROOT" >&2
    exit 1
fi

# Exclude the repo's own crate dir so a checked-out source tree cannot be
# mistaken for a built binary, and skip the Unity bundle's internals.
server_bin="$(find "$SEARCH_ROOT" \
    \( -name '*.app' -o -path '*/server/src' \) -prune -o \
    -type f -name "$SERVER_BIN_NAME" -perm -u+x -print 2>/dev/null | head -1)"
if [ -z "$server_bin" ]; then
    echo "ERROR: khong tim thay binary $SERVER_BIN_NAME duoi $SEARCH_ROOT" >&2
    exit 1
fi

version="${VERSION:-}"
if [ -z "$version" ]; then
    version="$(git -C "$REPO_ROOT" describe --tags --exact-match 2>/dev/null || true)"
fi
if [ -z "$version" ]; then
    version="$(sed -n 's/^  bundleVersion: *//p' "$REPO_ROOT/UnityProject/ProjectSettings/ProjectSettings.asset" | head -1)"
fi
version="${version#v}"
version="${version:-0.0}"

name="WangnguenBrigde-Live-macOS-v$version"
stage="$OUT_DIR/$name"

echo "App     : $app_bundle"
echo "Server  : $server_bin"
echo "Version : $version"
echo "Output  : $OUT_DIR/$name.zip"
echo "Arch    : $(lipo -archs "$server_bin" 2>/dev/null || echo 'khong doc duoc')"

rm -rf "$stage"
mkdir -p "$stage/Server"

# ditto preserves the bundle's symlinks and permission bits; cp -r does not.
ditto "$app_bundle" "$stage/$APP_NAME"

# On macOS Application.dataPath is <app>/Contents/Resources/Data, so the media
# folders the game probes for sit inside the bundle rather than next to it.
#   DJ_MUSIC -> MusicPlaylistPlayer.cs:158-163
#   DJ_VIDEO -> DjVideoScreen.cs:171-176, ClubBeatClock.cs:28-33 (BPM.txt)
resources="$stage/$APP_NAME/Contents/Resources"
mkdir -p "$resources"
ditto "$REPO_ROOT/DJ_MUSIC" "$resources/DJ_MUSIC"
ditto "$REPO_ROOT/DJ_VIDEO" "$resources/DJ_VIDEO"

# Nobody wants to dig through "Show Package Contents" to swap a song, so expose
# both folders next to run.command. The symlinks survive ditto's zip.
ln -s "$APP_NAME/Contents/Resources/DJ_MUSIC" "$stage/DJ_MUSIC"
ln -s "$APP_NAME/Contents/Resources/DJ_VIDEO" "$stage/DJ_VIDEO"

cp "$server_bin" "$stage/Server/$SERVER_BIN_NAME"
chmod +x "$stage/Server/$SERVER_BIN_NAME"
ditto "$REPO_ROOT/server/config" "$stage/Server/config"
ditto "$REPO_ROOT/server/public" "$stage/Server/public"
ditto "$REPO_ROOT/TikTokBridge/assets" "$stage/Server/assets"

sed 's|^ASSETS_DIR=.*|# ASSETS_DIR: goi phat hanh dung ./assets canh binary.|' \
    "$REPO_ROOT/server/.env.example" > "$stage/Server/.env.example"

cp "$REPO_ROOT/run.command" "$stage/"
chmod +x "$stage/run.command"
cp "$REPO_ROOT/README.md" "$REPO_ROOT/README-BAT-DAU.txt" "$REPO_ROOT/LICENSE" "$stage/"

# The package is unsigned, so Gatekeeper quarantines it on first launch.
cat > "$stage/DOC-TRUOC-KHI-CHAY.txt" <<'NOTE'
WANGNGUEN-BRIGDE LIVE - macOS

Goi nay CHUA DUOC KY SO (khong co tai khoan Apple Developer), nen macOS se
chan lan dau mo. Day la co che Gatekeeper binh thuong, khong phai virus.

CACH CHAY:

1. Giai nen TOAN BO file ZIP ra mot thu muc moi.
2. Mo Terminal tai thu muc vua giai nen, chay dung mot lan:

       xattr -dr com.apple.quarantine .

3. Nhap dup file run.command.

Neu buoc 2 bi bo qua, macOS se bao "khong mo duoc vi chua ro nguon goc".
Luc do vao System Settings > Privacy & Security, keo xuong duoi va bam
"Open Anyway", roi thu lai.

THEM NHAC VA VIDEO:

Tha file .mp3/.wav/.ogg vao thu muc DJ_MUSIC canh run.command.
Tha file .mp4/.mov/.webm vao thu muc DJ_VIDEO.
Hai thu muc do tro thang vao trong TikTokBarGame.app nen khong can mo bundle.

Control Panel: http://127.0.0.1:8085/control.html
NOTE

rm -f "$OUT_DIR/$name.zip"
# -c create, -k PKZip format, --keepParent keeps the top folder in the archive.
ditto -c -k --keepParent "$stage" "$OUT_DIR/$name.zip"

echo
echo "Package : $OUT_DIR/$name.zip"
( cd "$OUT_DIR" && shasum -a 256 "$name.zip" )
