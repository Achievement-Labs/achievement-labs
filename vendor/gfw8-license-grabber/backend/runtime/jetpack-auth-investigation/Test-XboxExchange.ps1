param([Parameter(Mandatory=$true)][string]$ReceiptPath,[switch]$Send,[switch]$PackagedPayload,[switch]$UseNativeTicket,[string]$PrivateTicketPath,[string]$PrivatePayloadPath,[string]$ContainerFamily,[string]$WamScope='service::xboxlive.com::MBI',[string]$OutputPath,[string]$HelperPackageName='Local.ReceiptAuthProbe',[string]$ExpectedReceiptIdentity='HalfbrickStudiosPtyLtd.JetpackJoyride_w77bc8x1h5kya',[uint32]$TitleId=1496516562,[switch]$Reauthenticate,[switch]$PromptIfNeeded,[ValidateSet('DotNet','WinInet')][string]$Transport='DotNet',[switch]$RetainedIndependentTicket,[switch]$CompareStoreAccount,[ValidateSet('Jetpack2012','Win81','Win81Root')][string]$SdkProfile='Jetpack2012',[string]$HelperAddonPackageName,[string]$HelperAppId='App',[ValidateSet('none','d','t','whole')][string]$WamTicketFormat='none',[string]$PrivateTokenOutputDirectory,[string]$ExpectedOnlineIdSignInName)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../standalone-receipt-tool/FileHash.ps1')
trap {
 $failureError=$_
 # Preserve the original exception directly before nested stderr formatting.
 if($OutputPath){
  try{[IO.File]::WriteAllText((Join-Path (Split-Path ([IO.Path]::GetFullPath($OutputPath)) -Parent) 'local-error-private.log'),($failureError.Exception.ToString()+[Environment]::NewLine+$failureError.InvocationInfo.PositionMessage+[Environment]::NewLine+$failureError.ScriptStackTrace),(New-Object Text.UTF8Encoding($false)))}catch{}
 }
 $failure=[ordered]@{stage='local-failure';tokenIssued=$false;helperPackage=$HelperPackageName;errorType=$_.Exception.GetType().Name;helperStopped=[bool]$cleanupConfirmed;privateSessionRemoved=([bool]$cleanupConfirmed -and $local -and !(Test-Path $local))}
 if($_.Exception.Message -match 'ERROR native (?:helper|OnlineId) ([0-9a-fA-F]+)'){$failure.helperHresult='0x'+$Matches[1]}
 if($report -and $report.tokenIssued){
  $failure.stage='post-issuance-local-failure';$failure.tokenIssued=$true
  $failure.httpStatus=$report.httpStatus;$failure.nativeXboxJwt=$report.nativeXboxJwt
  $failure.privateTokenExported=$false
 }
 $json=$failure | ConvertTo-Json -Depth 4
 if($OutputPath){$json | Set-Content -LiteralPath $OutputPath}
 $json
 Write-Error $_.Exception.Message -ErrorAction Continue
 exit 1
}
if($UseNativeTicket -and (!$PackagedPayload -or $PrivateTicketPath -or $PrivatePayloadPath)){throw 'Independent OnlineId requires the packaged helper and no captured-control paths.'}
if($RetainedIndependentTicket -and (!$PrivateTicketPath -or $UseNativeTicket)){throw 'Retained helper ticket control requires its private file and no fresh-ticket flag.'}
if($Reauthenticate -and $PromptIfNeeded){throw 'Choose one prompt mode.'}
if($PromptIfNeeded -and !$UseNativeTicket){throw 'PromptIfNeeded requires independent OnlineId.'}
if($Reauthenticate -and !$UseNativeTicket){throw 'Reauthentication requires independent OnlineId.'}
if($PrivateTokenOutputDirectory -and (!$Send -or !$UseNativeTicket)){throw 'Private token export requires a fresh independent ticket and a real exchange.'}
if($PrivateTokenOutputDirectory -and (Test-Path -LiteralPath $PrivateTokenOutputDirectory)){throw 'Private token export directory already exists; refusing overwrite.'}
. "$PSScriptRoot/../standalone-receipt-tool/PublicWam.ps1"
if(!$UseNativeTicket -and !$PrivateTicketPath){Initialize-PublicWam}
Add-Type -AssemblyName System.Security
$sdkRelative='Microsoft.Xbox.dll';$expectedSdkHash='98DC11C38384A4A031B75C7BF0ECAF25AE95CEEE956AFCC6BB51BC337FBA3B66'
if($SdkProfile -in @('Win81','Win81Root')){
 if($SdkProfile -eq 'Win81'){$sdkRelative='sdk-win81\Microsoft.Xbox.dll'}else{$sdkRelative='Microsoft.Xbox.dll'};$expectedSdkHash='03CD055967732FA2C8E3C191F2326E33C70C0B2869B519982B627B77D115F742'
}
if($PackagedPayload -and !$HelperAddonPackageName){
 $selectedPackage=Get-AppxPackage -Name $HelperPackageName
 if(@($selectedPackage).Count -ne 1){throw 'Expected one registered helper package.'}
 $sdk=Join-Path $selectedPackage.InstallLocation $sdkRelative
}elseif($SdkProfile -in @('Win81','Win81Root')){
 $sdk=(Resolve-Path (Join-Path $PSScriptRoot 'adera-identity-probe/sdk-win81/Microsoft.Xbox.dll')).Path
}else{$sdk=(Resolve-Path "$PSScriptRoot/../jetpack-original/Microsoft.Xbox.dll").Path}
if((Get-ResearchFileHash $sdk -Algorithm SHA256).Hash -ne $expectedSdkHash){throw 'Unsupported Xbox SDK binary.'}
$receipt=[IO.File]::ReadAllText((Resolve-Path $ReceiptPath).Path)
$xml=New-Object Xml.XmlDocument; $xml.PreserveWhitespace=$true; $xml.XmlResolver=$null; $xml.LoadXml($receipt)
if($xml.DocumentElement.Name -ne 'Receipt' -or @($xml.SelectNodes('/Receipt/AppReceipt')).Count -ne 1){throw 'Expected exactly one receipt AppReceipt.'}
if($xml.Receipt.AppReceipt.AppId -ne $ExpectedReceiptIdentity){throw 'Wrong receipt identity.'}
if($xml.Receipt.AppReceipt.LicenseType -notin @('Full','Trial')){throw 'Unsupported receipt license type. Expected Full or Trial.'}
$cert=New-Object Security.Cryptography.X509Certificates.X509Certificate2((Resolve-Path "$PSScriptRoot/../standalone-receipt-tool/receipt-public.cer").Path)
if($cert.Thumbprint -ne 'B809E47CD0110A4DB043B3F73E83ACD917FE1336' -or $xml.Receipt.CertificateId -ne $cert.Thumbprint){throw 'Unsupported receipt signing certificate.'}
$signatures=@($xml.GetElementsByTagName('Signature','http://www.w3.org/2000/09/xmldsig#'))
if($signatures.Count -ne 1){throw 'Expected exactly one receipt signature.'}
$sig=New-Object Security.Cryptography.Xml.SignedXml($xml);$sig.LoadXml($signatures[0])
if($sig.SignedInfo.References.Count -ne 1 -or $sig.SignedInfo.References[0].Uri -ne '' -or !$sig.CheckSignature($cert,$true)){throw 'Receipt signature failed.'}
$ticket=$null;$nativeTicket=$null
if(!$UseNativeTicket -and !$PrivateTicketPath){
$provider=Wait-WamOperation ([Windows.Security.Authentication.Web.Core.WebAuthenticationCoreManager]::FindAccountProviderAsync('https://login.microsoft.com','consumers')) ([Windows.Security.Credentials.WebAccountProvider])
$req=[Windows.Security.Authentication.Web.Core.WebTokenRequest]::new($provider,$WamScope,'dbccc2ba-1cf6-48b8-bf7e-892d6ad6f6b3')
$result=Wait-WamOperation ([Windows.Security.Authentication.Web.Core.WebAuthenticationCoreManager]::GetTokenSilentlyAsync($req)) ([Windows.Security.Authentication.Web.Core.WebTokenRequestResult])
if($result.ResponseStatus.ToString() -ne 'Success'){throw ('WAM: '+$result.ResponseStatus)}
$ticket=$result.ResponseData[0].Token
Add-Type -AssemblyName System.Web
$parts=[Web.HttpUtility]::ParseQueryString($ticket)
if(!$parts['t']){throw 'Expected t/p compact ticket format.'}
$nativeTicket=[Uri]::UnescapeDataString(($ticket.Substring(2) -split '&p=',2)[0])
$nativeTicket=$nativeTicket.Trim()
if($WamTicketFormat -eq 'd'){$nativeTicket='d='+$nativeTicket}elseif($WamTicketFormat -eq 't'){$nativeTicket='t='+$nativeTicket}elseif($WamTicketFormat -eq 'whole'){$nativeTicket=$ticket}
}
if($PrivateTicketPath){$nativeTicket=[IO.File]::ReadAllText((Resolve-Path $PrivateTicketPath).Path)}
if($PrivatePayloadPath){$payload=[IO.File]::ReadAllText((Resolve-Path $PrivatePayloadPath).Path)}elseif($PackagedPayload){
 $pkg=Get-AppxPackage -Name $HelperPackageName
 if(@($pkg).Count -ne 1){throw 'Expected exactly one registered research helper.'}
 $codePackage=$pkg
 if($HelperAddonPackageName){
  $codePackage=Get-AppxPackage -Name $HelperAddonPackageName -PackageTypeFilter Optional
  if(@($codePackage).Count -ne 1 -or $pkg.Dependencies.PackageFullName -notcontains $codePackage.PackageFullName){throw 'Research optional helper is not linked to the selected game package.'}
 }
 $helperExe=Join-Path $codePackage.InstallLocation 'Probe.exe' 
 if((Get-ResearchFileHash (Join-Path $codePackage.InstallLocation $sdkRelative)).Hash -ne (Get-ResearchFileHash $sdk).Hash){throw 'Helper SDK hash mismatch.'}
 if((Get-ResearchFileHash $helperExe).Hash -ne 'E16ED76D2A83B9752EAE643A00723B1A3051C6C9EB69A58E53C8C73E528FDF72'){throw 'Helper executable must match current research build.'}
 if(Get-Process -Name Probe -ErrorAction SilentlyContinue){throw 'A probe is already running; finish that session first.'}
 $session=[guid]::NewGuid().ToString('N')
 $local=Join-Path $env:LOCALAPPDATA ('Packages\'+$pkg.PackageFamilyName+'\LocalState\sessions\'+$session)
 New-Item -ItemType Directory $local -ErrorAction Stop | Out-Null
 $inputFile=Join-Path $local input.txt;$outputFile=Join-Path $local output.txt
 $ticketFile=Join-Path $local ticket.txt
 $titleFile=Join-Path $local title.txt
 $helperProcess=$null;$cleanupConfirmed=$false
 $accountComparison=$null
 if($CompareStoreAccount){
  Initialize-PublicWam
  $storeProvider=Wait-WamOperation ([Windows.Security.Authentication.Web.Core.WebAuthenticationCoreManager]::FindAccountProviderAsync('https://login.microsoft.com','consumers')) ([Windows.Security.Credentials.WebAccountProvider])
  $storeRequest=[Windows.Security.Authentication.Web.Core.WebTokenRequest]::new($storeProvider,'service::www.microsoft.com::mbi_ssl','{f0c62012-2cef-4831-b1f7-930682874c86}')
  $storeResponse=Wait-WamOperation ([Windows.Security.Authentication.Web.Core.WebAuthenticationCoreManager]::GetTokenSilentlyAsync($storeRequest)) ([Windows.Security.Authentication.Web.Core.WebTokenRequestResult])
  if($storeResponse.ResponseStatus.ToString() -ne 'Success'){throw 'Diagnostic Store account query failed.'}
  $storeAccount=$storeResponse.ResponseData[0].WebAccount
  $storeResponse=$null
  [IO.File]::WriteAllText((Join-Path $local 'compare-account.txt'),'metadata only')
 }
 try {
  [IO.File]::WriteAllText($titleFile,[string]$TitleId)
  [IO.File]::WriteAllText((Join-Path $local 'profile.txt'),$SdkProfile)
  if($ExpectedOnlineIdSignInName){[IO.File]::WriteAllText((Join-Path $local 'expected-account.txt'),$ExpectedOnlineIdSignInName)}
  $inputTicket=if($UseNativeTicket){if($PromptIfNeeded){'NATIVE_ONLINEID_PROMPT_IF_NEEDED'}elseif($Reauthenticate){'NATIVE_ONLINEID_REAUTH'}else{'NATIVE_ONLINEID'}}else{$nativeTicket}
  $prefix=[Text.Encoding]::UTF8.GetBytes($inputTicket+"`n")
  $receiptBytes=[IO.File]::ReadAllBytes((Resolve-Path $ReceiptPath).Path)
  [IO.File]::WriteAllBytes($inputFile,([byte[]]($prefix+$receiptBytes)))
  Add-Type -Path (Join-Path $PSScriptRoot 'ActivateHelper.cs')
  $silentActivation=($UseNativeTicket -and !$Reauthenticate -and !$PromptIfNeeded)
  $helperPid=if($silentActivation){[ProbeActivation]::LaunchSilent(($pkg.PackageFamilyName+'!'+$HelperAppId),$session)}else{[ProbeActivation]::Launch(($pkg.PackageFamilyName+'!'+$HelperAppId),$session)}
  if(!$helperPid){throw 'Activation returned no helper process ID.'}
  try{$helperProcess=Get-Process -Id $helperPid -ErrorAction Stop}catch{$helperProcess=$null}
  $waitMs=if($Reauthenticate -or $PromptIfNeeded){300000}else{75000}
  if($helperProcess -and !$helperProcess.WaitForExit($waitMs)){throw 'Helper timeout. Session files retained until shutdown can be confirmed.'}
  $cleanupConfirmed=$true
  if($CompareStoreAccount){
   $accountMetaPath=Join-Path $local 'account-comparison.txt'
   if(Test-Path $accountMetaPath){
    $lines=[IO.File]::ReadAllLines($accountMetaPath)
    if($lines.Count -eq 3 -and $lines[0] -match '^(\d+) (\d+)$'){
     $nativeIdCharacters=[int]$Matches[1];$nativeNameCharacters=[int]$Matches[2]
     function Get-SessionHash([string]$Value){$sha=[Security.Cryptography.SHA256]::Create();try{return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($session+':'+$Value.ToLowerInvariant())))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}}
     $accountComparison=[ordered]@{scope='adjacent diagnostic Store WAM query, not receipt wire-account proof';nativeIdAvailable=($nativeIdCharacters -gt 0);nativeNameAvailable=($nativeNameCharacters -gt 0);accountIdMatches=($nativeIdCharacters -gt 0 -and $storeAccount.Id.Length -eq $nativeIdCharacters -and (Get-SessionHash $storeAccount.Id) -ceq $lines[1]);signInNameMatches=($nativeNameCharacters -gt 0 -and (Get-SessionHash $storeAccount.UserName) -ceq $lines[2])}
    }else{$accountComparison=[ordered]@{available=$false}}
   }
  }
  if(!(Test-Path $outputFile)){throw 'Helper exited without output.'}
  $payload=[IO.File]::ReadAllText($outputFile).Trim()
  if($payload.StartsWith('ERROR')){throw ('Packaged payload failed: '+$payload)}
  $contextMetadata=[IO.File]::ReadAllText((Join-Path $local 'context.txt'))
  if($contextMetadata -notmatch '^family=(\S+) appContainer=(\d+)'){throw 'Helper context metadata missing.'}
  $helperPackageFamilyObserved=$Matches[1];$helperAppContainer=($Matches[2] -eq '1')
  if($helperPackageFamilyObserved -ne $pkg.PackageFamilyName -or !$helperAppContainer){throw 'Helper did not run inside the selected package AppContainer.'}
  $metadata=[IO.File]::ReadAllText((Join-Path $local 'metadata.txt'))
  if($metadata -notmatch '^ticket=(\d+) receipt=(\d+) title=(\d+)') {throw 'Helper metadata missing.'}
  $helperReceiptCharacters=[int]$Matches[2];$helperTitle=[uint32]$Matches[3]
  if($helperReceiptCharacters -ne $receipt.Length -or $helperTitle -ne $TitleId){throw 'Helper receipt decoding or title configuration mismatch.'}
  if(!$payload -or $payload -notmatch '^[A-Za-z0-9+/=]+$'){throw 'Invalid protected payload output.'}
  if($UseNativeTicket){
   $ui=[IO.File]::ReadAllText((Join-Path $local 'ui.txt'))
   if($ui -notmatch '^prompt=(\w+) visibleBefore=(0|1)\r?\nvisibleAfter=(0|1)'){throw 'Helper UI verification metadata missing.'}
   $helperUi=[ordered]@{activationMode=$(if($silentActivation){'prelaunch'}else{'normal'});promptMode=$Matches[1];windowVisibleBefore=($Matches[2] -eq '1');windowVisibleAfter=($Matches[3] -eq '1')}
   $expectedPrompt=if($PromptIfNeeded){'PromptIfNeeded'}elseif($Reauthenticate){'RetypeCredentials'}else{'DoNotPrompt'}
   if($helperUi.promptMode -ne $expectedPrompt){throw 'Helper prompt mode differs from requested mode.'}
   if($silentActivation -and ($helperUi.promptMode -ne 'DoNotPrompt' -or $helperUi.windowVisibleBefore -or $helperUi.windowVisibleAfter)){throw 'Normal helper acquisition was not silent.'}
   $helperAccountStatus=[ordered]@{}
   foreach($line in [IO.File]::ReadAllLines((Join-Path $local 'account-status.txt'))){
    if($line -match '^(canSignOut|signInNameAvailable|matchesExpected)=(0|1)$'){$helperAccountStatus[$Matches[1]]=($Matches[2] -eq '1')}
    elseif($line -match '^(canSignOutError|signInNameError)=([0-9a-fA-F]+)$'){$helperAccountStatus[$Matches[1]]='0x'+$Matches[2]}
   }
   $nativeTicket=[IO.File]::ReadAllText($ticketFile)
   if($nativeTicket -notmatch '^d=[A-Za-z0-9+/=]+$'){throw 'Invalid native delegation ticket framing.'}
   $ticketShape=[ordered]@{delegationPrefix=$true;containsWhitespace=[bool]($nativeTicket -match '\s')}
  }
 } finally {
  if($cleanupConfirmed){
   Remove-Item -LiteralPath $inputFile,$outputFile,$ticketFile,$titleFile,(Join-Path $local metadata.txt),(Join-Path $local 'context.txt'),(Join-Path $local 'profile.txt'),(Join-Path $local 'compare-account.txt'),(Join-Path $local 'account-comparison.txt'),(Join-Path $local 'ui.txt') -Force -ErrorAction SilentlyContinue
   Remove-Item -LiteralPath (Join-Path $local 'expected-account.txt'),(Join-Path $local 'account-status.txt') -Force -ErrorAction SilentlyContinue
   if(@(Get-ChildItem -LiteralPath $local -Force).Count -eq 0){Remove-Item -LiteralPath $local -Force}
   if(Test-Path $local){throw 'Private session cleanup incomplete.'}
  }
 }
}else{
$dep=Get-AppxPackage Microsoft.VCLibs.110.00 | Where-Object {$_.Architecture -eq 'X86'} | Select-Object -First 1
if(!$dep){throw 'VC110 x86 runtime not installed.'}
$start=New-Object Diagnostics.ProcessStartInfo
$start.FileName=Join-Path $PSScriptRoot BuildAuthPayload.exe
$start.Arguments='"'+$sdk+'" "'+$dep.InstallLocation+'" '+$TitleId
if($ContainerFamily){
 $dir=Join-Path $PSScriptRoot probe-package
 $start.FileName=Join-Path $PSScriptRoot RunInContainer.exe
 $start.Arguments='"'+$ContainerFamily+'" "'+$dir+'\BuildAuthPayload.exe" "'+$dir+'\Microsoft.Xbox.dll" "'+$dir+'" '+$TitleId
}
$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
$process=[Diagnostics.Process]::Start($start)
$process.StandardInput.WriteLine($nativeTicket);$process.StandardInput.WriteLine($receipt);$process.StandardInput.Close()
$read=$process.StandardOutput.ReadToEndAsync()
$readErr=$process.StandardError.ReadToEndAsync()
if(!$process.WaitForExit(20000)){ $process.Kill();throw ('Payload builder timeout: '+$readErr.GetAwaiter().GetResult()) }
$payload=$read.GetAwaiter().GetResult().Trim()
if($process.ExitCode -ne 0 -or $payload.StartsWith('ERROR')){throw ('Payload builder failed: '+$payload+'; stages: '+$readErr.GetAwaiter().GetResult())}
}
$report=[ordered]@{sdkProfile=$SdkProfile;sdkSha256=$expectedSdkHash;helperPackage=$HelperPackageName;helperAddonPackage=$HelperAddonPackageName;helperAppId=$HelperAppId;helperPackageFamilyObserved=$helperPackageFamilyObserved;helperAppContainer=$helperAppContainer;receiptIdentity=$ExpectedReceiptIdentity;receiptLicenseType=[string]$xml.Receipt.AppReceipt.LicenseType;titleId=$TitleId;wamTicketFormat=$WamTicketFormat;ticketSource=$(if($UseNativeTicket){'independent-onlineid'}elseif($RetainedIndependentTicket){'retained-helper-ticket-control'}elseif($PrivateTicketPath){'captured-native-control'}else{'exploratory-wam'});stage='payload-built';ticketShape=$ticketShape;accountComparison=$accountComparison;ticketCharacters=$nativeTicket.Length;receiptCharacters=$receipt.Length;payloadCharacters=$payload.Length;gameRunning=[bool](Get-Process JetpackJoyride_Win8,HexicApp,Adera -ErrorAction SilentlyContinue);helperStopped=$cleanupConfirmed;privateSessionRemoved=($PackagedPayload -and !(Test-Path $local));receiptSha256=(Get-ResearchFileHash $ReceiptPath).Hash;tokenIssued=$false}
if($UseNativeTicket){$report.helperUi=$helperUi;$report.helperAccountStatus=$helperAccountStatus}
if($Send){
 $context=[IO.File]::ReadAllText((Join-Path $PSScriptRoot context-template.xml)).Replace('{WindowsLiveTicket}',[Security.SecurityElement]::Escape($nativeTicket)).Replace('{AuthPayload}',[Security.SecurityElement]::Escape($payload))
 $soap=[IO.File]::ReadAllText((Join-Path $PSScriptRoot soap-template.xml)).Replace('{MessageId}','urn:uuid:'+([guid]::NewGuid().ToString())).Replace('{UrlAuthAppliesTo}','http://xboxlive.com').Replace('{TokenType}','http://oauth.net/grant_type/xjwt/1.0/bearer').Replace('{NewAuthAdditionalContext}',$context)
 $report.transport=$Transport
 if($Transport -eq 'WinInet'){
  Add-Type -Path (Join-Path $PSScriptRoot 'WinInetExchange.cs')
  $wire=[WinInetExchange]::Post($nativeTicket,$soap)
  $report.stage='xbox-response';$report.httpStatus=$wire.Status;$report.xErr=$wire.XErr;$body=$wire.Body
 }else{
 $web=[Net.HttpWebRequest]::Create('https://auth.xboxlive.com/XSts/xsts.svc/IWSTrust13');$web.Proxy=$null;$web.AllowAutoRedirect=$false;$web.Method='POST';$web.ContentType='application/soap+xml; charset=utf-8';$web.Timeout=30000
 $web.Headers['Authorization']='WLID1.0 '+$nativeTicket
 $bytes=[Text.Encoding]::UTF8.GetBytes($soap);$web.ContentLength=$bytes.Length;$stream=$web.GetRequestStream();$stream.Write($bytes,0,$bytes.Length);$stream.Close()
 try{$response=$web.GetResponse()}catch [Net.WebException]{if(!$_.Exception.Response){throw};$response=$_.Exception.Response}
 $report.stage='xbox-response';$report.httpStatus=[int]$response.StatusCode;$report.tlsSubject=$web.ServicePoint.Certificate.Subject;$report.tlsIssuer=$web.ServicePoint.Certificate.Issuer;$report.xErr=$response.Headers['X-Err'];$report.server=$response.Headers['Server'];$report.contentType=$response.ContentType;$reader=New-Object IO.StreamReader($response.GetResponseStream());$body=$reader.ReadToEnd();$reader.Close();$response.Close()
 }
 if([string]::IsNullOrWhiteSpace($body)){
  $report.responseCharacters=$body.Length
  $json=$report|ConvertTo-Json -Depth 5;if($OutputPath){$json|Set-Content -LiteralPath $OutputPath};$json;exit 1
 }
 $rx=New-Object Xml.XmlDocument;$rx.XmlResolver=$null;$rx.LoadXml($body)
 $report.soapFault=($rx.SelectNodes('//*[local-name()="Fault"]').Count -gt 0)
 $report.faultText=@($rx.SelectNodes('//*[local-name()="Fault"]//*[local-name()="Text" or local-name()="Value"]') | ForEach-Object { $_.InnerText -replace '\b\d{14,20}\b','REDACTED_ID' })
 $issued=@($rx.SelectNodes('//*[local-name()="RequestedSecurityToken"]/*'))
 $report.tokenIssued=($issued.Count -gt 0 -and !$report.soapFault -and $report.httpStatus -eq 200)
 if($report.tokenIssued){
  $node=$issued[0]
  if($node.LocalName -ne 'jwt' -or $node.GetAttribute('v') -notmatch '^\d+\.\d+$' -or !$node.HasAttribute('t')){throw 'Unexpected native Xbox token XML format.'}
  $compact=$node.GetAttribute('t')
  if($compact.Length -lt 100 -or $compact -match '[\x00-\x20]'){throw 'Empty or malformed native Xbox token.'}
  $report.nativeXboxJwt=$true;$report.jwtVersion=$node.GetAttribute('v');$report.jwtCharacters=$compact.Length
  $report.jwtCompact=($compact -match '^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$')
  if($report.jwtCompact){
   $headerPart=($compact -split '\.')[0].Replace('-','+').Replace('_','/')
   $headerPart=$headerPart.PadRight($headerPart.Length+((4-$headerPart.Length%4)%4),'=')
   $header=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($headerPart)) | ConvertFrom-Json
   $report.jwtAlgorithm=$header.alg
  }
  $report.jwtSignatureLocallyVerified=$false

 }
 $report.responseCharacters=$body.Length
 $report.tokenTypes=@($rx.SelectNodes('//*[local-name()="TokenType"]') | ForEach-Object {$_.InnerText})
 $report.expires=@($rx.SelectNodes('//*[local-name()="Lifetime"]/*[local-name()="Expires"]') | ForEach-Object {$_.InnerText})
 if($PrivateTokenOutputDirectory -and $report.tokenIssued){
  . (Join-Path $PSScriptRoot '../standalone-receipt-tool/PrivateTokenExport.ps1')
  $privateExport=Export-PrivateXboxToken -Directory $PrivateTokenOutputDirectory -ResponseXml $body -IssuedToken $node -Metadata $report
  $report.privateTokenExported=$true
 }
}
$json=$report | ConvertTo-Json -Depth 5
if($OutputPath){$json | Set-Content -LiteralPath $OutputPath}
$json
if($Send -and !$report.tokenIssued){exit 1}
# Default: credentials remain in memory after transient helper-session cleanup.
# Explicit private export retains only the issued token/response, not the input ticket/payload.



