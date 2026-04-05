$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot ".dotnet"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_NOLOGO = "1"

$steps = @(
    "npm --prefix SelfClaw.TranscriptVue install",
    "npm --prefix SelfClaw.TranscriptVue run build",
    "dotnet restore SelfClaw.slnx --force-evaluate",
    "dotnet build SelfClaw.Core/SelfClaw.Core.csproj --no-restore",
    "dotnet build SelfClaw.Infrastructure/SelfClaw.Infrastructure.csproj --no-restore",
    "dotnet build SelfClaw.Desktop/SelfClaw.Desktop.csproj --no-restore",
    "dotnet build SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore"
)

foreach ($step in $steps) {
    Write-Host "> $step"
    Invoke-Expression $step
}
