@echo off
setlocal EnableExtensions
chcp 65001 >nul

set "ROOT=%~dp0"
set "PROJECT_DIR=%ROOT%UnityProject"
set "SERVER_DIR=%ROOT%server"
set "OUTPUT_EXE=%ROOT%Build\TikTokBarGame.exe"
set "SERVER_EXE=%SERVER_DIR%\target\release\tiktok-server.exe"
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

rem Goi cargo bang duong dan tuyet doi: mot dau nhay le trong bien PATH cua
rem Windows la du de cmd khong tra cuu duoc cac thu muc dung sau no, trong khi
rem where.exe tu doc PATH nen van tim ra.
set "CARGO_EXE="
for /f "delims=" %%c in ('where cargo 2^>nul') do if not defined CARGO_EXE set "CARGO_EXE=%%c"

if not defined CARGO_EXE (
    echo [LOI] Khong tim thay cargo. Cai Rust toolchain: https://rustup.rs/
    goto :failed
)

echo =======================================
echo     BUILD WANGNGUEN-BRIGDE LIVE
echo =======================================
echo Unity: %PROJECT_VERSION%
echo Editor: %UNITY_EXE%
echo Game: %OUTPUT_EXE%
echo Server: %SERVER_EXE%
echo.

rem Server truoc vi no build nhanh hon Unity nhieu; hong thi bao ngay.
echo [1/3] Dang build server Rust...
"%CARGO_EXE%" build --release --manifest-path "%SERVER_DIR%\Cargo.toml"
if errorlevel 1 (
    echo [LOI] cargo build that bai.
    goto :failed
)
if not exist "%SERVER_EXE%" (
    echo [LOI] Khong tao duoc %SERVER_EXE%.
    goto :failed
)

echo.
echo [2/3] Dang build game Unity...
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
echo Build thanh cong:
echo   Game  : %OUTPUT_EXE%
echo   Server: %SERVER_EXE%

rem Dong goi thanh 1 file ZIP phat hanh. Script dong goi viet bang bash vi CI
rem cung dung chinh no; bo qua neu may chua co Git Bash.
set "BASH_EXE="
for %%b in (
    "%ProgramFiles%\Git\bin\bash.exe"
    "%ProgramFiles(x86)%\Git\bin\bash.exe"
    "%LOCALAPPDATA%\Programs\Git\bin\bash.exe"
) do if not defined BASH_EXE if exist %%b set "BASH_EXE=%%~b"
if not defined BASH_EXE for /f "delims=" %%b in ('where bash 2^>nul') do if not defined BASH_EXE set "BASH_EXE=%%b"

if not defined BASH_EXE (
    echo.
    echo [BO QUA] Khong tim thay Git Bash nen chua tao file ZIP phat hanh.
    echo Cai Git for Windows roi chay lai, hoac tu chay:
    echo     bash scripts/package-windows.sh
    pause
    exit /b 0
)

echo.
echo [3/3] Dang dong goi ban phat hanh...
rem Goi bang duong dan tuong doi de khoi phai doi D:\... sang /d/... cho bash.
pushd "%ROOT%"
"%BASH_EXE%" scripts/package-windows.sh
set "PACK_RESULT=%ERRORLEVEL%"
popd
if not "%PACK_RESULT%"=="0" (
    echo [LOI] Dong goi that bai.
    goto :failed
)

pause
exit /b 0

:failed
echo.
pause
exit /b 1
