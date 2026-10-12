param(
    [string]$OutputDirectory = 'docs/commerce'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repo $OutputDirectory
$data = Get-Content -LiteralPath (Join-Path $repo 'src/AchievementLabs.App/Events/Data.json') -Raw | ConvertFrom-Json -AsHashtable
$catalog = Get-Content -LiteralPath (Join-Path $repo 'XboxTitleIDs.json') -Raw | ConvertFrom-Json
$byId = @{}
foreach ($entry in $catalog) { $byId[[string]$entry.TitleId] = $entry }

$manual = @{
    '571417442' = @{ Title = 'Gigantic'; Devices = @('PC') }
    '908546367' = @{ Title = 'Songbringer'; Devices = @('PC') }
    '1835298427' = @{ Title = 'Minecraft'; Devices = @('Apple TV') }
}

function Get-PlatformLabel([object[]]$Devices) {
    $labels = foreach ($device in $Devices) {
        switch ([string]$device) {
            'PC' { 'Windows PC' }
            'Windows' { 'Windows PC' }
            'Desktop' { 'Windows PC' }
            'XboxOne' { 'Xbox One' }
            'XboxSeries' { 'Xbox Series X|S' }
            'Xbox360' { 'Xbox 360' }
            default { [string]$device }
        }
    }
    return (@($labels | Where-Object { $_ } | Select-Object -Unique) -join ', ')
}

$rows = foreach ($titleId in $data.Keys | Where-Object { $_ -match '^\d+$' }) {
    $block = $data[$titleId]
    $record = $byId[$titleId]
    $fallback = $manual[$titleId]
    $title = if ($block.TitleName) { $block.TitleName } elseif ($record.Name) { $record.Name } elseif ($block.Name) { $block.Name } elseif ($fallback.Title) { $fallback.Title } else { "Title $titleId" }
    $devices = if ($block.Devices) { @($block.Devices) } elseif ($record.Devices) { @($record.Devices) } else { @($fallback.Devices) }
    # Match hosted catalog eligibility; a block alone does not make it available.
    $available = $block.Enabled -ne $false -and
        $titleId -in @($data.SupportedTitleIDs | ForEach-Object { [string]$_ }) -and
        (Test-Path -LiteralPath (Join-Path $repo "src/AchievementLabs.App/Events/$titleId.json"))
    [pscustomobject][ordered]@{
        Title = $title
        Platform = Get-PlatformLabel $devices
        TitleId = $titleId
        SupportStatus = if ($available -and [bool]$block.FullySupported) {
            'Fully supported'
        } elseif ($available) {
            'Testing'
        } else {
            'Draft'
        }
    }
}

$rows = @($rows | Sort-Object Title, Platform, TitleId)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$rows | Export-Csv -LiteralPath (Join-Path $output 'datajson-game-catalog.csv') -NoTypeInformation -Encoding utf8
$rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'datajson-game-catalog.json') -Encoding utf8
$rows | Where-Object SupportStatus -eq 'Fully supported' | Export-Csv -LiteralPath (Join-Path $output 'stripe-products-ready.csv') -NoTypeInformation -Encoding utf8

Write-Output "Exported $($rows.Count) Data.json titles, including $(@($rows | Where-Object SupportStatus -eq 'Fully supported').Count) fully supported titles, to $output."
