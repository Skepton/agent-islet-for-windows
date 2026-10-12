# Focus-safety smoke test for the island overlay (PLAN section 5.5, the central UX risk).
# Launches the real app (--tray), sends permission requests through the real forwarder and checks that
#   - the island appears without becoming the foreground window,
#   - it carries WS_EX_NOACTIVATE and its buttons are what the mouse hits,
#   - a REAL mouse click on Yes / No answers the request,
#   - the foreground window is the same one afterwards.
# It moves the mouse for a moment (the cursor is restored), so don't touch the mouse or keyboard while it runs.
# Usage: powershell -File scripts\smoke-overlay.ps1 [-Config Debug]
param([string]$Config = 'Debug')

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Native {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
}
'@

# Coordinates from UI Automation and the mouse APIs must agree with the app's pixels.
[void][Native]::SetProcessDpiAwarenessContext([IntPtr]-4)   # per-monitor v2

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$root = (Resolve-Path "$PSScriptRoot\..").Path
$app = "$root\src\Island.App\bin\$Config\net8.0-windows\ClaudeCodeIsland.exe"
$hook = "$root\artifacts\hook\island-hook.exe"
$fixtures = "$root\tests\fixtures\hooks"
foreach ($f in $app, $hook) { if (-not (Test-Path $f)) { throw "missing $f (build / publish first)" } }

$WS_EX_NOACTIVATE = 0x08000000
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$pipePath = "\\.\pipe\claude-code-island-$sid"
function Test-Pipe { [IO.Directory]::GetFiles('\\.\pipe\') -contains $pipePath }

$failures = 0
function Check($name, [bool]$ok, $detail = '') {
    if ($ok) { "  PASS  $name $detail" } else { "  FAIL  $name $detail"; $script:failures++ }
}

function Describe-Window($hwnd) {
    if ($hwnd -eq [IntPtr]::Zero) { return 'none' }
    $procId = 0; [void][Native]::GetWindowThreadProcessId($hwnd, [ref]$procId)
    "$hwnd (pid $procId)"
}

function Find-IslandWindow($pidToFind, [int]$seconds = 10) {
    $deadline = (Get-Date).AddSeconds($seconds)
    $byPid = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $pidToFind)
    while ((Get-Date) -lt $deadline) {
        foreach ($w in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $byPid)) {
            if ($w.Current.Name -eq 'Claude Code Island' -and -not $w.Current.IsOffscreen) { return $w }
        }
        Start-Sleep -Milliseconds 100
    }
    return $null
}

function Find-Button($window, [string]$name, [int]$seconds = 5) {
    $deadline = (Get-Date).AddSeconds($seconds)
    $cond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)
    while ((Get-Date) -lt $deadline) {
        foreach ($b in $window.FindAll([Windows.Automation.TreeScope]::Descendants, $cond)) {
            if ($b.Current.Name -eq $name) { return $b }
        }
        Start-Sleep -Milliseconds 100
    }
    return $null
}

# True when the element under the point is the button itself or something inside it.
function Hits-Button($button, [int]$x, [int]$y) {
    $element = [Windows.Automation.AutomationElement]::FromPoint((New-Object Windows.Point($x, $y)))
    $walker = [Windows.Automation.TreeWalker]::ControlViewWalker
    for ($i = 0; $i -lt 5 -and $element; $i++) {
        if ([Windows.Automation.Automation]::Compare($element, $button)) { return $true }
        $element = $walker.GetParent($element)
    }
    return $false
}

function Real-Click($x, $y) {
    $saved = New-Object Native+POINT
    [void][Native]::GetCursorPos([ref]$saved)
    [void][Native]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 80
    [Native]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)   # left down
    [Native]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)   # left up
    Start-Sleep -Milliseconds 120
    [void][Native]::SetCursorPos($saved.X, $saved.Y)
}

if (Test-Pipe) { throw "an island instance is already running; quit it from the tray first" }

"This test moves the mouse for a moment. Don't touch the mouse or keyboard until it finishes."
$foreground = [Native]::GetForegroundWindow()
"foreground window before: $(Describe-Window $foreground)"

$proc = $null
try {
    # --allow-all-projects: the fixtures' folder would otherwise wait for a Show / Ignore answer.
    try { $proc = Start-Process $app -ArgumentList '--tray', '--allow-all-projects' -PassThru }
    catch { throw "could not start the app (Smart App Control?): $($_.Exception.Message)" }

    $deadline = (Get-Date).AddSeconds(10)
    while (-not (Test-Pipe) -and (Get-Date) -lt $deadline -and -not $proc.HasExited) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Pipe)) { throw "app never started listening" }

    Check 'island is hidden while nothing needs you' ($null -eq (Find-IslandWindow $proc.Id 1))

    # One round: a request appears, a real click on $buttonName answers it, focus never moves.
    function Round($fixture, $buttonName, $label) {
        $job = Start-Job -ScriptBlock { param($hook, $file) & $hook simulate $file 30 } -ArgumentList $hook, "$fixtures\$fixture"
        try {
            $window = Find-IslandWindow $proc.Id
            Check "$label`: island appears" ($null -ne $window)
            if (-not $window) { return }

            Start-Sleep -Milliseconds 600   # let the slide-in finish
            $now = [Native]::GetForegroundWindow()
            Check "$label`: foreground window did not change" ($now -eq $foreground) "(before $(Describe-Window $foreground), now $(Describe-Window $now))"

            $hwnd = [IntPtr]$window.Current.NativeWindowHandle
            $exStyle = [Native]::GetWindowLongPtr($hwnd, -20).ToInt64()
            Check "$label`: window has WS_EX_NOACTIVATE" (($exStyle -band $WS_EX_NOACTIVATE) -ne 0) ('(0x{0:X})' -f $exStyle)

            $button = Find-Button $window $buttonName
            Check "$label`: '$buttonName' button found" ($null -ne $button)
            if (-not $button) { return }

            $r = $button.Current.BoundingRectangle
            $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
            Check "$label`: mouse at the button's centre hits the button" (Hits-Button $button $x $y) "(at $x,$y)"

            Real-Click $x $y
            $null = Wait-Job $job -Timeout 15
            $script:out = (Receive-Job $job) -join ''

            $after = [Native]::GetForegroundWindow()
            Check "$label`: foreground window unchanged after the click" ($after -eq $foreground) "(now $(Describe-Window $after))"
        }
        finally { Remove-Job $job -Force -ErrorAction SilentlyContinue }
    }

    $script:out = $null
    Round 'PermissionRequest.with-suggestions.json' 'Yes' 'Yes'
    Check 'Yes -> forwarder prints an allow decision' ($script:out -match '"behavior":"allow"' -and $script:out -match '"hookEventName":"PermissionRequest"') "($($script:out))"
    Start-Sleep -Milliseconds 500
    Check 'island hides again after the answer' ($null -eq (Find-IslandWindow $proc.Id 2))

    $script:out = $null
    Round 'PermissionRequest.json' 'No' 'No'
    Check 'No -> forwarder prints a deny decision with a message' ($script:out -match '"behavior":"deny"' -and $script:out -match '"message"') "($($script:out))"

    Check 'app still alive' (-not $proc.HasExited)
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}

if ($failures -gt 0) { "`n$failures check(s) failed"; exit 1 } else { "`nall checks passed" }
