# Public WinRT API experiment. The observed Store client ID is not our own app registration.
# No raw credential values are written to disk or emitted to the host.
function Initialize-PublicWam {
 Add-Type -AssemblyName System.Runtime.WindowsRuntime
 $null=[Windows.Security.Authentication.Web.Core.WebAuthenticationCoreManager,Windows.Security.Authentication.Web.Core,ContentType=WindowsRuntime]
 $null=[Windows.Security.Credentials.WebAccountProvider,Windows.Security.Credentials,ContentType=WindowsRuntime]
 $null=[Windows.Security.Authentication.Web.Core.WebTokenRequestResult,Windows.Security.Authentication.Web.Core,ContentType=WindowsRuntime]
 $null=[Windows.Security.Authentication.Web.Core.WebTokenRequest,Windows.Security.Authentication.Web.Core,ContentType=WindowsRuntime]
}
function Wait-WamOperation($Operation,[Type]$ResultType) {
 $method=[System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and $_.GetGenericArguments().Count -eq 1 -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
 $task=$method.MakeGenericMethod($ResultType).Invoke($null,@($Operation))
 if (-not $task.Wait(30000)) { throw 'Windows account operation timed out.' }
 return $task.GetAwaiter().GetResult()
}
function Get-PublicWamTicket {
 Initialize-PublicWam
 $provider=Wait-WamOperation ([Windows.Security.Authentication.Web.Core.WebAuthenticationCoreManager]::FindAccountProviderAsync('https://login.microsoft.com','consumers')) ([Windows.Security.Credentials.WebAccountProvider])
 if (-not $provider) { throw 'Microsoft account provider not available.' }
 $request=[Windows.Security.Authentication.Web.Core.WebTokenRequest]::new($provider,'service::www.microsoft.com::mbi_ssl','{f0c62012-2cef-4831-b1f7-930682874c86}')
 $response=Wait-WamOperation ([Windows.Security.Authentication.Web.Core.WebAuthenticationCoreManager]::GetTokenSilentlyAsync($request)) ([Windows.Security.Authentication.Web.Core.WebTokenRequestResult])
 if ($response.ResponseStatus.ToString() -ne 'Success') {
  $code='unknown'; if ($response.ResponseError) { $code='0x{0:X8}' -f $response.ResponseError.ErrorCode }
  throw ('Windows account authorization returned {0} ({1}). Sign into Microsoft Store with the intended account and retry. Interactive recovery is not implemented in this PoC.' -f $response.ResponseStatus,$code)
 }
 if (@($response.ResponseData).Count -ne 1 -or -not $response.ResponseData[0].Token) { throw 'Windows returned an unexpected token response.' }
 return $response.ResponseData[0].Token
}

