@echo off
setlocal EnableExtensions
chcp 65001 >nul

set "ROOT=%~dp0"
set "PROJECT_DIR=%ROOT%UnityProject"
set "OUTPUT_EXE=%ROOT%Build\WangnguenBrigde_Live.exe"
set "LOG_FILE=%ROOT%build_log.txt"
set "PROJECT_VERSION="

for /f "tokens=2 delims=:" %%v in ('findstr /b "m_EditorVersion:" "%PROJECT_DIR%\ProjectSettings\ProjectVersion.txt" 2^>nul') do for /f "tokens=*" %%w in ("%%v") do set "PROJECT_VERSION=%%w"

if not defined PROJECT_VERSION (
    echo [LOI] Khong doc duoc phien ban Unity tu ProjectVersion.txt.
    goto :failed
)

rem Unity Hub cho phep doi thu muc cai Editor sang o dia khac, nen khong the
rem gia dinh no luon nam o C:\Program Files. Tim theo thu tu: bien moi truong
rem UNITY_EDITOR (chi dinh thu cong) -> duong dan phu Hub da luu -> cac vi tri
rem thong dung.
set "UNITY_EXE="

if defined UNITY_EDITOR if exist "%UNITY_EDITOR%" set "UNITY_EXE=%UNITY_EDITOR%"

set "HUB_CFG=%APPDATA%\UnityHub\secondaryInstallPath.json"
if not defined UNITY_EXE if exist "%HUB_CFG%" (
    for /f usebackq^ tokens^=1^ delims^=^" %%p in ("%HUB_CFG%") do (
        if exist "%%p\%PROJECT_VERSION%\Editor\Unity.exe" set "UNITY_EXE=%%p\%PROJECT_VERSION%\Editor\Unity.exe"
    )
)

for %%r in (
    "C:\Program Files\Unity\Hub\Editor"
    "D:\Unity\Editors"
    "D:\Unity\Hub\Editor"
    "D:\Program Files\Unity\Hub\Editor"
    "E:\Unity\Editors"
    "E:\Unity\Hub\Editor"
) do if not defined UNITY_EXE if exist "%%~r\%PROJECT_VERSION%\Editor\Unity.exe" set "UNITY_EXE=%%~r\%PROJECT_VERSION%\Editor\Unity.exe"

if not defined UNITY_EXE (
    echo [LOI] Khong tim thay Unity %PROJECT_VERSION% tren may.
    echo Cai phien ban nay bang Unity Hub, hoac chi dinh truc tiep:
    echo     set "UNITY_EDITOR=D:\duong\dan\Editor\Unity.exe"
    echo     build.bat
    goto :failed
)

echo =======================================
echo     BUILD GAME WANGNGUEN-BRIGDE LIVE
echo =======================================
echo Unity: %PROJECT_VERSION%
echo Editor: %UNITY_EXE%
echo Output: %OUTPUT_EXE%
echo.

if not exist "%ROOT%Build" mkdir "%ROOT%Build"
"%UNITY_EXE%" -quit -batchmode -projectPath "%PROJECT_DIR%" -buildWindows64Player "%OUTPUT_EXE%" -logFile "%LOG_FILE%"
set "BUILD_RESULT=%ERRORLEVEL%"

if not "%BUILD_RESULT%"=="0" (
    echo [LOI] Unity build that bai ^(ma loi %BUILD_RESULT%^).
    echo Xem log: %LOG_FILE%
    goto :failed
)

if not exist "%OUTPUT_EXE%" (
    echo [LOI] Unity khong tao file %OUTPUT_EXE%.
    echo Xem log: %LOG_FILE%
    goto :failed
)

echo.
echo Build thanh cong: %OUTPUT_EXE%
pause
exit /b 0

:failed
echo.
pause
exit /b 1
