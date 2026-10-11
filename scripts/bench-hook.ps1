# Measures wall-clock time per forwarder invocation (what Claude Code waits for on each hook).
# Usage: powershell -File scripts/bench-hook.ps1 -Exe artifacts\hook\island-hook.exe -Payload tests\fixtures\hooks\Stop.json -Runs 20
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Payload,
    [int]$Runs = 20
)

$json = [IO.File]::ReadAllText((Resolve-Path $Payload))
$ms = 1..$Runs | ForEach-Object {
    (Measure-Command { $json | & $Exe hook | Out-Null }).TotalMilliseconds
} | Sort-Object

"runs={0}  min={1:N1} ms  median={2:N1} ms  max={3:N1} ms" -f $Runs, $ms[0], $ms[[int][math]::Floor($Runs / 2)], $ms[-1]
