function Export-PrivateXboxToken {
 param([Parameter(Mandatory=$true)][string]$Directory,[Parameter(Mandatory=$true)][string]$ResponseXml,[Parameter(Mandatory=$true)][Xml.XmlElement]$IssuedToken,[Parameter(Mandatory=$true)]$Metadata)
 $full=[IO.Path]::GetFullPath($Directory)
 if(Test-Path -LiteralPath $full){throw 'Token export directory must be new; never overwrite an existing export.'}
 $null=[IO.Directory]::CreateDirectory($full)
 $user=[Security.Principal.WindowsIdentity]::GetCurrent().User
 $acl=New-Object Security.AccessControl.DirectorySecurity
 $acl.SetOwner($user);$acl.SetAccessRuleProtection($true,$false)
 foreach($sid in @($user,(New-Object Security.Principal.SecurityIdentifier('S-1-5-18')))){
  $rule=New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
  $acl.AddAccessRule($rule)
 }
 # Use .NET directly: actual CMD launches cannot rely on security-module autoload.
 [IO.Directory]::SetAccessControl($full,$acl)
 $actual=[IO.Directory]::GetAccessControl($full)
 if(!$actual.AreAccessRulesProtected){throw 'Private export access-control inheritance was not disabled.'}
 $allowed=@($user.Value,'S-1-5-18')
 foreach($entry in $actual.Access){if($entry.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin $allowed){throw 'Unexpected export access rule; no token written.'}}
 $utf8=New-Object Text.UTF8Encoding($false)
 [IO.File]::WriteAllText((Join-Path $full 'native-token.txt'),$IssuedToken.GetAttribute('t'),$utf8)
 [IO.File]::WriteAllText((Join-Path $full 'issued-token.xml'),$IssuedToken.OuterXml,$utf8)
 [IO.File]::WriteAllText((Join-Path $full 'xbox-response.xml'),$ResponseXml,$utf8)
 [IO.File]::WriteAllText((Join-Path $full 'inspection.json'),($Metadata | ConvertTo-Json -Depth 5),$utf8)
 try{
  $first=$IssuedToken.GetAttribute('t').Split('.')[0].Replace('-','+').Replace('_','/')
  $first=$first.PadRight($first.Length+((4-$first.Length%4)%4),'=')
  $header=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($first)) | ConvertFrom-Json -ErrorAction Stop
  [IO.File]::WriteAllText((Join-Path $full 'decoded-token-private.json'),(@{header=$header} | ConvertTo-Json -Depth 8),$utf8)
 }catch{
  # Decoding is informational; preserve the issued bytes even for unknown formats.
 }
 [IO.File]::WriteAllText((Join-Path $full 'READ-ME.txt'),@'
PRIVATE BEARER CREDENTIALS - requested local inspection export.
native-token.txt is the complete t attribute issued by Xbox.
issued-token.xml is the native jwt v/t element.
xbox-response.xml is the complete Xbox service SOAP response, including lifetime.
inspection.json contains metadata, not the token.
decoded-token-private.json, when present, decodes the header only. The observed
four-part legacy encrypted format does not expose readable JSON claims.

This is the legacy native xjwt/1.0/bearer result, not a Store receipt or the
intermediate OnlineId delegation ticket. Token issuance is verified by the
successful service response; local JWT signature verification and a separate
Xbox API authorization request are not performed. Do not assume modern compact
JWT syntax or construct a modern XBL3.0 header from it without protocol evidence.

Treat these files like a temporary password. Do not share or commit them.
Only your Windows account and SYSTEM have access through this directory ACL.
Expiry is recorded in inspection.json and the SOAP response.
Exports are opt-in and retained until you remove them. Default runs export none.
'@,$utf8)
 return $full
}
