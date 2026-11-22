$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$fixture=Join-Path $repo ('artifacts\win8-fixture-'+[guid]::NewGuid().ToString('N'))
$backend=Join-Path $fixture 'tools\Windows8License'
$null=New-Item -ItemType Directory -Force (Join-Path $fixture 'tools'),(Join-Path $backend 'runtime\jetpack-auth-investigation')
Copy-Item -LiteralPath (Join-Path $repo 'tools\Acquire-Windows8License.ps1') -Destination (Join-Path $fixture 'tools\Acquire-Windows8License.ps1')
$originalCore=(Join-Path $repo 'tools\Windows8License\Core.ps1').Replace("'","''")
$mock=@'
. '__CORE__'
$script:Runtime=Join-Path $PSScriptRoot 'runtime'
function Request-GameReceipt($Game,[string]$Run){
 $path=Join-Path $Run 'pop.bin';[IO.File]::WriteAllText($path,'synthetic receipt')
 [pscustomobject]@{Path=$path;Hash='synthetic';Valid=$true}
}
function Read-Receipt([string]$Path,$Game){[pscustomobject]@{Path=$Path;Hash='synthetic';Valid=$true}}
function Get-AppxPackage { param([string]$Name) if($env:LABS_WIN8_FIXTURE_INSTALLED -eq '1'){[pscustomobject]@{Name=$Name}} }
function Request-GameToken($Game,$Receipt,[string]$Run){
 $export=New-PrivateFolder (Join-Path $Run 'token')
 [IO.File]::WriteAllText((Join-Path $export 'issued-token.xml'),'<jwt v="1" t="synthetic" />')
 [pscustomobject]@{Export=$export;Window=[pscustomobject]@{Expires=[DateTimeOffset]::UtcNow.AddHours(1)};Report=[pscustomobject]@{dataPreservationAlert=$false}}
}
'@
$mock.Replace('__CORE__',$originalCore)|Set-Content (Join-Path $backend 'Core.ps1')
'{"games":[{"index":1,"legacy_xbox_title_id":123,"package_family_name":"Synthetic_Test"}]}'|Set-Content (Join-Path $backend 'games.json')
'function Get-TemporaryAuthConfig([int]$GameId){[pscustomobject]@{name="Synthetic";family="Synthetic_Test";titleId=123}}'|Set-Content (Join-Path $backend 'runtime\jetpack-auth-investigation\TemporaryAuthHelper.ps1')
$data=Join-Path $fixture 'private'
$script=Join-Path $fixture 'tools\Acquire-Windows8License.ps1'
foreach($installed in @('0','1')){
 $env:LABS_WIN8_FIXTURE_INSTALLED=$installed
 $request=[guid]::NewGuid().ToString('N')
 & powershell.exe -NoProfile -MTA -ExecutionPolicy Bypass -File $script -TitleId 123 -DataDirectory $data -RequestId $request
 $exitCode=$LASTEXITCODE
 $result=Get-Content -LiteralPath (Join-Path $data ($request+'.json')) -Raw|ConvertFrom-Json
 if($installed -eq '0'){
  if($exitCode -ne 0 -or !$result.success -or !(Test-Path -LiteralPath $result.tokenPath) -or !(Test-Path -LiteralPath $result.receiptPath)){throw 'Successful fixture contract failed.'}
 }else{
  if($exitCode -eq 0 -or $result.success -or $result.tokenPath -or !(Test-Path -LiteralPath $result.receiptPath) -or $result.message -notlike '*uninstalled*'){throw 'Installed-game fixture failed.'}
 }
}
Remove-Item Env:\LABS_WIN8_FIXTURE_INSTALLED
Write-Output 'PASS: real wrapper staging/private ACL/result protocol with synthetic receipt/token backend; installed game retains receipt and blocks token. No live calls or package changes.'
