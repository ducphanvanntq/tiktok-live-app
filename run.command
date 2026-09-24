#!/bin/bash
# Khoi dong WANGNGUEN-BRIGDE LIVE tren macOS — ban tuong duong cua run.bat.
# Duoi .command de nhap dup trong Finder la mo thang bang Terminal.
set -u

cd "$(dirname "$0")"
ROOT="$(pwd)"
GAME_PORT=8085   # cong hardcode trong UnityProject/Assets/Scripts/TikTokWebSocketClient.cs

echo "======================================="
echo "    KHOI DONG WANGNGUEN-BRIGDE LIVE"
echo "======================================="
echo

fail() {
    echo
    echo "$1" >&2
    echo
    read -r -p "Nhan Enter de dong cua so..." _
    exit 1
}

# Hai bo cuc: goi phat hanh co san binary trong Server/, con ban source thi
# binary nam duoi server/target/release sau khi build.
if [ -x "$ROOT/Server/tiktok-server" ]; then
    SERVER_HOME="$ROOT/Server"
    SERVER_BIN="$ROOT/Server/tiktok-server"
elif [ -x "$ROOT/server/target/release/tiktok-server" ]; then
    SERVER_HOME="$ROOT/server"
    SERVER_BIN="$ROOT/server/target/release/tiktok-server"
else
    fail "[LOI] Khong tim thay tiktok-server.
Neu dung goi phat hanh: hay giai nen TOAN BO file ZIP ra mot thu muc moi.
Neu dung ban source: chay 'cargo build --release' trong thu muc server."
fi

# Server doc config/public/assets theo thu muc lam viec, khong theo vi tri
# binary (server/src/config.rs:155 app_root).
[ -f "$SERVER_HOME/config/game.json" ] || fail "[LOI] Thieu $SERVER_HOME/config/game.json."

if [ ! -f "$SERVER_HOME/.env" ] && [ -f "$SERVER_HOME/.env.example" ]; then
    echo "[1/3] Lan dau chay: tao .env tu .env.example..."
    cp "$SERVER_HOME/.env.example" "$SERVER_HOME/.env"
fi

SERVER_PORT=8085
if [ -f "$SERVER_HOME/.env" ]; then
    value="$(sed -n 's/^PORT=//p' "$SERVER_HOME/.env" | head -1 | tr -d '"'"'"' \r')"
    [ -n "$value" ] && SERVER_PORT="$value"
fi

CONTROL_URL="http://127.0.0.1:$SERVER_PORT/control.html"
HEALTH_URL="http://127.0.0.1:$SERVER_PORT/api/health"

server_is_ready() {
    curl -fsS -m 2 "$HEALTH_URL" 2>/dev/null | grep -q '"appId":"wangnguen-brigde"'
}

if server_is_ready; then
    echo "[2/3] Server dang chay san tren cong $SERVER_PORT."
else
    if lsof -nP -iTCP:"$SERVER_PORT" -sTCP:LISTEN >/dev/null 2>&1; then
        pid="$(lsof -nP -tiTCP:"$SERVER_PORT" -sTCP:LISTEN | head -1)"
        fail "[LOI] Cong $SERVER_PORT dang bi chuong trinh khac su dung (PID $pid).
Launcher se KHONG tu tat chuong trinh khac de tranh mat du lieu.
Hay dong chuong trinh do, sau do chay lai run.command."
    fi

    echo "[2/3] Dang khoi dong TikTok Server..."
    ( cd "$SERVER_HOME" && "$SERVER_BIN" ) &

    ready=""
    for _ in $(seq 1 30); do
        if server_is_ready; then ready=1; break; fi
        sleep 1
    done
    [ -n "$ready" ] || fail "[LOI] Server khong san sang sau 30 giay. Xem loi o tren."
fi

echo "[3/3] Dang khoi dong Game..."
if [ "$SERVER_PORT" != "$GAME_PORT" ]; then
    echo "[CANH BAO] Ban game dung san chi ket noi cong $GAME_PORT."
    echo "Server va Control Panel van chay tren cong $SERVER_PORT, nhung game se khong duoc mo."
elif [ -d "$ROOT/TikTokBarGame.app" ]; then
    open "$ROOT/TikTokBarGame.app"
else
    echo "[CANH BAO] Khong tim thay TikTokBarGame.app canh run.command."
fi

open "$CONTROL_URL"
echo
echo "Da khoi dong. Control Panel: $CONTROL_URL"
echo "Dong cua so Terminal nay se tat server."
wait
