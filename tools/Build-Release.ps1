# Creates a new, isolated release candidate; never deletes old builds or research.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [switch]$RefreshCatalog,
    [string]$EventsSource = (Join-Path $PSScriptRoot '../src/AchievementLabs.App/Events'),
    [ValidateRange(0,65534)][int]$BuildRevision = 0,
    [string]$SigningThumbprint = $env:ACHIEVEMENT_LABS_SIGNING_THUMBPRINT,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'Build-Release.ps1 requires PowerShell 7 or newer. Run it with: pwsh -File tools/Build-Release.ps1'
}
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a stable version such as 1.0.6.' }
$taskRoot = Split-Path -Parent $PSScriptRoot
if ($RefreshCatalog) { & (Join-Path $PSScriptRoot 'Build-SiteGameData.ps1') }
if (-not (Test-Path -LiteralPath (Join-Path $taskRoot 'site/assets/games.json'))) {
    throw 'Public catalog metadata is missing. Prepare it before building or use -RefreshCatalog.'
}
$taskStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$taskOutput = Join-Path $taskRoot "releases/$Version-$taskStamp"
$taskWork = Join-Path $taskRoot "artifacts/release-work/$taskStamp"
$taskHelpers = Join-Path $taskWork 'helpers'
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.dotnet'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.nuget-packages'
New-Item -ItemType Directory -Path $taskOutput,$taskHelpers -Force | Out-Null
foreach ($taskHelper in @(
    @{ Project='AchievementLabs.SteamIdle'; Rid='win-x64'; Folder='steam-idle'; Platform='AnyCPU' },
    @{ Project='AchievementLabs.GfwlInjector'; Rid='win-x86'; Folder='gfwl'; Platform='x86' }
)) {
    & dotnet publish (Join-Path $taskRoot "src/$($taskHelper.Project)") -c Release -r $taskHelper.Rid --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false "-p:Platform=$($taskHelper.Platform)" -p:NuGetAudit=false -o (Join-Path $taskHelpers $taskHelper.Folder) -v:q
    if ($LASTEXITCODE -ne 0) { throw "Helper publish failed: $($taskHelper.Project)" }
}
& dotnet publish (Join-Path $taskRoot 'src/AchievementLabs.Desktop') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:LabsSingleFile=true "-p:LabsHelperDirectory=$taskHelpers" "-p:LabsEventsSource=$([System.IO.Path]::GetFullPath($EventsSource))" "-p:Version=$Version" "-p:AssemblyVersion=$Version.$BuildRevision" "-p:FileVersion=$Version.$BuildRevision" "-p:InformationalVersion=$Version" -p:UsedAvaloniaProducts= -p:NuGetAudit=false -o $taskOutput -v:q
if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }
$taskFiles = @(Get-ChildItem -LiteralPath $taskOutput -Recurse -File)
if ($taskFiles.Count -ne 1 -or $taskFiles[0].Name -ne 'AchievementLabs.exe') { throw 'Release must contain exactly one AchievementLabs.exe.' }
$taskExe = $taskFiles[0]
$taskSigned = $false
if (-not [string]::IsNullOrWhiteSpace($SigningThumbprint)) {
    $taskThumbprint = $SigningThumbprint.Replace(' ', '').ToUpperInvariant()
    $taskCertificate = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object Thumbprint -eq $taskThumbprint |
        Select-Object -First 1
    if ($null -eq $taskCertificate -or -not $taskCertificate.HasPrivateKey) {
        throw "A code-signing certificate with private key was not found in Cert:\CurrentUser\My for thumbprint $taskThumbprint."
    }

    $taskSignTool = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
    if ([string]::IsNullOrWhiteSpace($taskSignTool)) {
        $taskSignTool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object FullName -Match '\\x64\\signtool\.exe$' |
            Sort-Object FullName -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }
    if ([string]::IsNullOrWhiteSpace($taskSignTool)) {
        throw 'signtool.exe was not found. Install the Windows SDK Signing Tools feature.'
    }

    & $taskSignTool sign /sha1 $taskThumbprint /fd SHA256 /td SHA256 /tr $TimestampUrl $taskExe.FullName
    if ($LASTEXITCODE -ne 0) { throw 'Authenticode signing failed.' }
    & $taskSignTool verify /pa /v $taskExe.FullName
    if ($LASTEXITCODE -ne 0) { throw 'Authenticode signature verification failed.' }
    $taskSigned = $true
}
$taskHash = (Get-FileHash -LiteralPath $taskExe.FullName -Algorithm SHA256).Hash
[ordered]@{ version="$Version.$BuildRevision"; bytes=$taskExe.Length; sha256=$taskHash.ToLowerInvariant() } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskWork 'update.json')
[ordered]@{ version=$Version; file=$taskExe.FullName; bytes=$taskExe.Length; sha256=$taskHash; selfContained=$true; privateEventsIncluded=$false; signed=$taskSigned; liveLicensingVerified=$false } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskWork 'release-manifest.json')
Write-Output "Release candidate: $($taskExe.FullName)"
Write-Output "SHA256: $taskHash"
