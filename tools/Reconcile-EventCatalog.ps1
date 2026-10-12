# Builds a new snapshot only. Inputs are never edited. Sources run from lowest
# to highest priority; an existing title block is replaced as a complete unit.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Sources,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 is required.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Output must be a new directory.' }
$taskRoot = [ordered]@{}
$taskIds = [ordered]@{}
$taskOrigins = @{}
$taskChanges = [Collections.Generic.List[object]]::new()
$taskSnapshots = [Collections.Generic.List[object]]::new()
foreach ($taskSource in $Sources) {
    $taskDirectory = (Resolve-Path -LiteralPath $taskSource).Path
    $taskPath = Join-Path $taskDirectory 'Data.json'
    $taskData = Get-Content -LiteralPath $taskPath -Raw | ConvertFrom-Json -AsHashtable
    if ($taskData.SupportedTitleIDs -isnot [array]) { throw "SupportedTitleIDs array missing: $taskPath" }
    $taskSnapshots.Add([pscustomobject]@{Directory=$taskDirectory;Data=$taskData;Hash=(Get-FileHash -LiteralPath $taskPath).Hash})
    foreach ($taskKey in $taskData.Keys) {
        if ($taskKey -eq 'SupportedTitleIDs') { continue }
        if ($taskRoot.Contains($taskKey)) {
            $taskBefore = ConvertTo-Json -InputObject $taskRoot[$taskKey] -Depth 100 -Compress
            $taskAfter = ConvertTo-Json -InputObject $taskData[$taskKey] -Depth 100 -Compress
            if ($taskBefore -cne $taskAfter) { $taskChanges.Add([pscustomobject]@{TitleId=$taskKey;Previous=$taskOrigins[$taskKey];Winner=$taskDirectory}) }
        }
        $taskRoot[$taskKey] = $taskData[$taskKey]
        $taskOrigins[$taskKey] = $taskDirectory
    }
    foreach ($taskId in $taskData.SupportedTitleIDs) { $taskIds[[string]$taskId] = $taskId }
}
foreach ($taskId in $taskIds.Keys) { if (-not $taskRoot.Contains($taskId)) { throw "Listed ID has no title block: $taskId" } }
$taskRoot['SupportedTitleIDs'] = @($taskIds.Values)
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$taskBackups = Join-Path $OutputDirectory 'source-backups'
New-Item -ItemType Directory -Path $taskBackups | Out-Null
$taskIndex = 0
foreach ($taskSnapshot in $taskSnapshots) {
    $taskIndex++
    $taskBackup = Join-Path $taskBackups "$taskIndex"
    New-Item -ItemType Directory -Path $taskBackup | Out-Null
    foreach ($taskFile in Get-ChildItem -LiteralPath $taskSnapshot.Directory -Filter '*.json' -File) {
        Copy-Item -LiteralPath $taskFile.FullName -Destination $taskBackup
        if ($taskFile.Name -eq 'Data.json') { continue }
        # Numeric templates without a block are backed up but not published.
        if ($taskFile.BaseName -match '^\d+$' -and -not $taskRoot.Contains($taskFile.BaseName)) { continue }
        Copy-Item -LiteralPath $taskFile.FullName -Destination $OutputDirectory -Force
    }
}
$taskOutputPath = Join-Path $OutputDirectory 'Data.json'
[IO.File]::WriteAllText($taskOutputPath, (ConvertTo-Json -InputObject $taskRoot -Depth 100), [Text.UTF8Encoding]::new($false))
$taskVerified = Get-Content -LiteralPath $taskOutputPath -Raw | ConvertFrom-Json -AsHashtable
if (@($taskVerified.Keys)[-1] -ne 'SupportedTitleIDs') { throw 'SupportedTitleIDs must be last.' }
$taskInventory = @($taskVerified.Keys | Where-Object { $_ -match '^\d+$' } | ForEach-Object {
    $taskBlock = $taskVerified[$_]
    [pscustomobject]@{TitleId=$_;Name=$taskBlock.TitleName;FullySupported=$taskBlock.FullySupported;Enabled=($taskBlock.Enabled -ne $false);Listed=$taskIds.Contains($_);Mappings=if($taskBlock.Achievements){$taskBlock.Achievements.Count}else{0};Template=(Test-Path -LiteralPath (Join-Path $OutputDirectory "$_.json"));Source=$taskOrigins[$_]}
})
$taskInventory | Export-Csv -LiteralPath (Join-Path $taskBackups 'inventory.csv') -NoTypeInformation
@{Sources=@($taskSnapshots | Select-Object Directory,Hash);Titles=$taskInventory.Count;Listed=$taskIds.Count;MissingTemplates=@($taskInventory | Where-Object { -not $_.Template });Unlisted=@($taskInventory | Where-Object { -not $_.Listed });Replacements=@($taskChanges)} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $taskBackups 'reconciliation-report.json')
Write-Output "Reconciled $($taskInventory.Count) titles, $($taskIds.Count) listed IDs; $(@($taskInventory | Where-Object { -not $_.Template }).Count) missing templates."
