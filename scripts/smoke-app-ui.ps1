# UI smoke test: shows the control panel, sends permission requests through the real forwarder, then clicks the
# buttons via Windows UI Automation and checks what the forwarder printed. Briefly shows a window on screen.
# Usage: powershell -File scripts\smoke-app-ui.ps1 [-Config Debug]
param([string]$Config = 'Debug')

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$root = (Resolve-Path "$PSScriptRoot\..").Path
$app = "$root\src\Island.App\bin\$Config\net8.0-windows\AgentIslet.exe"
$hook = "$root\artifacts\hook\island-hook.exe"
$fixtures = "$root\tests\fixtures\hooks"
foreach ($f in $app, $hook) { if (-not (Test-Path $f)) { throw "missing $f (build / publish first)" } }

$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$pipePath = "\\.\pipe\agent-islet-$sid"
function Test-Pipe { [IO.Directory]::GetFiles('\\.\pipe\') -contains $pipePath }
$failures = 0
function Check($name, [bool]$ok, $detail = '') {
    if ($ok) { "  PASS  $name $detail" } else { "  FAIL  $name $detail"; $script:failures++ }
}

function Find-Button($window, [string]$namePrefix, [int]$seconds = 8) {
    $deadline = (Get-Date).AddSeconds($seconds)
    $type = [Windows.Automation.ControlType]::Button
    while ((Get-Date) -lt $deadline) {
        $buttons = $window.FindAll([Windows.Automation.TreeScope]::Descendants,
            (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, $type)))
        foreach ($b in $buttons) { if ($b.Current.Name -like "$namePrefix*") { return $b } }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

function Click($button) { $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }

# Sends one permission request, clicks the named button, returns what the forwarder printed.
function Round($fixture, $buttonPrefix) {
    $job = Start-Job -ScriptBlock { param($hook, $file) & $hook simulate $file 30 } -ArgumentList $hook, "$fixtures\$fixture"
    $button = Find-Button $script:window $buttonPrefix
    if (-not $button) { Stop-Job $job; Remove-Job $job -Force; return $null }
    Click $button
    $null = Wait-Job $job -Timeout 15
    $out = (Receive-Job $job) -join ''
    Remove-Job $job -Force
    return $out
}

if (Test-Pipe) { throw "an island instance is already running; quit it from the tray first" }

$proc = $null
try {
    # --allow-all-projects: the fixtures' folder would otherwise wait for a Show / Ignore answer.
    try { $proc = Start-Process $app -ArgumentList '--allow-all-projects' -PassThru }
    catch { throw "could not start the app (Smart App Control?): $($_.Exception.Message)" }

    $deadline = (Get-Date).AddSeconds(10)
    while (-not (Test-Pipe) -and (Get-Date) -lt $deadline -and -not $proc.HasExited) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Pipe)) { throw "app never started listening" }

    $window = $null
    $deadline = (Get-Date).AddSeconds(10)
    while (-not $window -and (Get-Date) -lt $deadline) {
        # The process also owns the island overlay window, so pick the control panel by its title.
        $window = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,
            (New-Object Windows.Automation.AndCondition(
                (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)),
                (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'Agent Islet - panel')))))
        if (-not $window) { Start-Sleep -Milliseconds 150 }
    }
    Check 'control panel is shown' ($null -ne $window)
    if (-not $window) { throw "no window" }

    # The panel's Sessions tab uses the island's labels (No / Yes), and the full label for each remember option.
    $out = Round 'PermissionRequest.json' 'Yes'
    Check 'Yes -> forwarder prints an allow decision' ($out -match '"behavior":"allow"' -and $out -match '"hookEventName":"PermissionRequest"') "($out)"

    $out = Round 'PermissionRequest.json' 'No'
    Check 'No -> forwarder prints a deny decision with a message' ($out -match '"behavior":"deny"' -and $out -match '"message"') "($out)"

    $out = Round 'PermissionRequest.json' 'Answer in terminal'
    Check 'Answer in terminal -> forwarder prints nothing' ($out -eq '') "($out)"

    $out = Round 'PermissionRequest.with-suggestions.json' 'Allow + remember'
    Check 'Allow + remember -> decision carries updatedPermissions' ($out -match '"behavior":"allow"' -and $out -match '"updatedPermissions":\[\{"type":"addRules"') "($out)"

    $out = Round 'PermissionRequest.with-adddirectories.json' 'Allow + add folder'
    Check 'Allow + add folder -> decision carries the directory suggestion' ($out -match '"type":"addDirectories"') "($out)"

    Check 'app still alive' (-not $proc.HasExited)
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}

if ($failures -gt 0) { "`n$failures check(s) failed"; exit 1 } else { "`nall checks passed" }
