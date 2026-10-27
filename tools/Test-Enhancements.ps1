param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskProject = Join-Path $taskRoot 'tests/AchievementLabs.Enhancements.Tests/AchievementLabs.Enhancements.Tests.csproj'
& dotnet build $taskProject -c $Configuration -m:1
if ($LASTEXITCODE -ne 0) { throw 'Enhancement test build failed.' }
$taskTests = Join-Path $taskRoot "tests/AchievementLabs.Enhancements.Tests/bin/$Configuration/net9.0-windows10.0.19041.0/AchievementLabs.Enhancements.Tests.dll"
foreach ($taskCase in @('', '--scroll', '--mapping', '--editions', '--queue-tools', '--auto-token', '--playtime', '--port', '--export', '--ui-tweaks', '--title-search', '--startup-mode', '--queue-descriptions', '--event-verification', '--xbox360-preview', '--exophase-360', '--mirror')) {
    if ($taskCase) { & dotnet $taskTests $taskCase }
    else { & dotnet $taskTests }
    if ($LASTEXITCODE -ne 0) { throw "Enhancement test failed: $taskCase" }
}
