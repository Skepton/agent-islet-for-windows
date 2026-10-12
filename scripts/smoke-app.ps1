# End-to-end smoke test: launches the real app (tray only), drives it with the real forwarder, checks it stays healthy.
# Needs: dotnet build (app) and scripts\publish-hook.ps1 (forwarder). Does not touch ~/.claude/settings.json.
# Usage: powershell -File scripts\smoke-app.ps1 [-Config Debug]
param([string]$Config = 'Debug')

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..").Path
$app = "$root\src\Island.App\bin\$Config\net8.0-windows\ClaudeCodeIsland.exe"
$hook = "$root\artifacts\hook\island-hook.exe"
$fixtures = "$root\tests\fixtures\hooks"
foreach ($f in $app, $hook) { if (-not (Test-Path $f)) { throw "missing $f (build / publish first)" } }

$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$pipePath = "\\.\pipe\claude-code-island-$sid"
function Test-Pipe { [IO.Directory]::GetFiles('\\.\pipe\') -contains $pipePath }
$failures = 0
function Check($name, [bool]$ok, $detail = '') {
    if ($ok) { "  PASS  $name $detail" } else { "  FAIL  $name $detail"; $script:failures++ }
}

if (Test-Pipe) { throw "an island instance is already running; quit it from the tray first" }

$proc = $null
try {
    # --allow-all-projects: the fixtures' folder would otherwise wait for a Show / Ignore answer.
    try { $proc = Start-Process $app -ArgumentList '--tray', '--allow-all-projects' -PassThru }
    catch { throw "could not start the app (Smart App Control?): $($_.Exception.Message)" }

    $deadline = (Get-Date).AddSeconds(10)
    while (-not (Test-Pipe) -and (Get-Date) -lt $deadline -and -not $proc.HasExited) { Start-Sleep -Milliseconds 100 }
    Check 'app is listening on its pipe' (Test-Pipe) ($(if ($proc.HasExited) { "(process exited, code $($proc.ExitCode); check the CodeIntegrity log)" } else { '' }))
    if (-not (Test-Pipe)) { throw "app never started listening" }

    # Non-blocking events return immediately and print nothing.
    foreach ($name in 'SessionStart', 'UserPromptSubmit', 'Notification.permission_prompt', 'Stop') {
        $t = (Measure-Command { $out = & $hook simulate "$fixtures\$name.json" }).TotalMilliseconds
        Check "simulate $name returns quickly and silently" (($t -lt 1500) -and (-not $out) -and ($LASTEXITCODE -eq 0)) ("({0:N0} ms)" -f $t)
    }

    # A permission request nobody answers: forwarder gives up after 2 s with no output, app must survive and drop the card.
    $t = (Measure-Command { $out = & $hook simulate "$fixtures\PermissionRequest.json" 2 }).TotalMilliseconds
    Check 'unanswered permission request times out silently' ((-not $out) -and ($LASTEXITCODE -eq 0) -and ($t -lt 5000)) ("({0:N0} ms)" -f $t)
    Check 'app still alive after the timeout' (-not $proc.HasExited)

    # Latency with the app running.
    $ms = 1..20 | ForEach-Object { (Measure-Command { & $hook simulate "$fixtures\Stop.json" | Out-Null }).TotalMilliseconds } | Sort-Object
    Check 'median forwarder latency with app running' ($ms[10] -lt 120) ("(median {0:N1} ms, min {1:N1}, max {2:N1})" -f $ms[10], $ms[0], $ms[-1])

    # Second launch must exit by itself and leave the first one running.
    $second = Start-Process $app -ArgumentList '--tray' -PassThru
    $null = $second.WaitForExit(8000)
    Check 'second instance exits on its own' $second.HasExited
    Check 'first instance unaffected by the second' ((-not $proc.HasExited) -and (Test-Pipe))
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}

if ($failures -gt 0) { "`n$failures check(s) failed"; exit 1 } else { "`nall checks passed" }
