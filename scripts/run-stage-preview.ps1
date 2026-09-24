$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$serverDirectory = Join-Path $workspace 'server'
$serverBinary = Join-Path $serverDirectory 'target\debug\tiktok-server.exe'
$gameBinary = Join-Path $workspace 'UnityProject\Builds\LedPreview\TikTokBarGame.exe'
if (-not (Test-Path -LiteralPath $serverBinary) -or -not (Test-Path -LiteralPath $gameBinary)) {
    throw 'Missing preview build. See docs/mockups/README.md for build commands.'
}
$listener = Get-NetTCPConnection -LocalPort 8085 -State Listen -ErrorAction SilentlyContinue
if ($listener) {
    $owner = Get-Process -Id $listener[0].OwningProcess -ErrorAction SilentlyContinue
    if ($owner.Path -ne $serverBinary) { throw 'Port 8085 is used by another server. Close that server before opening this preview.' }
} else {
    $env:PORT = '8085'
    $env:HOST = '127.0.0.1'
    $env:CONFIG_DIR = Join-Path $serverDirectory 'config'
    $env:PUBLIC_DIR = Join-Path $serverDirectory 'public'
    $serverProcess = Start-Process -FilePath $serverBinary -WorkingDirectory $serverDirectory -WindowStyle Hidden -PassThru
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($serverProcess.HasExited) { throw 'Preview server exited. Check server config.' }
        try { $null = Invoke-RestMethod 'http://127.0.0.1:8085/api/health'; $ready = $true; break } catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $ready) { throw 'Preview server did not become ready on port 8085.' }
}
$alreadyRunning = Get-Process TikTokBarGame -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $gameBinary }
if (-not $alreadyRunning) {
    $logPath = Join-Path $workspace 'UnityProject\Logs\stage-preview.log'
    Start-Process -FilePath $gameBinary -WorkingDirectory $workspace -ArgumentList @('-logFile', ('"' + $logPath + '"'))
}
Start-Process 'http://127.0.0.1:8085/control.html#lighting'
Write-Host 'Preview ready: http://127.0.0.1:8085/control.html#lighting'
