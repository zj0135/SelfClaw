$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot ".dotnet"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_NOLOGO = "1"

Write-Host "> dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj"
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --logger "console;verbosity=minimal"