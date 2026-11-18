# Capture raw child streams without NativeCommandError aborting the caller.
function ConvertTo-AuthNativeArgument([string]$Value){
 $escaped=[regex]::Replace($Value,'(\\*)"','$1$1\"')
 $escaped=[regex]::Replace($escaped,'(\\+)$','$1$1')
 return '"'+$escaped+'"'
}
function Invoke-CapturedAuthDriver([string]$Executable,[string[]]$Arguments,[string]$LogPath){
 $info=New-Object Diagnostics.ProcessStartInfo
 $info.FileName=$Executable
 $info.Arguments=(@($Arguments|ForEach-Object {ConvertTo-AuthNativeArgument $_}) -join ' ')
 $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden'
 $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $process=$null
 try{
  $process=[Diagnostics.Process]::Start($info)
  $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
  $process.WaitForExit()
  [IO.File]::WriteAllText($LogPath,('STDOUT'+[Environment]::NewLine+$stdout.GetAwaiter().GetResult()+[Environment]::NewLine+'STDERR'+[Environment]::NewLine+$stderr.GetAwaiter().GetResult()),(New-Object Text.UTF8Encoding($false)))
  [pscustomobject]@{ExitCode=$process.ExitCode}
 }catch{
  [IO.File]::WriteAllText($LogPath,('Child process capture failed: '+$_.Exception.ToString()),(New-Object Text.UTF8Encoding($false)))
  throw
 }finally{if($process){$process.Dispose()}}
}
