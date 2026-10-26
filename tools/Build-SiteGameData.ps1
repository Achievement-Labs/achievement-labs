# Public metadata only. Data.json is the authority for release readiness.
[CmdletBinding()]
param([string]$Destination)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (!$Destination) { $Destination = Join-Path $taskRoot 'site/assets/games.json' }
& (Join-Path $PSScriptRoot 'Export-StripeGameCatalog.ps1') -OutputDirectory 'artifacts/site-catalog'
$taskRows = Get-Content -LiteralPath (Join-Path $taskRoot 'artifacts/site-catalog/datajson-game-catalog.json') -Raw | ConvertFrom-Json
$taskPublic = @($taskRows | ForEach-Object {
    [ordered]@{
        titleId = $_.TitleId
        name = $_.Title
        platform = $_.Platform
        status = if ($_.SupportStatus -eq 'Fully supported') { 'supported' } elseif ($_.SupportStatus -eq 'Partially supported') { 'partial' } elseif ($_.SupportStatus -eq 'Testing') { 'testing' } else { 'coming-soon' }
    }
})
[IO.File]::WriteAllText($Destination, ($taskPublic | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
Write-Output "Wrote $($taskPublic.Count) public titles. No recipes or templates exported."
