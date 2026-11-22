using System.Net;
using System.Net.Http;
using System.Text;
using XboxAuthNet.XboxLive;
using XboxAuthNet.XboxLive.Crypto;
using XboxAuthNet.XboxLive.Requests;

namespace AchievementLabs.Services.LegacyXbox;

public sealed class LegacyXboxBridge : ILegacyXboxBridge
{
    private readonly HttpClient _httpClient;
    private const string ProgressContractVersion = "1";
    private IXboxRequestSigner? _win8RequestSigner;
    private string? _win8SignedAuthorizationHeader;
    private readonly Func<string>? _responseLanguage;

    public LegacyXboxBridge(Func<string>? responseLanguage = null)
    {
        _responseLanguage = responseLanguage;
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _httpClient = new HttpClient(handler);
    }

    public async Task<LegacyAuthBridgeResult> ConvertMsaAccessTokenAsync(string msaAccessToken, string relyingParty = "http://xboxlive.com")
    {
        if (string.IsNullOrWhiteSpace(msaAccessToken))
            throw new ArgumentException("MSA access token is required.", nameof(msaAccessToken));

        var authClient = new XboxAuthClient(_httpClient);
        Exception? lastError = null;

        foreach (var ticket in BuildRpsTicketCandidates(msaAccessToken.Trim()))
        {
            try
            {
                var userToken = await authClient.RequestUserToken(ticket);
                if (string.IsNullOrWhiteSpace(userToken.Token))
                    continue;

                var xsts = await authClient.RequestXsts(userToken.Token, relyingParty);
                var userHash = xsts.XuiClaims?.UserHash ?? "";
                var xuid = xsts.XuiClaims?.XboxUserId ?? "";

                if (string.IsNullOrWhiteSpace(xsts.Token) || string.IsNullOrWhiteSpace(userHash))
                    throw new InvalidOperationException("XSTS response did not include a token or user hash.");

                return new LegacyAuthBridgeResult(
                    $"XBL3.0 x={userHash};{xsts.Token}",
                    userHash,
                    xuid,
                    xsts.Token,
                    xsts.ExpireOn);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException("Unable to convert the MSA access token to an XBL3.0 authorization header.", lastError);
    }

    public async Task<LegacySignedAuthBridgeResult> ConvertMsaAccessTokenToSignedWin8Async(
        string msaAccessToken,
        bool allowWin32Fallback = false,
        string relyingParty = "http://xboxlive.com")
    {
        var deviceCandidates = new List<(string DeviceType, string Version, bool IncludeSerial)>
        {
            (XboxDeviceTypes.WindowsOneCore, "10.0.19045", false),
            (XboxDeviceTypes.WindowsOneCore, "10.0.19045", true)
        };
        if (allowWin32Fallback)
            deviceCandidates.Add((XboxDeviceTypes.Win32, "10.0.19045", false));

        return await ConvertMsaAccessTokenToSignedDeviceAsync(
            msaAccessToken,
            deviceCandidates,
            allowWin32Fallback ? "WindowsOneCore/Win32" : "WindowsOneCore",
            relyingParty);
    }

    public Task<LegacySignedAuthBridgeResult> ConvertMsaAccessTokenToSignedWin32Async(
        string msaAccessToken,
        string relyingParty = "http://xboxlive.com") =>
        ConvertMsaAccessTokenToSignedDeviceAsync(
            msaAccessToken,
            new List<(string DeviceType, string Version, bool IncludeSerial)>
            {
                (XboxDeviceTypes.Win32, "10.0.19045", false)
            },
            "Win32",
            relyingParty);

    private async Task<LegacySignedAuthBridgeResult> ConvertMsaAccessTokenToSignedDeviceAsync(
        string msaAccessToken,
        IReadOnlyList<(string DeviceType, string Version, bool IncludeSerial)> deviceCandidates,
        string failureLabel,
        string relyingParty)
    {
        if (string.IsNullOrWhiteSpace(msaAccessToken))
            throw new ArgumentException("MSA access token is required.", nameof(msaAccessToken));

        var errors = new List<string>();

        foreach (var ticket in BuildRpsTicketCandidates(msaAccessToken.Trim()))
        {
            foreach (var deviceCandidate in deviceCandidates)
            {
                var signer = new XboxRequestSigner(new ECDCertificatePopCryptoProvider());
                var signedClient = new XboxSignedClient(signer, _httpClient);
                var authClient = new XboxAuthClient(_httpClient);
                var ticketLabel = ticket.StartsWith("d=", StringComparison.OrdinalIgnoreCase)
                    ? "d="
                    : ticket.StartsWith("t=", StringComparison.OrdinalIgnoreCase) ? "t=" : "raw";

                try
                {
                    var userToken = await RunAuthPhase(
                        "user token",
                        () => authClient.RequestUserToken(ticket));
                    var deviceToken = await RunAuthPhase(
                        $"device token {deviceCandidate.DeviceType} {deviceCandidate.Version} serial={deviceCandidate.IncludeSerial}",
                        () => signedClient.RequestDeviceToken(new XboxDeviceTokenRequest
                        {
                            DeviceType = deviceCandidate.DeviceType,
                            DeviceVersion = deviceCandidate.Version,
                            SerialNumber = deviceCandidate.IncludeSerial ? null : ""
                        }));
                    var xsts = await RunAuthPhase(
                        "signed XSTS",
                        () => signedClient.RequestSignedXsts(new XboxSignedXstsRequest
                        {
                            UserToken = userToken.Token,
                            DeviceToken = deviceToken.Token,
                            RelyingParty = relyingParty
                        }));

                    var userHash = xsts.XuiClaims?.UserHash ?? "";
                    var xuid = xsts.XuiClaims?.XboxUserId ?? "";

                    if (string.IsNullOrWhiteSpace(xsts.Token) || string.IsNullOrWhiteSpace(userHash))
                        throw new InvalidOperationException("Signed XSTS response did not include a token or user hash.");

                    _win8RequestSigner = signer;
                    _win8SignedAuthorizationHeader = $"XBL3.0 x={userHash};{xsts.Token}";

                    return new LegacySignedAuthBridgeResult(
                        _win8SignedAuthorizationHeader,
                        userHash,
                        xuid,
                        xsts.Token,
                        xsts.ExpireOn,
                        deviceCandidate.DeviceType,
                        deviceCandidate.Version,
                        deviceCandidate.IncludeSerial,
                        ticketLabel);
                }
                catch (Exception ex)
                {
                    errors.Add($"{ticketLabel} / {deviceCandidate.DeviceType} {deviceCandidate.Version} serial={deviceCandidate.IncludeSerial}: {DescribeAuthException(ex)}");
                }
            }
        }

        throw new InvalidOperationException(
            $"Unable to mint a signed {failureLabel} XSTS token." +
            Environment.NewLine +
            string.Join(Environment.NewLine, errors));
    }

    public async Task<IReadOnlyList<LegacyDeviceAuthProbeResult>> ProbeSignedDeviceAuthAsync()
    {
        var results = new List<LegacyDeviceAuthProbeResult>();
        foreach (var deviceType in new[] { XboxDeviceTypes.WindowsOneCore, XboxDeviceTypes.Win32 })
        {
            var signer = new XboxRequestSigner(new ECDCertificatePopCryptoProvider());
            var signedClient = new XboxSignedClient(signer, _httpClient);

            try
            {
                var deviceToken = await signedClient.RequestDeviceToken(new XboxDeviceTokenRequest
                {
                    DeviceType = deviceType,
                    DeviceVersion = "10.0.19045",
                    SerialNumber = ""
                });
                results.Add(new LegacyDeviceAuthProbeResult(
                    deviceType,
                    true,
                    $"OK. Token length: {deviceToken.Token?.Length ?? 0}. Expires: {deviceToken.ExpireOn}"));
            }
            catch (Exception ex)
            {
                results.Add(new LegacyDeviceAuthProbeResult(
                    deviceType,
                    false,
                    DescribeAuthException(ex)));
            }
        }

        return results;
    }

    public async Task<LegacyProgressUnlockResult> SendProgressUnlockAsync(
        LegacyXboxTitleProfile profile,
        string authorizationHeader,
        string xuid,
        string titleId,
        string achievementId,
        int achievementPlatform,
        DateTime unlockTimeUtc,
        LegacyUnlockRequestMode requestMode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
            throw new ArgumentException("Authorization header is required.", nameof(authorizationHeader));
        if (string.IsNullOrWhiteSpace(xuid))
            throw new ArgumentException("XUID is required.", nameof(xuid));
        if (string.IsNullOrWhiteSpace(titleId))
            throw new ArgumentException("Title ID is required.", nameof(titleId));
        if (string.IsNullOrWhiteSpace(achievementId))
            throw new ArgumentException("Achievement ID is required.", nameof(achievementId));

        var timestamp = unlockTimeUtc.Kind == DateTimeKind.Utc
            ? unlockTimeUtc
            : unlockTimeUtc.ToUniversalTime();

        var requestUri = BuildUnlockRequestUri(profile, xuid, titleId, achievementId, requestMode);
        var requestBody = BuildUnlockRequestBody(
            profile,
            xuid,
            titleId,
            achievementId,
            achievementPlatform,
            timestamp,
            requestMode);
        var method = requestMode == LegacyUnlockRequestMode.PercentCompletePut ? HttpMethod.Put : HttpMethod.Post;

        var requestAuthorizationHeader = requestMode == LegacyUnlockRequestMode.SignedAchievementRecord &&
                                         !string.IsNullOrWhiteSpace(_win8SignedAuthorizationHeader)
            ? _win8SignedAuthorizationHeader
            : authorizationHeader;

        using var request = new HttpRequestMessage(method, requestUri);
        request.Headers.TryAddWithoutValidation(HeaderNames.Authorization, requestAuthorizationHeader);
        request.Headers.TryAddWithoutValidation(HeaderNames.AcceptEncoding, HeaderValues.AcceptEncoding);
        request.Headers.TryAddWithoutValidation(HeaderNames.AcceptLanguage, "en-AU");
        request.Headers.TryAddWithoutValidation(
            HeaderNames.ContractVersion,
            requestUri.Contains("progress.xboxlive.com", StringComparison.OrdinalIgnoreCase)
                ? ProgressContractVersion
                : HeaderValues.ContractVersion2);
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        if (requestMode == LegacyUnlockRequestMode.SignedAchievementRecord)
        {
            if (_win8RequestSigner == null)
                throw new InvalidOperationException("Use Signed Win8 first to mint a device-bound signer.");

            request.Headers.TryAddWithoutValidation(
                HeaderNames.Signature,
                _win8RequestSigner.SignRequest(requestUri, requestAuthorizationHeader, requestBody));
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var isAlreadyUnlocked = response.StatusCode == HttpStatusCode.Conflict &&
                                responseBody.Contains("\"code\":19", StringComparison.OrdinalIgnoreCase);

        return new LegacyProgressUnlockResult(
            (int)response.StatusCode,
            response.ReasonPhrase ?? "",
            requestUri,
            requestBody,
            responseBody,
            response.IsSuccessStatusCode || isAlreadyUnlocked);
    }

    public async Task<LegacyAchievementReadResult> ReadAchievementsAsync(
        string authorizationHeader,
        string xuid,
        string titleId,
        LegacyAchievementReadSource source,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
            throw new ArgumentException("Authorization header is required.", nameof(authorizationHeader));
        if (string.IsNullOrWhiteSpace(xuid))
            throw new ArgumentException("XUID is required.", nameof(xuid));
        if (string.IsNullOrWhiteSpace(titleId))
            throw new ArgumentException("Title ID is required.", nameof(titleId));

        var requestUri = source == LegacyAchievementReadSource.Progress
            ? $"https://progress.xboxlive.com/users/xuid({Uri.EscapeDataString(xuid)})/progress/titleachievements?titleId={Uri.EscapeDataString(titleId)}&maxItems=300"
            : $"https://achievements.xboxlive.com/users/xuid({Uri.EscapeDataString(xuid)})/achievements?titleId={Uri.EscapeDataString(titleId)}&maxItems=1000";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation(HeaderNames.Authorization, authorizationHeader);
        request.Headers.TryAddWithoutValidation(HeaderNames.Accept, HeaderValues.Accept);
        request.Headers.TryAddWithoutValidation(HeaderNames.AcceptEncoding, HeaderValues.AcceptEncoding);
        request.Headers.TryAddWithoutValidation(HeaderNames.AcceptLanguage, _responseLanguage?.Invoke() ?? "en-US");
        request.Headers.TryAddWithoutValidation(
            HeaderNames.ContractVersion,
            source == LegacyAchievementReadSource.Progress ? ProgressContractVersion : HeaderValues.ContractVersion4);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        return new LegacyAchievementReadResult(
            (int)response.StatusCode,
            response.ReasonPhrase ?? "",
            requestUri,
            responseBody,
            response.IsSuccessStatusCode);
    }

    private static IEnumerable<string> BuildRpsTicketCandidates(string msaAccessToken)
    {
        if (msaAccessToken.StartsWith("d=", StringComparison.OrdinalIgnoreCase) ||
            msaAccessToken.StartsWith("t=", StringComparison.OrdinalIgnoreCase))
        {
            yield return msaAccessToken;
        }
        else
        {
            yield return $"d={msaAccessToken}";
            yield return msaAccessToken;
            yield return $"t={msaAccessToken}";
        }
    }

    private static async Task<T> RunAuthPhase<T>(string phase, Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{phase} failed: {DescribeAuthException(ex)}", ex);
        }
    }

    private static string DescribeAuthException(Exception ex)
    {
        var parts = new List<string>();
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is XboxAuthException xboxAuthException)
            {
                parts.Add(
                    $"XboxAuth status={xboxAuthException.StatusCode}" +
                    $"{(string.IsNullOrWhiteSpace(xboxAuthException.Error) ? "" : $" xerr={xboxAuthException.Error}")}" +
                    $"{(string.IsNullOrWhiteSpace(xboxAuthException.ErrorMessage) ? "" : $" message={xboxAuthException.ErrorMessage}")}");
            }
            else
            {
                parts.Add($"{current.GetType().Name}: {current.Message}");
            }
        }

        return string.Join(" <- ", parts);
    }

    private static string BuildUnlockRequestUri(
        LegacyXboxTitleProfile profile,
        string xuid,
        string titleId,
        string achievementId,
        LegacyUnlockRequestMode requestMode)
    {
        var escapedXuid = Uri.EscapeDataString(xuid);
        var escapedTitleId = Uri.EscapeDataString(titleId);
        var escapedAchievementId = Uri.EscapeDataString(achievementId);

        return requestMode switch
        {
            LegacyUnlockRequestMode.PercentCompletePost or LegacyUnlockRequestMode.ProgressUpdatePost =>
                $"https://progress.xboxlive.com/users/xuid({escapedXuid})/progress/achievements",
            LegacyUnlockRequestMode.TitleAchievementProgressUpdatePost =>
                $"https://progress.xboxlive.com/users/xuid({escapedXuid})/progress/titleachievements",
            LegacyUnlockRequestMode.TitleAchievementRecord =>
                $"https://progress.xboxlive.com/users/xuid({escapedXuid})/progress/titleachievements/{escapedAchievementId}?titleId={escapedTitleId}",
            LegacyUnlockRequestMode.PercentCompletePut =>
                $"https://progress.xboxlive.com/users/xuid({escapedXuid})/progress/achievements/{escapedAchievementId}?titleId={escapedTitleId}",
            LegacyUnlockRequestMode.AchievementServiceUpdate =>
                $"https://achievements.xboxlive.com/users/xuid({escapedXuid})/achievements",
            LegacyUnlockRequestMode.AchievementServiceScidUpdate when !string.IsNullOrWhiteSpace(profile.ServiceConfigId) =>
                $"https://achievements.xboxlive.com/users/xuid({escapedXuid})/achievements/{profile.ServiceConfigId.ToLowerInvariant()}/update",
            LegacyUnlockRequestMode.AchievementServiceScidUpdate =>
                $"https://achievements.xboxlive.com/users/xuid({escapedXuid})/achievements",
            _ => profile.ProgressEndpointTemplate
                .Replace("{xuid}", escapedXuid)
                .Replace("{achievementId}", escapedAchievementId)
                .Replace("{titleId}", escapedTitleId)
        };
    }

    private static string BuildUnlockRequestBody(
        LegacyXboxTitleProfile profile,
        string xuid,
        string titleId,
        string achievementId,
        int achievementPlatform,
        DateTime timestamp,
        LegacyUnlockRequestMode requestMode)
    {
        return requestMode switch
        {
            LegacyUnlockRequestMode.PercentCompletePost =>
                $"{{\"titleId\":{JsonNumberOrString(titleId)},\"achievements\":[{{\"id\":{JsonNumberOrString(achievementId)},\"percentComplete\":100}}]}}",
            LegacyUnlockRequestMode.PercentCompletePut =>
                "{\"percentComplete\":100}",
            LegacyUnlockRequestMode.ProgressUpdatePost =>
                BuildProgressUpdateRequestBody(profile, xuid, titleId, achievementId, useStringAchievementId: false, includeServiceConfigId: true),
            LegacyUnlockRequestMode.TitleAchievementProgressUpdatePost =>
                BuildProgressUpdateRequestBody(profile, xuid, titleId, achievementId, useStringAchievementId: false, includeServiceConfigId: true),
            LegacyUnlockRequestMode.AchievementServiceUpdate =>
                BuildProgressUpdateRequestBody(profile, xuid, titleId, achievementId, useStringAchievementId: false, includeServiceConfigId: false),
            LegacyUnlockRequestMode.AchievementServiceScidUpdate =>
                BuildProgressUpdateRequestBody(profile, xuid, titleId, achievementId, useStringAchievementId: true, includeServiceConfigId: true),
            LegacyUnlockRequestMode.AchievementRecord or LegacyUnlockRequestMode.SignedAchievementRecord or LegacyUnlockRequestMode.TitleAchievementRecord => profile.UnlockPayloadTemplate
                .Replace("{achievementId}", achievementId)
                .Replace("{titleId}", titleId)
                .Replace("{platform}", achievementPlatform.ToString())
                .Replace("{timestamp}", timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")),
            _ => profile.UnlockPayloadTemplate
        };
    }

    private static string BuildProgressUpdateRequestBody(
        LegacyXboxTitleProfile profile,
        string xuid,
        string titleId,
        string achievementId,
        bool useStringAchievementId,
        bool includeServiceConfigId)
    {
        var serviceConfig = !includeServiceConfigId || string.IsNullOrWhiteSpace(profile.ServiceConfigId)
            ? ""
            : $",\"serviceConfigId\":\"{EscapeJson(profile.ServiceConfigId.ToLowerInvariant())}\"";
        var achievementIdValue = useStringAchievementId
            ? $"\"{EscapeJson(achievementId)}\""
            : JsonNumberOrString(achievementId);

        return $"{{\"action\":\"progressUpdate\",\"titleId\":{JsonNumberOrString(titleId)},\"userId\":\"{EscapeJson(xuid)}\"{serviceConfig},\"achievements\":[{{\"id\":{achievementIdValue},\"percentComplete\":100}}]}}";
    }

    private static string JsonNumberOrString(string value)
    {
        return long.TryParse(value, out _) ? value : $"\"{EscapeJson(value)}\"";
    }

    private static string EscapeJson(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
