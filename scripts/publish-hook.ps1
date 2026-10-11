# Publishes the hook forwarder as one self-contained, ReadyToRun, trimmed exe.
# Usage: pwsh scripts/publish-hook.ps1 [-Out <dir>]
param(
    [string]$Out = "$PSScriptRoot\..\artifacts\hook"
)

$ErrorActionPreference = 'Stop'
dotnet publish "$PSScriptRoot\..\src\Island.Hook\Island.Hook.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:PublishTrimmed=true `
    -p:DebugType=none -o $Out
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Write-Host "Published: $(Resolve-Path (Join-Path $Out 'island-hook.exe'))"
