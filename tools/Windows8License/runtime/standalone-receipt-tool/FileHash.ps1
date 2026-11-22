# Use .NET directly so CMD launches do not depend on Get-FileHash module autoloading.
function Get-ResearchFileHash {
 param([Parameter(Position=0)][string]$Path,[string]$LiteralPath,[ValidateSet('SHA256')][string]$Algorithm='SHA256')
 $target=if($LiteralPath){$LiteralPath}else{$Path}
 $resolved=(Resolve-Path -LiteralPath $target -ErrorAction Stop).ProviderPath
 $stream=[IO.File]::OpenRead($resolved);$sha=[Security.Cryptography.SHA256]::Create()
 try { [pscustomobject]@{Algorithm='SHA256';Path=$resolved;Hash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')} }
 finally {$sha.Dispose();$stream.Dispose()}
}
