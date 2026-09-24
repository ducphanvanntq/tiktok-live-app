@echo off
setlocal EnableExtensions
chcp 65001 >nul

set "ROOT=%~dp0"
set "SERVER_PORT=8085"
rem Cong ma game Unity noi toi, hardcode trong
rem UnityProject\Assets\Scripts\TikTokWebSocketClient.cs
set "GAME_PORT=8085"

echo =======================================
echo     KHOI DONG WANGNGUEN-BRIGDE LIVE
echo =======================================
echo.

rem Hai bo cuc: goi phat hanh co san binary trong Server\, con ban source thi
rem binary nam duoi server\target\release sau khi chay build.bat.
set "SERVER_HOME="
set "SERVER_BIN="
if exist "%ROOT%Server\tiktok-server.exe" (
    set "SERVER_HOME=%ROOT%Server"
    set "SERVER_BIN=%ROOT%Server\tiktok-server.exe"
) else if exist "%ROOT%server\target\release\tiktok-server.exe" (
    set "SERVER_HOME=%ROOT%server"
    set "SERVER_BIN=%ROOT%server\target\release\tiktok-server.exe"
)

if not defined SERVER_BIN (
    echo [LOI] Khong tim thay tiktok-server.exe.
    echo Neu ban dung goi phat hanh: hay giai nen TOAN BO file ZIP ra mot thu muc moi.
    echo Neu ban dung ban source: chay build.bat truoc.
    goto :failed
)

rem Server doc config/public/assets theo thu muc lam viec, khong theo vi tri
rem binary (server\src\config.rs:155 app_root).
if not exist "%SERVER_HOME%\config\game.json" (
    echo [LOI] Thieu %SERVER_HOME%\config\game.json.
    goto :failed
)

if not exist "%SERVER_HOME%\.env" if exist "%SERVER_HOME%\.env.example" (
    echo [1/3] Lan dau chay: tao .env tu .env.example...
    copy /y "%SERVER_HOME%\.env.example" "%SERVER_HOME%\.env" >nul
)

for /f "usebackq tokens=1,* delims==" %%a in (`findstr /b /r "^PORT=" "%SERVER_HOME%\.env" 2^>nul`) do set "SERVER_PORT=%%b"
rem Bo dau nhay neu nguoi dung viet PORT="8085"
set "SERVER_PORT=%SERVER_PORT:"=%"

set "CONTROL_URL=http://127.0.0.1:%SERVER_PORT%/control.html"
set "HEALTH_URL=http://127.0.0.1:%SERVER_PORT%/api/health"

call :server_is_ready
if defined SERVER_READY (
    echo [2/3] Server dang chay san tren cong %SERVER_PORT%.
    goto :launch_game
)

set "PORT_PID="
for /f "tokens=5" %%a in ('netstat -ano ^| findstr /r /c:":%SERVER_PORT% .*LISTENING" 2^>nul') do if not defined PORT_PID set "PORT_PID=%%a"
if defined PORT_PID (
    echo [LOI] Cong %SERVER_PORT% dang bi chuong trinh khac su dung ^(PID %PORT_PID%^).
    echo Launcher se KHONG tu tat chuong trinh khac de tranh mat du lieu.
    echo Hay dong chuong trinh do, sau do chay lai run.bat.
    goto :failed
)

echo [2/3] Dang khoi dong TikTok Server...
start "TikTok Server" /D "%SERVER_HOME%" "%SERVER_BIN%"

set "SERVER_READY="
for /l %%i in (1,1,30) do (
    if not defined SERVER_READY (
        call :server_is_ready
        if not defined SERVER_READY ping 127.0.0.1 -n 2 >nul
    )
)
if not defined SERVER_READY (
    echo [LOI] Server khong san sang sau 30 giay.
    echo Hay xem loi trong cua so "TikTok Server".
    goto :failed
)

:launch_game
echo [3/3] Dang khoi dong Game...
if not "%SERVER_PORT%"=="%GAME_PORT%" (
    echo [CANH BAO] Ban game dung san chi ket noi cong %GAME_PORT%.
    echo Server va Control Panel van chay tren cong %SERVER_PORT%, nhung game se khong duoc mo.
    goto :open_control
)
if exist "%ROOT%Build\TikTokBarGame.exe" (
    start "" "%ROOT%Build\TikTokBarGame.exe"
) else (
    echo [CANH BAO] Khong tim thay file Game trong thu muc Build.
    echo Chay build.bat sau khi cai Unity 6, hoac mo UnityProject bang Unity Hub.
)

:open_control
start "" "%CONTROL_URL%"
echo.
echo Da khoi dong. Control Panel: %CONTROL_URL%
exit /b 0

:server_is_ready
set "SERVER_READY="
for /f "usebackq delims=" %%r in (`powershell -NoProfile -Command "try { $h = Invoke-RestMethod -Uri '%HEALTH_URL%' -TimeoutSec 2; if ($h.status -eq 'ok' -and $h.appId -eq 'wangnguen-brigde') { 'YES' } } catch {}"`) do set "SERVER_READY=%%r"
exit /b 0

:failed
echo.
pause
exit /b 1
