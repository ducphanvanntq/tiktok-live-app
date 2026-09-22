@echo off
setlocal
cd /d "%~dp0"
set "LED_PREVIEW=%~dp0UnityProject\Builds\LedPreview\TikTokBarGame.exe"
if not exist "%LED_PREVIEW%" (
    echo Chua co ban build LED Preview. Xem docs\mockups\README.md.
    pause
    exit /b 1
)
echo LED Preview: F6 doi mau, F7 doi hieu ung, F8 bat/tat sang.
echo Chay offline voi 20 NPC, khong ket noi TikTok.
start "LED Preview" "%LED_PREVIEW%" -ledFloorDemo -logFile "%~dp0UnityProject\Logs\led-floor-demo.log"
endlocal
