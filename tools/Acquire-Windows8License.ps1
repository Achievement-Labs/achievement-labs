param(
 [Parameter(Mandatory=$true)][ValidatePattern('^\d+$')][string]$TitleId,
 [Parameter(Mandatory=$true)][string]$DataDirectory,
 [Parameter(Mandatory=$true)][ValidatePattern('^[a-f0-9]{32}$')][string]$RequestId,
 [string]$ReceiptPath,
 [switch]$OfflineCheck
)
$ErrorActionPreference='Stop'
$source=Join-Path $PSScriptRoot 'Windows8License'
. (Join-Path $source 'Core.ps1')
$games=(Get-Content -LiteralPath (Join-Path $source 'games.json') -Raw|ConvertFrom-Json).games
$matches=@($games|Where-Object {[string]$_.legacy_xbox_title_id -eq $TitleId})
if($OfflineCheck){
 if($matches.Count -ne 1){throw 'Expected exactly one title route.'}
 . (Join-Path $source 'runtime\jetpack-auth-investigation\TemporaryAuthHelper.ps1')
 $config=Get-TemporaryAuthConfig $matches[0].index
 if([string]$config.titleId -ne $TitleId -or $config.family -ne $matches[0].package_family_name){throw 'Title route mismatch.'}
 $inventory=Get-Content -LiteralPath (Join-Path $script:SharedHelperRoot 'payload-inventory.json') -Raw|ConvertFrom-Json
 foreach($file in $inventory){if((Get-ResearchFileHash (Join-Path $script:SharedHelperRoot $file.path)).Hash -ne $file.sha256){throw 'Runtime hash mismatch.'}}
 Write-Output 'PASS: exact title route and pinned helper payload; no Store/Xbox calls or package changes.'
 exit 0
}
$licenseRoot=[IO.Path]::GetFullPath($DataDirectory)
if(!(Test-Path -LiteralPath $licenseRoot)){$null=New-PrivateFolder $licenseRoot}
$acl=[IO.Directory]::GetAccessControl($licenseRoot)
$owner=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if(!$acl.AreAccessRulesProtected){throw 'License directory must have private permissions.'}
foreach($rule in $acl.Access){if($rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin @($owner,'S-1-5-18')){throw 'License directory grants access to another account.'}}
$resultPath=Join-Path $licenseRoot ($RequestId+'.json')
$result=[ordered]@{success=$false;titleId=$TitleId;message='License/token acquisition did not finish.';receiptPath=$null;tokenPath=$null;expiresUtc=$null;dataPreservationAlert=$false}
$mutex=New-Object Threading.Mutex($false,('Local\GFW8LicenseGrabber-'+[Security.Principal.WindowsIdentity]::GetCurrent().User.Value))
$locked=$false
try{
 try{$locked=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$locked=$true}
 if(!$locked){$result.message='Another license/token operation is running. Wait for it to finish.';throw $result.message}
 if($matches.Count -ne 1){$result.message='No unique license/token route is available for this title.';throw $result.message}
 $game=$matches[0]
 $runtimeDirectory=Join-Path $licenseRoot 'runtime-2'
 if(!(Test-Path -LiteralPath $runtimeDirectory)){
  $stage=New-PrivateFolder (Join-Path $licenseRoot ('staging-'+$RequestId))
  Copy-Item -Path (Join-Path $source '*') -Destination $stage -Recurse
  $stageFull=[IO.Path]::GetFullPath($stage);$runtimeFull=[IO.Path]::GetFullPath($runtimeDirectory)
  if(!$stageFull.StartsWith($licenseRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or !$runtimeFull.StartsWith($licenseRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Runtime staging path is outside the private workspace.'}
  Move-Item -LiteralPath $stageFull -Destination $runtimeFull
 }
 . (Join-Path $runtimeDirectory 'Core.ps1')
 $script:DataRoot=Join-Path $licenseRoot 'runs'
 if(!(Test-Path -LiteralPath $script:DataRoot)){$null=New-PrivateFolder $script:DataRoot}
 $run=New-PoCRun $game.index
 if($ReceiptPath){
  $sourceReceipt=(Read-Receipt $ReceiptPath $game)
  if(!$sourceReceipt.Valid){throw 'The selected pop.bin is expired or not yet valid.'}
  $privateReceipt=Join-Path $run 'pop.bin'
  [IO.File]::WriteAllBytes($privateReceipt,$sourceReceipt.Bytes)
  $receipt=Read-Receipt $privateReceipt $game
  if($receipt.Hash -ne $sourceReceipt.Hash){throw 'The selected pop.bin changed while it was copied.'}
 }else{
  $result.message='Receipt acquisition failed. Check Windows Microsoft-account sign-in and the private diagnostics.'
  $receipt=Request-GameReceipt $game $run
  # Fresh service receipts are checked again against the pinned Microsoft certificate before authentication.
  $receipt=Read-Receipt $receipt.Path $game
 }
 $result.receiptPath=$receipt.Path
 $result.message='Receipt saved. Token acquisition failed; see the private diagnostics.'
 . (Join-Path $script:Runtime 'jetpack-auth-investigation\TemporaryAuthHelper.ps1')
 $config=Get-TemporaryAuthConfig $game.index
 if(Get-AppxPackage -Name $config.name){$result.message='Receipt saved. Token acquisition requires this game to be uninstalled; its installation was left unchanged.';throw $result.message}
 try{$token=Request-GameToken $game $receipt $run}
 catch{
  if(!$_.Exception.Data['RequiresReauthentication']){throw}
  $run=New-PoCRun $game.index
  $token=Request-GameToken $game $receipt $run -PromptIfNeeded
 }
 $result.tokenPath=Join-Path $token.Export 'issued-token.xml'
 $result.expiresUtc=$token.Window.Expires.ToString('o')
 $result.dataPreservationAlert=[bool]$token.Report.dataPreservationAlert
 $result.success=$true
 $result.message='License and Xbox token acquired.'
}catch{
 if($_.Exception.Message -like '*STORE_ERROR_UNLICENSED_USER*'){$result.message='No license for this edition on the Windows Microsoft account. No token requested.'}
 [IO.File]::WriteAllText((Join-Path $licenseRoot ($RequestId+'-error-private.log')),$_.Exception.ToString())
}finally{
 if($locked){$mutex.ReleaseMutex()};$mutex.Dispose()
 $result|ConvertTo-Json|Set-Content -LiteralPath $resultPath -Encoding UTF8
}
if(!$result.success){exit 1}
