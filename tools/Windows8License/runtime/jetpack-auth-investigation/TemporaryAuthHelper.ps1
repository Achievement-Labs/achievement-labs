. (Join-Path $PSScriptRoot "NativeProcessCapture.ps1")
. "$PSScriptRoot/../standalone-receipt-tool/FileHash.ps1"
$script:SharedHelperRoot=Join-Path $PSScriptRoot 'shared-auth-helper'
$script:SharedAuthResearchRoot=$PSScriptRoot
function Get-TemporaryAuthConfig([int]$GameId){
 $all=Get-Content -LiteralPath (Join-Path $script:SharedHelperRoot 'identities.json') -Raw | ConvertFrom-Json
 $selected=@($all | Where-Object {$_.gameId -eq $GameId})
 if($selected.Count -ne 1){throw 'No verified temporary-helper configuration for this game.'}
 return $selected[0]
}
function Get-TemporaryAuthReceiptIdentity([int]$GameId,[string]$ReceiptPath,$Config){
 # Store receipts can use either this edition's PFN or its legacy Store GUID.
 # Preserve the signed XML identity; do not rewrite it to match the helper PFN.
 $catalogue=Get-Content -LiteralPath (Join-Path $script:SharedAuthResearchRoot '..\..\games.json') -Raw|ConvertFrom-Json
 $games=@($catalogue.games|Where-Object {[int]$_.index -eq $GameId})
 if($games.Count -ne 1 -or $games[0].package_family_name -ine $Config.family){throw 'Receipt catalogue and token helper edition do not match.'}
 $path=[IO.Path]::GetFullPath($ReceiptPath)
 if((Get-Item -LiteralPath $path).Length -gt 1048576){throw 'Receipt too large.'}
 $settings=New-Object Xml.XmlReaderSettings;$settings.DtdProcessing='Prohibit';$settings.XmlResolver=$null;$settings.MaxCharactersInDocument=1048576
 $reader=[Xml.XmlReader]::Create($path,$settings);$xml=New-Object Xml.XmlDocument;$xml.XmlResolver=$null
 try{$xml.Load($reader)}finally{$reader.Dispose()}
 $apps=@($xml.SelectNodes('/Receipt/AppReceipt'))
 if($apps.Count -ne 1){throw 'Expected exactly one receipt AppReceipt.'}
 $identity=$apps[0].GetAttribute('AppId')
 $allowed=@([string]$games[0].package_family_name,[string]$games[0].legacy_store_guid)|Where-Object {$_}
 if(!$identity -or $identity -notin $allowed){throw 'Receipt belongs to a different game/edition.'}
 return $identity
}
function Get-TemporaryAuthRegistrations {
 $known=@((Get-Content -LiteralPath (Join-Path $script:SharedHelperRoot 'identities.json') -Raw | ConvertFrom-Json).name)
 Get-AppxPackage | Where-Object {$_.Name -in $known -and $_.IsDevelopmentMode -and $_.InstallLocation.TrimEnd('\') -eq $script:SharedHelperRoot.TrimEnd('\')}
}
function Test-TemporaryAuthReady([int]$GameId){
 $c=Get-TemporaryAuthConfig $GameId
 $inventory=Get-Content -LiteralPath (Join-Path $script:SharedHelperRoot 'payload-inventory.json') -Raw | ConvertFrom-Json
 foreach($file in $inventory){if((Get-ResearchFileHash (Join-Path $script:SharedHelperRoot $file.path)).Hash -ne $file.sha256){throw 'Shared payload file hash mismatch.'}}
 foreach($p in @(Get-AppxPackage -Name $c.name)){
  if(!$p.IsDevelopmentMode -or $p.InstallLocation.TrimEnd('\') -ne $script:SharedHelperRoot.TrimEnd('\')){throw 'A real game or another helper occupies a target identity. No package will be replaced.'}
 }
 return $c
}
function Get-TemporaryAuthDataSnapshot($Config){
 $data=Join-Path $env:LOCALAPPDATA ('Packages\'+$Config.family)
 if(!(Test-Path -LiteralPath $data)){return}
 foreach($file in @(Get-ChildItem -LiteralPath $data -File -Recurse -Force -ErrorAction Stop)){
  if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Unexpected reparse file in existing data.'}
  [pscustomobject]@{path=$file.FullName;sha256=(Get-ResearchFileHash $file.FullName).Hash;pop=($file.Name -ieq 'pop.bin')}
 }
}
function Assert-TemporaryAuthDataUnchanged($Snapshot){
 foreach($file in $Snapshot){if(!(Test-Path -LiteralPath $file.path) -or (Get-ResearchFileHash $file.path).Hash -ne $file.sha256){throw 'Existing app-data changed; stop and inspect the preserved backup.'}}
}
function Remove-VerifiedTemporaryAuthRegistration($Package){
 if(!$Package.IsDevelopmentMode -or $Package.InstallLocation.TrimEnd('\') -ne $script:SharedHelperRoot.TrimEnd('\')){throw 'Refusing to unregister a package outside the shared helper workspace.'}
 if(Get-Process Probe -ErrorAction SilentlyContinue){throw 'A helper is still running; registration retained until exit is confirmed.'}
 Remove-AppxPackage -Package $Package.PackageFullName -PreserveApplicationData -ErrorAction Stop
 if(Get-AppxPackage -Name $Package.Name){throw 'Temporary registration remains after removal.'}
}
function Invoke-TemporaryXboxAuth {
 param([Parameter(Mandatory=$true)][int]$GameId,[Parameter(Mandatory=$true)][string]$ReceiptPath,[Parameter(Mandatory=$true)][string]$OutputPath,[switch]$Reauthenticate,[switch]$PromptIfNeeded,[string]$PrivateTokenOutputDirectory,[switch]$InjectFailureAfterRegistration)
 $c=Test-TemporaryAuthReady $GameId
 $receiptIdentity=Get-TemporaryAuthReceiptIdentity $GameId $ReceiptPath $c
 if(Get-Process Probe,Adera,HexicApp,JetpackJoyride_Win8 -ErrorAction SilentlyContinue){throw 'Close games/helpers before temporary registration.'}
 $lock=$null;$snapshot=@();$registered=$false;$failure=$null;$cleanupFailure=$null;$dataFailure=$null;$report=$null
 $parent=Split-Path ([IO.Path]::GetFullPath($OutputPath)) -Parent
 if(!(Test-Path -LiteralPath $parent)){New-Item -ItemType Directory -Path $parent -Force | Out-Null}
 $life=[ordered]@{gameId=$GameId;package=$c.name;sharedHelperReused=$true;registeredBefore=0;registeredDuring=0;registeredAfter=$null;registrationRemoved=$false;staleSharedRegistrationsRecovered=0;existingDataFilesVerified=0;existingPopFilesVerified=0;dataPreservationChecked=$false;dataPreservationVerified=$false;receiptRefreshed=$false;injectedFailure=[bool]$InjectFailureAfterRegistration;exchangeAttempted=$false}
 try{
  # A file lock serializes all identities that use this shared manifest/executable.
  $lock=New-Object IO.FileStream((Join-Path $script:SharedHelperRoot 'registration.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
  foreach($stale in @(Get-TemporaryAuthRegistrations)){
   $staleConfig=(Get-Content -LiteralPath (Join-Path $script:SharedHelperRoot 'identities.json') -Raw | ConvertFrom-Json) | Where-Object {$_.name -eq $stale.Name}
   $staleData=@(Get-TemporaryAuthDataSnapshot $staleConfig)
   Remove-VerifiedTemporaryAuthRegistration $stale
   Assert-TemporaryAuthDataUnchanged $staleData
   $life.staleSharedRegistrationsRecovered++
  }
  $life.registeredBefore=@(Get-TemporaryAuthRegistrations).Count
  if($life.registeredBefore -ne 0){throw 'Another target identity remains registered.'}
  $snapshot=@(Get-TemporaryAuthDataSnapshot $c)
  $template=New-Object Xml.XmlDocument;$template.PreserveWhitespace=$true
  $template.Load((Join-Path $script:SharedHelperRoot 'manifest-template.xml'))
  $template.Package.Identity.SetAttribute('Name',$c.name);$template.Package.Identity.SetAttribute('Publisher',$c.publisher);$template.Package.Identity.SetAttribute('Version',$c.version)
  $template.Package.Properties.DisplayName='Temporary Xbox Authentication Helper'
  $manifest=Join-Path $script:SharedHelperRoot 'AppxManifest.xml';$template.Save($manifest)
  Add-AppxPackage -Register $manifest -ErrorAction Stop
  $registered=$true
  $p=Get-AppxPackage -Name $c.name
  if(!$p -or !$p.IsDevelopmentMode -or $p.InstallLocation.TrimEnd('\') -ne $script:SharedHelperRoot.TrimEnd('\') -or [string]$p.Version -ne $c.version -or $p.PackageFamilyName -ne $c.family){throw 'Temporary package identity verification failed.'}
  $life.registeredDuring=@(Get-TemporaryAuthRegistrations).Count
  if($life.registeredDuring -ne 1){throw 'Expected only one temporary registered identity.'}
  if($InjectFailureAfterRegistration){throw 'Injected failure after registration; no helper activation or network exchange.'}
  $args=@('-NoProfile','-MTA','-ExecutionPolicy','Bypass','-File',(Join-Path $script:SharedAuthResearchRoot 'Test-XboxExchange.ps1'),'-ReceiptPath',[IO.Path]::GetFullPath($ReceiptPath),'-PackagedPayload','-UseNativeTicket','-HelperPackageName',$c.name,'-HelperAppId','App','-ExpectedReceiptIdentity',$receiptIdentity,'-TitleId',[string]$c.titleId,'-SdkProfile',$c.sdkProfile,'-Send','-OutputPath',[IO.Path]::GetFullPath($OutputPath))
  if($Reauthenticate){$args+='-Reauthenticate'}
  if($PromptIfNeeded){$args+='-PromptIfNeeded'}
  if($PrivateTokenOutputDirectory){$args+=@('-PrivateTokenOutputDirectory',$PrivateTokenOutputDirectory)}
  $capture=Invoke-CapturedAuthDriver (Join-Path $PSHOME 'powershell.exe') $args (Join-Path $parent 'driver-metadata.log')
  $driverExit=$capture.ExitCode
  if(Test-Path -LiteralPath $OutputPath){$report=Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json;$life.exchangeAttempted=($report.stage -in @('xbox-response','post-issuance-local-failure'))}
  if(!$report.helperStopped -or !$report.privateSessionRemoved){throw 'The authentication helper did not confirm shutdown and session cleanup.'}
  if(!$report.tokenIssued -or !$report.nativeXboxJwt){
   if($report.stage -eq 'xbox-response' -and $report.xErr){throw ('Xbox rejected the token request ('+[string]$report.xErr+').')}
   throw 'The token request did not produce a usable Xbox token. See private diagnostics.'
  }
  if($driverExit -ne 0){throw 'The token worker reported a local failure. See private diagnostics.'}
 }catch{$failure=$_}
 finally{
  if($lock){
   try{
    $remaining=Get-AppxPackage -Name $c.name
    if($remaining -and $remaining.InstallLocation.TrimEnd('\') -eq $script:SharedHelperRoot.TrimEnd('\')){Remove-VerifiedTemporaryAuthRegistration $remaining}
    $life.registeredAfter=@(Get-TemporaryAuthRegistrations).Count
    $life.registrationRemoved=($life.registeredAfter -eq 0)
    if(!$life.registrationRemoved){throw 'A temporary identity is still registered.'}
   }catch{$cleanupFailure=$_}
   if($life.registrationRemoved){
    try{
     $life.dataPreservationChecked=$true;Assert-TemporaryAuthDataUnchanged $snapshot;$life.dataPreservationVerified=$true
     $life.existingDataFilesVerified=$snapshot.Count;$life.existingPopFilesVerified=@($snapshot | Where-Object {$_.pop}).Count
    }catch{$dataFailure=$_}
   }
   $lock.Dispose()
  }
  if($failure){$life.failureType=$failure.Exception.GetType().Name}
  if($cleanupFailure){$life.cleanupFailureType=$cleanupFailure.Exception.GetType().Name}
  if($dataFailure){$life.dataPreservationFailureType=$dataFailure.Exception.GetType().Name}
  $life | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $parent 'temporary-helper.json')
  if($report){
   $report | Add-Member -NotePropertyName helperRegistrationRemoved -NotePropertyValue $life.registrationRemoved -Force
   $report | Add-Member -NotePropertyName sharedHelperReused -NotePropertyValue $true -Force
   $report | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $OutputPath
  }
 }
 if($cleanupFailure){throw $cleanupFailure}
 if($dataFailure){throw $dataFailure}
 if($failure){throw $failure}
 return [pscustomobject]$life
}
