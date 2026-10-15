using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AchievementLabs.MultiSelect;

// The POST is a batch statistics READ, not a statistics update.
public static class XboxPlaytime
{
    static JsonElement Get(JsonElement value,string key)
    {
        if(value.ValueKind==JsonValueKind.Object)
            foreach(var p in value.EnumerateObject())if(p.Name.Equals(key,StringComparison.OrdinalIgnoreCase))return p.Value;
        return default;
    }
    static string Text(JsonElement value)=>value.ValueKind is JsonValueKind.String?value.GetString()??"":value.ValueKind==JsonValueKind.Number?value.ToString():"";
    public static decimal? Parse(string json,string xuid,string titleId)
    {
        using var doc=JsonDocument.Parse(json);
        var values=new HashSet<decimal>();
        void ReadCollections(JsonElement parent)
        {
            var lists=Get(parent,"statlistscollection");
            if(lists.ValueKind!=JsonValueKind.Array)lists=Get(parent,"statlistcollection");
            if(lists.ValueKind!=JsonValueKind.Array)return;
            foreach(var list in lists.EnumerateArray())
            {
                // Match the account and title, never simply take the first stat.
                if(!Text(Get(list,"arrangebyfield")).Equals("xuid",StringComparison.OrdinalIgnoreCase) || Text(Get(list,"arrangebyfieldid"))!=xuid)continue;
                var stats=Get(list,"stats");if(stats.ValueKind!=JsonValueKind.Array)continue;
                foreach(var stat in stats.EnumerateArray())
                {
                    if(Text(Get(stat,"titleid"))!=titleId || !Text(Get(stat,"name")).Equals("MinutesPlayed",StringComparison.OrdinalIgnoreCase))continue;
                    if(decimal.TryParse(Text(Get(stat,"value")),NumberStyles.Float,CultureInfo.InvariantCulture,out var minutes) && minutes>=0 && minutes<1000000000000m)values.Add(minutes);
                }
            }
        }
        ReadCollections(doc.RootElement);
        var groups=Get(doc.RootElement,"groups");
        if(groups.ValueKind==JsonValueKind.Array)foreach(var group in groups.EnumerateArray())ReadCollections(group);
        return values.Count==1?values.Single():null;
    }
    public static string Format(decimal minutes)
    {
        decimal whole=decimal.Floor(minutes);
        return $"{decimal.Floor(whole/60):N0} h {whole%60:00} min";
    }
    public sealed class RateLimited:Exception
    {
        public DateTimeOffset RetryAt {get;}
        public RateLimited(DateTimeOffset retryAt):base("Xbox rate limit; waiting before refreshing."){RetryAt=retryAt;}
    }
    public static async Task<decimal?> Read(HttpClient http,string xuid,string titleId,CancellationToken token,string? authorization=null)
    {
        if(!ulong.TryParse(xuid,out _) || !uint.TryParse(titleId,out uint id) || id==0)throw new ArgumentException("Choose a valid Xbox Title ID and connect your account.");
        string body=JsonSerializer.Serialize(new {arrangebyfield="xuid",xuids=new[]{xuid},stats=new[]{new{name="MinutesPlayed",titleid=titleId}}});
        using var request=new HttpRequestMessage(HttpMethod.Post,"https://userstats.xboxlive.com/batch") {Content=new StringContent(body,Encoding.UTF8,"application/json")};
        request.Headers.Add("x-xbl-contract-version","2");
        if(authorization!=null)request.Headers.TryAddWithoutValidation("Authorization",authorization);
        using var response=await http.SendAsync(request,token);
        if(response.StatusCode==HttpStatusCode.TooManyRequests)
        {
            var retry=response.Headers.RetryAfter?.Date??DateTimeOffset.UtcNow+(response.Headers.RetryAfter?.Delta??TimeSpan.FromMinutes(5));
            throw new RateLimited(retry>DateTimeOffset.UtcNow?retry:DateTimeOffset.UtcNow.AddMinutes(1));
        }
        if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)throw new UnauthorizedAccessException("Xbox denied the statistics read. Reconnect your account.");
        if(!response.IsSuccessStatusCode)throw new HttpRequestException($"Xbox statistics returned HTTP {(int)response.StatusCode}.");
        return Parse(await response.Content.ReadAsStringAsync(token),xuid,titleId);
    }
}
