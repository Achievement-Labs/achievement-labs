using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AchievementLabs.MultiSelect;

// User-authorized, current-Windows-user Gaming Services cache access only.
// No process-memory reads, no elevation, no token files or diagnostics.
public static class AutomaticEventToken
{
    [DllImport("ncrypt.dll")] static extern int NCryptUnprotectSecret(out IntPtr descriptor,uint flags,byte[] blob,uint length,IntPtr memory,IntPtr window,out IntPtr data,out uint size);
    [DllImport("ncrypt.dll")] static extern int NCryptCloseProtectionDescriptor(IntPtr descriptor);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    public sealed class Candidate
    {
        public string Value {get;} public DateTimeOffset ExpiresAt {get;}
        public Candidate(string value,DateTimeOffset expiresAt){Value=value;ExpiresAt=expiresAt;}
        public override string ToString()=>"[redacted token candidate]";
    }
    public sealed class Grant
    {
        public string Value {get;} public DateTimeOffset ExpiresAt {get;}
        public Grant(string value,DateTimeOffset expiresAt){Value=value;ExpiresAt=expiresAt;}
        public override string ToString()=>"[redacted event authorization]";
    }
    static string S(JsonElement e,string p)=>e.ValueKind==JsonValueKind.Object && e.TryGetProperty(p,out var v) && v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
    public static Candidate? ParseCandidate(ReadOnlyMemory<byte> data)
    {
        using var doc=JsonDocument.Parse(data);var root=doc.RootElement;
        if(S(root,"IdentityType")!="Utoken" || S(root,"RelyingParty")!="http://auth.xboxlive.com" || !root.TryGetProperty("TokenData",out var token))return null;
        string value=S(token,"Token");
        return value.Length>0 && DateTimeOffset.TryParse(S(token,"NotAfter"),out var expiry) && expiry>DateTimeOffset.UtcNow.AddMinutes(2)?new Candidate(value,expiry):null;
    }
    static byte[]? Unprotect(byte[] blob)
    {
        IntPtr descriptor=IntPtr.Zero,data=IntPtr.Zero;uint size=0;
        try
        {
            int result=NCryptUnprotectSecret(out descriptor,0x40,blob,(uint)blob.Length,IntPtr.Zero,IntPtr.Zero,out data,out size);
            if(result==5 || unchecked((uint)result)==0x80090010)throw new UnauthorizedAccessException("Windows denied access to the Gaming Services cache.");
            if(result!=0 || size==0 || size>4*1024*1024)return null;
            var bytes=new byte[(int)size];Marshal.Copy(data,bytes,0,bytes.Length);return bytes;
        }
        finally
        {
            if(data!=IntPtr.Zero)
            {
                if(size<=4*1024*1024)Marshal.Copy(new byte[(int)size],0,data,(int)size);
                LocalFree(data);
            }
            if(descriptor!=IntPtr.Zero)NCryptCloseProtectionDescriptor(descriptor);
        }
    }
    public static Candidate[] ReadCache(CancellationToken token)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Automatic event-token retrieval requires Windows.");
        string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Packages","Microsoft.GamingServices_8wekyb3d8bbwe","LocalState","Auth");
        if(!Directory.Exists(root))return Array.Empty<Candidate>();
        var result=new List<Candidate>();
        var options=new EnumerationOptions {RecurseSubdirectories=true,IgnoreInaccessible=false,AttributesToSkip=FileAttributes.ReparsePoint,MaxRecursionDepth=8};
        int count=0;
        foreach(string path in Directory.EnumerateFiles(root,"*",options))
        {
            token.ThrowIfCancellationRequested();if(++count>2000)throw new InvalidDataException("Gaming Services cache is unexpectedly large; automatic retrieval stopped.");
            byte[]? clear=null;
            try
            {
                if(new FileInfo(path).Length>4*1024*1024)continue;
                clear=Unprotect(File.ReadAllBytes(path));if(clear==null)continue;
                var candidate=ParseCandidate(clear);if(candidate!=null)result.Add(candidate);
            }
            catch(JsonException){} // Cache also contains records that are not JSON user tokens.
            catch(FileNotFoundException){} // Gaming Services can rotate a record during enumeration.
            finally{if(clear!=null)CryptographicOperations.ZeroMemory(clear);}
        }
        return result.DistinctBy(c=>c.Value).OrderByDescending(c=>c.ExpiresAt).ToArray();
    }
    static async Task<JsonDocument?> Exchange(HttpClient http,string userToken,string relyingParty,CancellationToken cancellation)
    {
        var body=JsonSerializer.Serialize(new {Properties=new{SandboxId="RETAIL",UserTokens=new[]{userToken}},RelyingParty=relyingParty,TokenType="JWT"});
        using var content=new StringContent(body,Encoding.UTF8,"application/json");
        using var response=await http.PostAsync("https://xsts.auth.xboxlive.com/xsts/authorize",content,cancellation);
        if(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)return null;
        if(!response.IsSuccessStatusCode)throw new HttpRequestException($"Xbox authentication returned HTTP {(int)response.StatusCode}; retrieval stopped.");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
    }
    static JsonElement Claim(JsonElement root)
    {
        if(root.TryGetProperty("DisplayClaims",out var d) && d.TryGetProperty("xui",out var a) && a.ValueKind==JsonValueKind.Array && a.GetArrayLength()==1)return a[0];
        return default;
    }
    public static async Task<Grant?> Acquire(HttpClient http,string xuid,IEnumerable<Candidate> candidates,CancellationToken token)
    {
        if(!ulong.TryParse(xuid,out _))throw new InvalidOperationException("Connect your Xbox account first.");
        foreach(var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();if(candidate.ExpiresAt<=DateTimeOffset.UtcNow.AddMinutes(2))continue;
            using var identity=await Exchange(http,candidate.Value,"http://xboxlive.com",token);
            if(identity==null)continue;
            var account=Claim(identity.RootElement);string uhs=S(account,"uhs");
            if(S(account,"xid")!=xuid || uhs.Length==0)continue;
            using var events=await Exchange(http,candidate.Value,"http://events.xboxlive.com",token);
            if(events==null)return null;
            var claim=Claim(events.RootElement);string eventXuid=S(claim,"xid");
            if(S(claim,"uhs")!=uhs || eventXuid.Length>0 && eventXuid!=xuid)return null;
            string value=S(events.RootElement,"Token");
            if(value.Length==0 || !DateTimeOffset.TryParse(S(events.RootElement,"NotAfter"),out var expiry) || expiry<=DateTimeOffset.UtcNow.AddMinutes(2))return null;
            return new Grant("x:XBL3.0 x="+uhs+";"+value,expiry);
        }
        return null;
    }
}
