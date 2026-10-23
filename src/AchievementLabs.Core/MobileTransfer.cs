using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace AchievementLabs.Core;
public sealed record MobileTransferFile(byte[] Bytes, string PairingKey);
public static class MobileTransfer
{
    public static string[] QrFrames(MobileTransferFile transfer)
    {
        if (transfer.Bytes.Length > 128*1024) throw new InvalidDataException("Transfer is too large.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { key=transfer.PairingKey, file=Convert.ToBase64String(transfer.Bytes) });
        var text = Convert.ToBase64String(bytes);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var count = (text.Length+799)/800;
        if (count > 256) throw new InvalidDataException("Transfer is too large for QR sync.");
        return Enumerable.Range(0,count).Select(i=>$"ALPC1|{digest}|{i}|{count}|{text.Substring(i*800,Math.Min(800,text.Length-i*800))}").ToArray();
    }
    public static MobileTransferFile Create(string authorization, string xuid, string eventToken, DateTimeOffset? authorizationExpiresAt, bool eventOnly=false, bool signInOnly=false)
    {
        if (!ulong.TryParse(xuid,out _) || (!eventOnly && string.IsNullOrWhiteSpace(authorization))) throw new InvalidDataException("Connect an Xbox account first.");
        if (eventOnly) { if(string.IsNullOrWhiteSpace(eventToken)) throw new InvalidDataException("Save an event token first."); authorization=""; authorizationExpiresAt=null; }
        if (eventOnly && signInOnly) throw new InvalidDataException("Choose one transfer mode.");
        if (signInOnly) eventToken="";
        var key = RandomNumberGenerator.GetBytes(32); var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion=1,kind=eventOnly ? "eventToken" : signInOnly ? "signIn" : "account",xuid,authorization,eventToken,expiresAt=DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),authorizationExpiresAt=authorizationExpiresAt?.ToUniversalTime().ToString("O") });
        try {
            var encrypted = new byte[plain.Length]; var tag = new byte[16];
            using var aes = new AesGcm(key,16);
            aes.Encrypt(nonce,plain,encrypted,tag,Encoding.UTF8.GetBytes("AchievementLabs-PC-Transfer-v1"));
            var envelope = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion=1,nonce=Convert.ToBase64String(nonce),ciphertext=Convert.ToBase64String(encrypted),tag=Convert.ToBase64String(tag) });
            return new(envelope,Convert.ToBase64String(key).TrimEnd('=').Replace('+','-').Replace('/','_'));
        } finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(key); }
    }
}

