$ErrorActionPreference='Stop'
$script:Root=$PSScriptRoot
$script:DataRoot=Join-Path $env:LOCALAPPDATA 'GFW8LicenseGrabber'
$script:Runtime=Join-Path $PSScriptRoot 'runtime'
. (Join-Path $script:Runtime 'standalone-receipt-tool\FileHash.ps1')
. (Join-Path $script:Runtime 'standalone-receipt-tool\PublicWam.ps1')
Add-Type -AssemblyName System.Security
Add-Type -AssemblyName System.Web.Extensions
$script:Json=New-Object Web.Script.Serialization.JavaScriptSerializer
$script:Json.MaxJsonLength=2097152
function New-PrivateFolder([string]$Path){
 $full=[IO.Path]::GetFullPath($Path)
 if(Test-Path -LiteralPath $full){throw 'Private session folder already exists.'}
 $null=[IO.Directory]::CreateDirectory($full)
 $owner=[Security.Principal.WindowsIdentity]::GetCurrent().User
 $acl=New-Object Security.AccessControl.DirectorySecurity
 $acl.SetOwner($owner);$acl.SetAccessRuleProtection($true,$false)
 foreach($sid in @($owner,(New-Object Security.Principal.SecurityIdentifier('S-1-5-18')))){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')))}
 [IO.Directory]::SetAccessControl($full,$acl)
 $actual=[IO.Directory]::GetAccessControl($full)
 if(!$actual.AreAccessRulesProtected){throw 'Private folder permissions could not be verified.'}
 foreach($rule in $actual.Access){if($rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin @($owner.Value,'S-1-5-18')){throw 'Unexpected private folder access rule.'}}
 return $full
}
function New-PoCRun([int]$GameId){
 New-PrivateFolder (Join-Path $script:DataRoot ('Game-'+$GameId+'-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)))
}
function Format-Time([DateTimeOffset]$Value){$Value.ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss zzz')+' (UTC '+$Value.UtcDateTime.ToString('yyyy-MM-dd HH:mm:ss')+')'}
function Get-ReceiptWindow([DateTimeOffset]$Minted,[DateTimeOffset]$Now=[DateTimeOffset]::UtcNow){
 $expires=$Minted.AddHours(24)
 [pscustomobject]@{Minted=$Minted;Expires=$expires;Valid=($Minted -le $Now.AddMinutes(1) -and $Now -lt $expires);Future=($Minted -gt $Now.AddMinutes(1))}
}
function Read-Receipt([string]$Path,$Game,[switch]$SkipSignature){
 $bytes=[IO.File]::ReadAllBytes([IO.Path]::GetFullPath($Path))
 if($bytes.Length -gt 1048576){throw 'Receipt too large.'}
 $settings=New-Object Xml.XmlReaderSettings
 $settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$settings.XmlResolver=$null;$settings.MaxCharactersInDocument=1048576
 $memory=New-Object IO.MemoryStream(,$bytes);$reader=[Xml.XmlReader]::Create($memory,$settings)
 $doc=New-Object Xml.XmlDocument;$doc.PreserveWhitespace=$true;$doc.XmlResolver=$null
 try{$doc.Load($reader)}finally{$reader.Dispose();$memory.Dispose()}
 if($doc.DocumentElement.Name -ne 'Receipt' -or @($doc.SelectNodes('/Receipt/AppReceipt')).Count -ne 1){throw 'Expected exactly one signed AppReceipt.'}
 $app=$doc.SelectSingleNode('/Receipt/AppReceipt');$appId=$app.GetAttribute('AppId')
 if($appId -ine $Game.package_family_name -and $appId -ine $Game.legacy_store_guid){throw 'Receipt belongs to a different game/edition.'}
 $licenseType=$app.GetAttribute('LicenseType')
 if($licenseType -notin @('Full','Trial')){throw 'Unsupported receipt license type. Expected Full or Trial.'}
 $hash=(Get-ResearchFileHash $Path).Hash
 if($script:TrustedReceiptHash -and $script:TrustedReceiptHash -ieq $hash){$SkipSignature=$true}
 if(!$SkipSignature){
 $cert=New-Object Security.Cryptography.X509Certificates.X509Certificate2((Join-Path $script:Runtime 'standalone-receipt-tool\receipt-public.cer'))
 if($cert.Thumbprint -ine 'B809E47CD0110A4DB043B3F73E83ACD917FE1336' -or $doc.DocumentElement.GetAttribute('CertificateId') -ine $cert.Thumbprint){throw 'Receipt uses an unknown signing certificate.'}
 $signatures=@($doc.GetElementsByTagName('Signature','http://www.w3.org/2000/09/xmldsig#'))
 if($signatures.Count -ne 1){throw 'Expected one receipt signature.'}
 $signed=New-Object Security.Cryptography.Xml.SignedXml($doc);$signed.LoadXml($signatures[0])
 if($signed.SignedInfo.References.Count -ne 1 -or $signed.SignedInfo.References[0].Uri -ne '' -or !$signed.CheckSignature($cert,$true)){throw 'Receipt signature invalid.'}
 }
 $minted=[DateTimeOffset]::Parse($doc.DocumentElement.GetAttribute('ReceiptDate'),[Globalization.CultureInfo]::InvariantCulture)
 $window=Get-ReceiptWindow $minted
 [pscustomobject]@{Path=[IO.Path]::GetFullPath($Path);Bytes=$bytes;Hash=(Get-ResearchFileHash $Path).Hash;Minted=$window.Minted;Expires=$window.Expires;Valid=$window.Valid;Future=$window.Future;SignatureVerified=(!$SkipSignature);AppId=$appId;LicenseType=$licenseType}
}
function Get-StoreCredential {
 Initialize-PublicWam
 $provider=Wait-WamOperation ([Windows.Security.Authentication.Web.Core.WebAuthenticationCoreManager]::FindAccountProviderAsync('https://login.microsoft.com','consumers')) ([Windows.Security.Credentials.WebAccountProvider])
 if(!$provider){throw 'Windows Microsoft-account provider unavailable.'}
 $request=[Windows.Security.Authentication.Web.Core.WebTokenRequest]::new($provider,'service::www.microsoft.com::mbi_ssl','{f0c62012-2cef-4831-b1f7-930682874c86}')
 $response=Wait-WamOperation ([Windows.Security.Authentication.Web.Core.WebAuthenticationCoreManager]::GetTokenSilentlyAsync($request)) ([Windows.Security.Authentication.Web.Core.WebTokenRequestResult])
 if($response.ResponseStatus.ToString() -ne 'Success'){
  $code='unknown';if($response.ResponseError){$code='0x{0:X8}' -f $response.ResponseError.ErrorCode}
  throw ('Windows SSO authorization: '+$response.ResponseStatus+' ('+$code+'). No receipt requested. Sign-in/recovery must be completed in Windows.')
 }
 if(@($response.ResponseData).Count -ne 1 -or !$response.ResponseData[0].Token){throw 'Unexpected Windows credential response.'}
 $item=$response.ResponseData[0];$name=$null
 if($item.WebAccount){$name=[string]$item.WebAccount.UserName}
 if([string]::IsNullOrWhiteSpace($name)){$name='Name not disclosed by Windows SSO'}
 [pscustomobject]@{Token=$item.Token;AccountName=$name;AccountNameAvailable=($name -ne 'Name not disclosed by Windows SSO')}
}
function Get-ServiceErrorName([string]$Code){
 if($Code -in @('0','0x0','0x00000000')){return 'NONE'}
 if($Code -ieq '0xc03f300c' -or $Code -eq '3225366540' -or $Code -eq '-1069600756'){return 'STORE_ERROR_UNLICENSED_USER'}
 return 'OTHER_SERVICE_ERROR'
}
function ConvertTo-GameReceiptBytes([byte[]]$Bytes){
 # Some legacy SDKs widen UTF-8 BOM bytes into the literal characters U+00EF,
 # U+00BB,U+00BF. Remove only this encoding marker, preserving all signed XML.
 if($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF){
  $result=New-Object byte[] ($Bytes.Length-3)
  [Buffer]::BlockCopy($Bytes,3,$result,0,$result.Length)
  return ,$result
 }
 return ,$Bytes
}
function Get-ReceiptRetryDelay([string]$Value){
 $seconds=0;$date=[DateTimeOffset]::MinValue
 if([int]::TryParse($Value,[ref]$seconds) -and $seconds -gt 0){return $seconds}
 if([DateTimeOffset]::TryParse($Value,[ref]$date)){return [Math]::Max(0,[int][Math]::Ceiling(($date-[DateTimeOffset]::UtcNow).TotalSeconds))}
 return 0
}
function Get-ReceiptCooldown {
 $path=Join-Path $script:DataRoot 'receipt-cooldown-private.json'
 if(Test-Path -LiteralPath $path){try{$saved=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json;$until=[DateTimeOffset]::Parse([string]$saved.untilUtc);if($until -gt [DateTimeOffset]::UtcNow){return [int][Math]::Ceiling(($until-[DateTimeOffset]::UtcNow).TotalSeconds)}}catch{}}
 return 0
}
function Assert-ReceiptResponseAllowed([int]$status,[string]$retryAfter){
 if($status -eq 429 -or ($status -eq 503 -and $retryAfter)){
  # Fifteen minutes is an app precaution, not a published limit for this legacy API.
  $delay=[Math]::Max(900,(Get-ReceiptRetryDelay $retryAfter))
  if(!(Test-Path -LiteralPath $script:DataRoot)){$null=New-PrivateFolder $script:DataRoot}
  @{untilUtc=[DateTimeOffset]::UtcNow.AddSeconds($delay).ToString('o');httpStatus=$status}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $script:DataRoot 'receipt-cooldown-private.json') -Encoding UTF8
  $error=New-Object InvalidOperationException('Microsoft throttled receipt requests (HTTP '+$status+'). Checks stopped; try again later.');$error.Data['ReceiptRetryAfterSeconds']=$delay;throw $error
 }
}
function Get-ReceiptRequestUri($Game){
 $product=([guid]$Game.legacy_store_guid).ToString()
 'https://licensingwindows.mp.microsoft.com/Licensing/License/AcquireReceipt/6.2/0?productId='+[Uri]::EscapeDataString($product)+'&receiptType=Full'
}
function Request-GameReceipt($Game,[string]$Run){
 # Coordinate all local app workers; cached sharing transfers can still run concurrently.
 $mutex=New-Object Threading.Mutex($false,('Local\GFW8LG-ReceiptRequests-'+[Security.Principal.WindowsIdentity]::GetCurrent().User.Value))
 $locked=$false
 try{
  try{$locked=$mutex.WaitOne(120000)}catch [Threading.AbandonedMutexException]{$locked=$true}
  if(!$locked){throw 'Another Microsoft receipt request is still running. Try again shortly.'}
  $delay=Get-ReceiptCooldown
  if($delay -gt 0){$error=New-Object InvalidOperationException('Microsoft receipt requests are cooling down. Try again after '+$delay+' seconds.');$error.Data['ReceiptRetryAfterSeconds']=$delay;throw $error}
  Invoke-GameReceiptRequest $Game $Run
 }finally{if($locked){$mutex.ReleaseMutex()};$mutex.Dispose()}
}
function Invoke-GameReceiptRequest($Game,[string]$Run){
 $uri=Get-ReceiptRequestUri $Game
 Write-Host ('Requesting Store edition: '+$Game.package_family_name)
 if($Game.identityWarning){Write-Host $Game.identityWarning -ForegroundColor Yellow}
 $credential=Get-StoreCredential
 Write-Host ('Receipt account (returned with this Windows SSO credential): '+$credential.AccountName) -ForegroundColor Cyan

 $web=[Net.HttpWebRequest]::Create($uri)
 $web.Proxy=$null;$web.AllowAutoRedirect=$false;$web.Method='POST';$web.Timeout=45000;$web.ReadWriteTimeout=45000;$web.ContentLength=0
 $web.Headers['Authorization']='WLID1.0='+$credential.Token
 $web.Headers['MS-SiteName']='www.microsoft.com';$web.Headers['MS-RequestId']=[guid]::NewGuid().ToString();$web.Headers['MS-CorrelationId']=[guid]::NewGuid().ToString()
 try{$response=$web.GetResponse()}catch [Net.WebException]{if(!$_.Exception.Response){throw 'Receipt network/TLS request failed; no license conclusion can be drawn.'};$response=$_.Exception.Response}
 try{$status=[int]$response.StatusCode;$retryAfter=[string]$response.Headers['Retry-After'];$reader=New-Object IO.StreamReader($response.GetResponseStream());try{$raw=$reader.ReadToEnd()}finally{$reader.Dispose()}}finally{$response.Close();$web.Headers.Remove('Authorization');$credential.Token=$null}
 Assert-ReceiptResponseAllowed $status $retryAfter
 if($status -ne 200){throw ('Receipt HTTP '+$status+'. This is not automatically a no-license result.')}
 $envelope=$script:Json.DeserializeObject($raw);$code=[string]$envelope['errorCode'];$kind=Get-ServiceErrorName $code
 $report=[ordered]@{gameId=$Game.index;requestIdentity='legacy-guid';requestProductId=$Game.legacy_store_guid;httpStatus=$status;serviceError=$code;serviceErrorName=$kind;accountNameAvailable=$credential.AccountNameAvailable;accountSource='WebAccount on the exact successful WAM credential response';success=$false}
 $report|ConvertTo-Json|Set-Content (Join-Path $Run 'receipt-result.json')
 if($kind -eq 'STORE_ERROR_UNLICENSED_USER'){
  $report|ConvertTo-Json|Set-Content (Join-Path $Run 'receipt-result.json')
  throw 'No license for this game/edition on the requesting account: 0xC03F300C (STORE_ERROR_UNLICENSED_USER). No Xbox authentication attempted.'
 }
 if($code -notin @('0','0x0','0x00000000') -or !$envelope['receipt']){
  throw ('Receipt service failed: '+$code+'. No token request attempted.')
 }
 $path=Join-Path $Run 'pop.bin';[IO.File]::WriteAllBytes($path,(ConvertTo-GameReceiptBytes ([Convert]::FromBase64String($envelope['receipt']))))
 $info=Read-Receipt $path $Game -SkipSignature
 $report.success=$true;$report.receiptSha256=$info.Hash;$report.receiptDate=$info.Minted.ToString('o');$report.policyExpires=$info.Expires.ToString('o');$report.returnedAppId=$info.AppId;$report.licenseType=$info.LicenseType
 Write-Host ('Microsoft returned a '+$info.LicenseType+' receipt.')
 Write-Host ('Returned receipt identity: '+$info.AppId)
 $report|ConvertTo-Json|Set-Content (Join-Path $Run 'receipt-result.json')
 return $info
}
function Get-TokenWindow($Report){
 $expiry=@($Report.expires)
 if($expiry.Count -ne 1){throw 'Expected one service-issued token expiry; cannot safely guess.'}
 $expires=[DateTimeOffset]::Parse([string]$expiry[0],[Globalization.CultureInfo]::InvariantCulture)
 [pscustomobject]@{Expires=$expires;FourHourReference=$expires.AddHours(-4);Valid=([DateTimeOffset]::UtcNow -lt $expires)}
}
function Get-TokenOutcome([int]$ExitCode,$Report,$Lifecycle,[bool]$ExportExists){
 $issued=($Report.tokenIssued -and $Report.nativeXboxJwt -and $Report.privateTokenExported -and $Report.helperStopped -and $Report.privateSessionRemoved -and $Report.helperRegistrationRemoved -and $ExportExists)
 if($issued -and $Lifecycle.registrationRemoved -and ($Lifecycle.dataPreservationFailureType -or $Report.dataPreservationAlert)){return 'issued-with-data-alert'}
 if($issued -and $ExitCode -eq 0){return 'issued'}
 if($issued -and $Lifecycle.registrationRemoved -and $Lifecycle.cleanupFailureType -and !$Lifecycle.failureType){return 'issued-with-data-alert'}
 if(!$Report.tokenIssued -and [string]$Report.helperHresult -ieq '0x8086000c'){return 'reauthentication-required'}
 return 'failed'
}
function Write-TokenCleanupStatus($Report,$Lifecycle){
 if($Report.helperStopped -and $Report.privateSessionRemoved -and $Lifecycle.registrationRemoved -and !$Lifecycle.cleanupFailureType){
  Write-Host 'Helper cleanup: complete (process stopped; session removed; temporary registration removed).' -ForegroundColor Green
 }else{
  $processState=if($Report.helperStopped){'stopped'}else{'exit unconfirmed'}
  $sessionState=if($Report.privateSessionRemoved){'removed'}else{'removal unconfirmed'}
  $registrationState=if($Lifecycle.registrationRemoved){'removed'}else{'removal unconfirmed'}
  Write-Host ('Helper cleanup: not fully confirmed (process: '+$processState+'; session: '+$sessionState+'; registration: '+$registrationState+'). Check private diagnostics before retrying.') -ForegroundColor Yellow
 }
}
function Get-TokenFailureMessage($Report,$Lifecycle){
 if(!$Report.helperStopped -or !$Report.privateSessionRemoved -or !$Lifecycle.registrationRemoved -or $Lifecycle.cleanupFailureType){return 'Token request failed and helper cleanup is not confirmed. Check private diagnostics before retrying.'}
 if($Report.stage -eq 'xbox-response' -and $Report.xErr){
  $message='Xbox rejected the token request ('+[string]$Report.xErr+').'
  if(@($Report.faultText) -contains 'The creator of this fault did not specify a Reason.'){$message+=' The server did not provide a specific reason.'}
  return $message
 }
 if($Report.helperHresult){return ('Windows token authorization failed ('+[string]$Report.helperHresult+'). See private diagnostics.')}
 return 'Token acquisition failed before a usable token was issued. See private diagnostics.'
}
function Save-CurrentGameData($Configuration,[string]$Run){
 $data=Join-Path $env:LOCALAPPDATA ('Packages\'+$Configuration.family)
 $count=0
 if(Test-Path -LiteralPath $data){foreach($file in Get-ChildItem -LiteralPath $data -File -Recurse -Force){
  if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Unexpected reparse file in current game data.'}
  $relative=$file.FullName.Substring($data.Length+1);$destination=Join-Path $Run ('data-after-private\'+$relative)
  $null=[IO.Directory]::CreateDirectory((Split-Path $destination -Parent))
  $input=[IO.File]::Open($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
  $output=[IO.File]::Open($destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
  try{$input.CopyTo($output)}finally{$input.Dispose();$output.Dispose()}
  $count++
 }}
 return $count
}
function Request-GameToken($Game,$Receipt,[string]$Run,[switch]$Reauthenticate,[switch]$PromptIfNeeded){
 if($Reauthenticate -and $PromptIfNeeded){throw 'Choose forced verification or PromptIfNeeded, not both.'}
 $current=Read-Receipt $Receipt.Path $Game
 if(!$current.Valid){throw 'Receipt expired: acquire a new pop.bin before minting a token.'}
 if($current.Hash -ne $Receipt.Hash){throw 'Receipt changed since validation.'}
 Write-Host ('Token request receipt type: '+$current.LicenseType+'.')
 if(!$Game.tokenAttemptAvailable){throw 'Token metadata for this title is not ready. Its signed pop.bin is still saved; no guessed Xbox title ID will be used.'}
 . (Join-Path $script:Runtime 'jetpack-auth-investigation\TemporaryAuthHelper.ps1')
 $config=Test-TemporaryAuthReady -GameId $Game.index
 $snapshot=@(Get-TemporaryAuthDataSnapshot $config)
 $data=Join-Path $env:LOCALAPPDATA ('Packages\'+$config.family)
 foreach($file in $snapshot){
  $relative=$file.path.Substring($data.Length+1);$destination=Join-Path $Run ('data-before-private\'+$relative)
  $null=[IO.Directory]::CreateDirectory((Split-Path $destination -Parent));Copy-Item -LiteralPath $file.path -Destination $destination
  if((Get-ResearchFileHash $destination).Hash -ne $file.sha256){throw 'Current game-data backup mismatch; helper will not register.'}
 }
 ConvertTo-Json -InputObject $snapshot -Depth 5|Set-Content (Join-Path $Run 'data-baseline-private.json')

 $export=Join-Path $Run ('token-'+[DateTime]::UtcNow.ToString('HHmmss-fff'));$reportPath=Join-Path $Run 'token-result.json'
 $controller=Join-Path $script:Runtime 'jetpack-auth-investigation\Invoke-TemporaryXboxAuth.ps1'
 $arguments=@('-NoProfile','-MTA','-ExecutionPolicy','Bypass','-File',$controller,'-GameId',[string]$Game.index,'-ReceiptPath',$Receipt.Path,'-OutputPath',$reportPath,'-PrivateTokenOutputDirectory',$export)
 if($Reauthenticate){$arguments+='-Reauthenticate';Write-Host 'Complete any Windows sign-in prompt yourself.'}
 if($PromptIfNeeded){$arguments+='-PromptIfNeeded';Write-Host 'Windows will show sign-in only if needed. Complete any Windows authentication prompt.'}
 $capture=Invoke-CapturedAuthDriver (Join-Path $PSHOME 'powershell.exe') $arguments (Join-Path $Run 'token-driver-private.log')
 $exitCode=$capture.ExitCode
 if(!(Test-Path -LiteralPath $reportPath)){Write-Host 'Helper cleanup: not confirmed because token diagnostics are missing.' -ForegroundColor Yellow;throw 'Token helper failed before it produced a result. No token success claimed.'}
 $report=Get-Content -LiteralPath $reportPath -Raw|ConvertFrom-Json
 $lifePath=Join-Path $Run 'temporary-helper.json'
 $life=if(Test-Path -LiteralPath $lifePath){Get-Content -LiteralPath $lifePath -Raw|ConvertFrom-Json}else{[pscustomobject]@{}}
 Write-TokenCleanupStatus $report $life
 if($life.dataPreservationFailureType){
  try{
   $afterCount=Save-CurrentGameData $config $Run
   $report|Add-Member -NotePropertyName dataPreservationAlert -NotePropertyValue $true -Force
   $report|Add-Member -NotePropertyName postRunFilesPreserved -NotePropertyValue $afterCount -Force
   $report|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $reportPath
   Write-Host 'Game-data verification: changes detected. Before/after copies are preserved in saved diagnostics; no files were restored.' -ForegroundColor Yellow
  }catch{
   Write-Host 'Game-data verification: changes detected; the after-copy could not be completed. Keep the current data and inspect private diagnostics.' -ForegroundColor Yellow
   throw 'Helper cleanup was checked, but changed app-data could not be fully preserved. Check private diagnostics before retrying.'
  }
 }
 $outcome=Get-TokenOutcome $exitCode $report $life (Test-Path (Join-Path $export 'issued-token.xml'))
 if($outcome -eq 'reauthentication-required'){
  $exception=New-Object InvalidOperationException('Windows silent sign-in needs verification: 0x8086000C (ONL_E_ACTION_REQUIRED). No Xbox exchange occurred. Retry the same receipt with Windows reauthentication; this is not a no-license error.')
  $exception.Data['RequiresReauthentication']=$true
  throw $exception
 }
 if($outcome -eq 'failed'){
  throw (Get-TokenFailureMessage $report $life)
 }
 if($outcome -eq 'issued-with-data-alert'){
  $afterCount=if($report.postRunFilesPreserved){$report.postRunFilesPreserved}else{Save-CurrentGameData $config $Run}
  $report|Add-Member -NotePropertyName dataPreservationAlert -NotePropertyValue $true -Force
  $report|Add-Member -NotePropertyName postRunFilesPreserved -NotePropertyValue $afterCount -Force
  $report|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $reportPath
  Write-Host 'Xbox token issuance succeeded. A separate app-data preservation check found changed files.' -ForegroundColor Yellow
  Write-Host 'Before/after copies are preserved in this private run folder. Inspect them before another authentication attempt; no files were restored or discarded.' -ForegroundColor Yellow
 }
 if((Get-ResearchFileHash $Receipt.Path).Hash -ne $Receipt.Hash){throw 'Receipt changed during token acquisition.'}
 $window=Get-TokenWindow $report
 Write-Host 'Xbox issued a native token; temporary helper removed.' -ForegroundColor Green
 Write-Host ('Token expires (Xbox service): '+(Format-Time $window.Expires))
 Write-Host ('Token issuance reference (expiry minus exactly 4 hours): '+(Format-Time $window.FourHourReference))
 Write-Host 'The expiry above is authoritative. Xbox may backdate Created for clock skew.'
 Write-Host ('Private token: '+(Join-Path $export 'issued-token.xml'))
 Write-Host ('After token expiry, reuse this exact pop.bin to request another token until '+(Format-Time $Receipt.Expires)+'. Renewal is subject to Xbox acceptance.')
 [pscustomobject]@{Export=$export;Report=$report;Window=$window;Receipt=$Receipt}
}
