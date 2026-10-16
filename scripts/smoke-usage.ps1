# Live check of usage polling: launches the app with --poll-usage against a throwaway data folder, waits for the
# first successful poll, and checks what was persisted. Makes ONE real call to the usage endpoint per run (plus
# repeats only if you leave it running). Never prints or stores the token; it also scans the data folder to
# confirm the token and refresh token did not leak into any file.
# Usage: powershell -File scripts\smoke-usage.ps1 [-Config Debug]
param([string]$Config = 'Debug')

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..").Path
$app = "$root\src\Island.App\bin\$Config\net8.0-windows\AgentIslet.exe"
if (-not (Test-Path $app)) { throw "build the app first" }

$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if ([IO.Directory]::GetFiles('\\.\pipe\') -contains "\\.\pipe\agent-islet-$sid") { throw "an island instance is already running; quit it from the tray first" }

$failures = 0
function Check($name, [bool]$ok, $detail = '') {
    if ($ok) { "  PASS  $name $detail" } else { "  FAIL  $name $detail"; $script:failures++ }
}

$dataDir = Join-Path ([IO.Path]::GetTempPath()) ("island-usage-smoke-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dataDir | Out-Null
$stateFile = Join-Path $dataDir 'state.json'
$proc = $null
try {
    $env:ISLAND_DATA_DIR = $dataDir
    try { $proc = Start-Process $app -ArgumentList '--tray', '--poll-usage' -PassThru }
    catch { throw "could not start the app (Smart App Control?): $($_.Exception.Message)" }

    $deadline = (Get-Date).AddSeconds(30)
    while (-not (Test-Path $stateFile) -and (Get-Date) -lt $deadline -and -not $proc.HasExited) { Start-Sleep -Milliseconds 250 }
    Check 'first successful poll wrote the state file' (Test-Path $stateFile) ($(if ($proc.HasExited) { "(app exited with $($proc.ExitCode))" } else { '' }))

    if (Test-Path $stateFile) {
        $state = Get-Content $stateFile -Raw | ConvertFrom-Json
        $entries = @($state.windows.PSObject.Properties)
        Check 'at least one usage window recorded' ($entries.Count -ge 1) "($($entries.Count) window(s))"
        foreach ($e in $entries) {
            $w = $e.Value
            Check "window $($e.Name): percent is 0-100 and reset is in the future" (($w.lastPercent -ge 0) -and ($w.lastPercent -le 100) -and ([datetimeoffset]::Parse($w.resetsAt, [Globalization.CultureInfo]::InvariantCulture) -gt [datetimeoffset]::UtcNow)) ("(last {0:N1}%, announced threshold {1}, resets {2})" -f $w.lastPercent, $w.notified, $w.resetsAt)
        }
    }

    # The credentials never leave memory: neither token may appear in anything the app wrote.
    $credPath = Join-Path $env:USERPROFILE '.claude\.credentials.json'
    if (Test-Path $credPath) {
        $oauth = (Get-Content $credPath -Raw | ConvertFrom-Json).claudeAiOauth
        $secrets = @($oauth.accessToken, $oauth.refreshToken) | Where-Object { $_ }
        $leaked = $false
        foreach ($file in Get-ChildItem $dataDir -Recurse -File) {
            $text = [IO.File]::ReadAllText($file.FullName)
            foreach ($s in $secrets) { if ($text.Contains($s)) { $leaked = $true } }
        }
        Check 'no token appears in any file the app wrote' (-not $leaked)
    }
    Check 'app still running' (-not $proc.HasExited)
}
finally {
    Remove-Item Env:\ISLAND_DATA_DIR -ErrorAction SilentlyContinue
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures -gt 0) { "`n$failures check(s) failed"; exit 1 } else { "`nall checks passed" }
