# Remakes the pictures in docs/images (the usage bar, the usage strip and every card) from the demo data.
# The app draws each view straight into a PNG (--shots), so nothing on your screen is captured and nothing shows up on it.
# It runs next to a running island without touching it: own data folder, own single-instance lock, own pipe.
# Usage: powershell -File scripts\make-screenshots.ps1 [-Config Debug] [-Exe <path to AgentIslet.exe>]
# (-Exe is for a build made with "dotnet build -o <folder>" while a running island holds the normal output folder.)
param([string]$Config = 'Debug', [string]$Exe)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$app = if ($Exe) { $Exe } else { "$root\src\Island.App\bin\$Config\net8.0-windows\AgentIslet.exe" }
if (-not (Test-Path $app)) { throw "build first: dotnet build AgentIslet.sln (missing $app)" }

$out = "$root\docs\images"
$data = Join-Path ([IO.Path]::GetTempPath()) ("island-shots-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $data | Out-Null

$env:ISLAND_DATA_DIR = $data
$env:ISLAND_INSTANCE_NAME = 'shots'
$env:ISLAND_PIPE_NAME = 'agent-islet-shots-test'
try {
    $proc = Start-Process $app -ArgumentList '--shots', "`"$out`"" -PassThru
    if (-not $proc.WaitForExit(90000)) { Stop-Process -Id $proc.Id -Force; throw "the screenshot run did not finish in 90 s" }
    if ($proc.ExitCode -ne 0) { throw "the screenshot run failed (exit code $($proc.ExitCode))" }
}
finally {
    Remove-Item Env:ISLAND_DATA_DIR, Env:ISLAND_INSTANCE_NAME, Env:ISLAND_PIPE_NAME -ErrorAction SilentlyContinue
    if (Test-Path $data) { Remove-Item $data -Recurse -Force }
}
Get-ChildItem $out -Filter '*.png' | Where-Object { $_.Name -ne 'icon.png' -and $_.Name -ne 'icon-preview.png' } | ForEach-Object { "  $($_.Name)  ($($_.Length) bytes)" }
