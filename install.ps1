# Installs BlueSnap for the current user (no admin rights needed).
#   .\install.ps1 -Address 12:34:56:78:9A:BC
# Re-running updates the binaries and keeps the existing config.ini unless -Address is given.
param(
    [string]$Address,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'BlueSnap')
)
$ErrorActionPreference = 'Stop'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$valueName = 'BlueSnap'

# 1. Build into a staging folder
$stage = Join-Path $PSScriptRoot 'bin'
& (Join-Path $PSScriptRoot 'build.ps1') -OutDir $stage

# 2. Stop a running agent so its exe can be replaced
$existingCli = Join-Path $InstallDir 'bluesnap-cli.exe'
if (Test-Path $existingCli) { & $existingCli stop | Out-Null }
Get-Process BlueSnap -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# 3. Copy files
New-Item -ItemType Directory -Force $InstallDir | Out-Null
Copy-Item (Join-Path $stage 'BlueSnap.exe'), (Join-Path $stage 'bluesnap-cli.exe') $InstallDir -Force

# 4. Configuration
$cfg = Join-Path $InstallDir 'config.ini'
if ($Address -or -not (Test-Path $cfg)) {
    if (-not $Address) {
        Write-Host "Paired Bluetooth audio devices:`n"
        & (Join-Path $InstallDir 'bluesnap-cli.exe') list
        Write-Host "Now re-run with your speaker's address, e.g.:" -ForegroundColor Yellow
        Write-Host "  powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -Address 12:34:56:78:9A:BC" -ForegroundColor Yellow
        exit 1
    }
    $text = Get-Content (Join-Path $PSScriptRoot 'config.example.ini') -Raw
    $text = $text -replace '(?m)^DeviceAddress=.*$', "DeviceAddress=$Address"
    Set-Content -Path $cfg -Value $text -Encoding ascii
}

# 5. Autostart at sign-in (per-user Run key; audio devices are per user session)
$exe = Join-Path $InstallDir 'BlueSnap.exe'
Set-ItemProperty -Path $runKey -Name $valueName -Value "`"$exe`""

# 6. Start now
Start-Process $exe -WorkingDirectory $InstallDir
Start-Sleep -Seconds 2
& (Join-Path $InstallDir 'bluesnap-cli.exe') status
Write-Host "`nInstalled to $InstallDir (starts automatically at sign-in). Log: $(Join-Path $InstallDir 'agent.log')"
