# Removes BlueSnap for the current user. Does not touch Bluetooth pairing or audio settings.
param([string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'BlueSnap'))

$cli = Join-Path $InstallDir 'bluesnap-cli.exe'
if (Test-Path $cli) { & $cli stop | Out-Null }
Get-Process BlueSnap -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'BlueSnap' -ErrorAction SilentlyContinue
if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir }
Write-Host 'BlueSnap removed.'
