# Produces a local test package without publishing, changing update feeds or editing Events.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$HelperDirectory,
    [string]$Version = '1.1.0',
    [ValidateRange(1,65534)][int]$BuildRevision = 1
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskHelpers = (Resolve-Path -LiteralPath $HelperDirectory).Path
foreach ($taskRelative in @('steam-idle/AchievementLabs.SteamIdle.exe','gfwl/AchievementLabs.GfwlInjector.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskHelpers $taskRelative))) { throw "Missing prepared helper: $taskRelative" }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must have three numeric components.' }
$taskStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$taskOutput = Join-Path $taskRoot "releases/platform-test-$taskStamp"
New-Item -ItemType Directory -Path $taskOutput | Out-Null
& dotnet publish (Join-Path $taskRoot 'src/AchievementLabs.Desktop/AchievementLabs.Desktop.csproj') --no-restore -m:1 -nr:false -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:LabsSingleFile=true "-p:LabsHelperDirectory=$taskHelpers" "-p:Version=$Version" "-p:AssemblyVersion=$Version.$BuildRevision" "-p:FileVersion=$Version.$BuildRevision" "-p:InformationalVersion=$Version-platform-test.$BuildRevision" -p:UsedAvaloniaProducts= -p:NuGetAudit=false -o $taskOutput -v:q
if ($LASTEXITCODE -ne 0) { throw 'Platform test publish failed.' }
$taskPublished = @(Get-ChildItem -LiteralPath $taskOutput -File -Recurse)
if ($taskPublished.Count -ne 1 -or $taskPublished[0].Name -ne 'AchievementLabs.exe') { throw 'Expected one self-contained AchievementLabs.exe before adding test notes.' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/platform-test-build.md') -Destination (Join-Path $taskOutput 'README.md')
$taskExe = Join-Path $taskOutput 'AchievementLabs.exe'
$taskHash = (Get-FileHash -LiteralPath $taskExe -Algorithm SHA256).Hash.ToLowerInvariant()
[ordered]@{ version="$Version-platform-test.$BuildRevision"; sha256=$taskHash; selfContained=$true; published=$false; accountLibraries=$true; liveAccountSignInVerified=$false; launcherSessionAttachmentPlatforms=@('Epic','EA','Ubisoft','GOG'); achievementWritesTested=$false; spoofingSupported=$false } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskOutput 'build-info.json')
Write-Output "Test executable: $taskExe"
Write-Output "SHA256: $taskHash"
