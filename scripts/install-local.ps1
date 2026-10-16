# Installs the island for everyday use on this machine:
#   1. publishes the hook forwarder and the app to %LOCALAPPDATA%\AgentIslet\{bin,app}, outside the repo,
#      so rebuilding the repo never breaks the hooks your live Claude Code sessions run;
#   2. registers the hooks in Claude Code's user settings (~/.claude/settings.json, backed up first), so every
#      project reaches the island. The island then asks once per project whether to show it;
#   3. starts the app in the tray.
# Run it again to update: it closes the running island first. Usage:
#   powershell -File scripts\install-local.ps1 [-PollUsage] [-NoStart]
#   powershell -File scripts\install-local.ps1 -Uninstall      (removes the hooks; the files stay)
param(
    [switch]$Uninstall,
    [switch]$PollUsage,   # opt in to usage tracking (reads Claude Code's sign-in token, calls api.anthropic.com)
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'AgentIslet'
$bin = Join-Path $root 'bin'
$appDir = Join-Path $root 'app'
$hook = Join-Path $bin 'island-hook.exe'
$app = Join-Path $appDir 'AgentIslet.exe'

# Before the rename the app was "ClaudeCodeIsland" (own exe, folder and quit event). An island from that time may
# still be running or installed; it is closed the same way and its folder is left in place (nothing is deleted).
$oldRoot = Join-Path $env:LOCALAPPDATA 'ClaudeCodeIsland'
$oldHook = Join-Path $oldRoot 'bin\island-hook.exe'
$processNames = @('AgentIslet', 'ClaudeCodeIsland')
$quitEvents = @('Local\AgentIslet-Quit', 'Local\ClaudeCodeIsland-Quit')

$runningApp = @(Get-Process $processNames -ErrorAction SilentlyContinue)

if ($Uninstall) {
    if (-not (Test-Path $hook)) { throw "nothing is installed in $bin" }
    & $hook uninstall
    if ($LASTEXITCODE -ne 0) { throw "uninstall failed" }
    if ($runningApp.Count -gt 0) { Write-Host "The island app is still running; quit it from its tray icon." }
    Write-Host "Files remain in $root (projects.json there holds your Show / Ignore choices). Delete the folder to remove everything."
    return
}

if ($runningApp.Count -gt 0) {
    # Ask it to quit cleanly (any waiting permission request is released with "no decision": Claude Code asks itself).
    foreach ($name in $quitEvents) {
        $signal = $null
        if ([Threading.EventWaitHandle]::TryOpenExisting($name, [ref]$signal)) {
            Write-Host "Closing the running island to update it..."
            $null = $signal.Set(); $signal.Dispose()
        }
    }
    foreach ($p in $runningApp) { $null = $p.WaitForExit(15000) }
    $still = @(Get-Process $processNames -ErrorAction SilentlyContinue)
    if ($still.Count -gt 0) {
        throw "the island app is still running (pid $($still.Id -join ', ')); quit it from its tray icon, then run this again"
    }
}
$waitingHooks = @(Get-Process island-hook -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $hook -or $_.Path -eq $oldHook })
if ($waitingHooks.Count -gt 0) {
    throw "a Claude Code permission request is still waiting on the installed forwarder; answer it in Claude Code, then run this again"
}

& "$PSScriptRoot\publish-hook.ps1" -Out $bin

dotnet publish "$PSScriptRoot\..\src\Island.App\Island.App.csproj" -c Release -r win-x64 --self-contained false `
    -p:DebugType=none -o $appDir
if ($LASTEXITCODE -ne 0) { throw "app publish failed" }

& $hook install
if ($LASTEXITCODE -ne 0) { throw "hook install failed" }
if (Test-Path $oldRoot) {
    Write-Host "The hooks now point at $hook. The previous folder $oldRoot is no longer used and can be deleted once you are happy with this one (your choices were copied from it)."
}

if (-not $NoStart) {
    $appArgs = @('--tray')
    if ($PollUsage) { $appArgs += '--poll-usage' }
    Start-Process $app -ArgumentList $appArgs
    Write-Host "Island started in the tray ($app)."
}
Write-Host "While this island runs, 'dotnet run' of a dev build exits at once (one island per user). Quit this one from the tray to try a dev build; it then gets the same live events."
