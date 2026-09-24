@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\run-stage-preview.ps1"
if errorlevel 1 pause
endlocal
