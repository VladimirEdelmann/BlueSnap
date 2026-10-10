# Builds both executables with the C# compiler that ships with Windows (.NET Framework 4.x).
# No SDK, NuGet packages or admin rights required.
#   BlueSnap.exe - background agent (no console window)
#   bluesnap-cli.exe            - same code as a console app: diagnostics CLI / foreground agent
param([string]$OutDir = (Join-Path $PSScriptRoot 'bin'))
$ErrorActionPreference = 'Stop'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw '.NET Framework 4.x compiler (csc.exe) not found.' }

$src = Join-Path $PSScriptRoot 'src\BlueSnap.cs'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$common = @('/nologo', '/optimize+', '/platform:anycpu', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll')

foreach ($t in @(@{ Target = 'winexe'; Name = 'BlueSnap.exe' }, @{ Target = 'exe'; Name = 'bluesnap-cli.exe' })) {
    $out = Join-Path $OutDir $t.Name
    & $csc @common "/target:$($t.Target)" "/out:$out" $src
    if ($LASTEXITCODE -ne 0) { throw "Compilation of $($t.Name) failed." }
    Write-Host "Built $out"
}
