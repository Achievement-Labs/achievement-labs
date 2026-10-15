using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AchievementLabs.MultiSelect;

// Read-only Xbox export. This client never uses event/unlock endpoints.
public sealed class AchievementExport
{
    public sealed record Game(string Id,string Name,string Platform,int ExpectedScore);
    public sealed record Row(string Id,string Name,string Description,string Status,string Score,string UnlockedAt,string ServiceConfigId,bool LegacyProgressVerified = false, double? GlobalUnlockPercentage = null, string? ImageUrl = null);
    public sealed record Saved(int Version,string Account,Game Game,DateTimeOffset ScannedAt,Row[] Rows);
    public sealed record Progress(int Done,int Total,int Saved,int Skipped,int Failed,string Message);
    public sealed record Result(int Saved,int Skipped,int Failed,bool Paused,string Folder);
    readonly HttpClient http;
    readonly string xuid;
    readonly Func<TimeSpan,CancellationToken,Task> delay;
    readonly Action<string>? notice;
    public AchievementExport(HttpClient http,string xuid,Action<string>? notice=null,Func<TimeSpan,CancellationToken,Task>? delay=null)
    {this.http=http;this.xuid=xuid;this.notice=notice;this.delay=delay??Task.Delay;}
    public static string AccountKey(string xuid)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xuid))).Substring(0,16);
    static JsonElement Get(JsonElement e,string name)=>e.ValueKind==JsonValueKind.Object && e.TryGetProperty(name,out var v)?v:default;
    static string Text(JsonElement e)=>e.ValueKind is JsonValueKind.String?e.GetString()??"":e.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False?e.ToString():"";
    static string S(JsonElement e,string name)=>Text(Get(e,name));
    static string Continuation(JsonElement e)=>S(Get(e,"pagingInfo"),"continuationToken") is string s && s.Length>0?s:S(e,"continuationToken");
    static bool ValidId(string s)=>s.Length>0 && s.All(char.IsAsciiDigit);
    public static bool Legacy(Game g)=>GfwlTitles.Supports(g.Id) || g.Platform.Split(", ",StringSplitOptions.RemoveEmptyEntries).Any(d=>new[]{"Xbox360","Mobile","WindowsPhone","Win8","Windows8"}.Contains(d,StringComparer.OrdinalIgnoreCase));
    public static bool LegacyUnlocked(JsonElement achievement)
    {
        var unlocked = Get(achievement, "unlocked");
        if (unlocked.ValueKind is JsonValueKind.True or JsonValueKind.False) return unlocked.GetBoolean();
        var online = Get(achievement, "unlockedOnline");
        if (online.ValueKind == JsonValueKind.True) return true; // False means not earned online; offline progress may still exist.
        return DateTimeOffset.TryParse(S(achievement,"timeUnlocked"),out var when) && when.Year >= 2005;
    }
    async Task<JsonDocument> Read(string url,string version,CancellationToken token)
    {
        for(int attempt=0;;attempt++)
        {
            token.ThrowIfCancellationRequested();
            using var request=new HttpRequestMessage(HttpMethod.Get,url);
            request.Headers.Add("x-xbl-contract-version",version);
            using var response=await http.SendAsync(request,token);
            if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException("Xbox denied access. Reconnect your account before resuming.");
            if((response.StatusCode==HttpStatusCode.TooManyRequests || (int)response.StatusCode>=500) && attempt<3)
            {
                var wait=response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date-DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(5*Math.Pow(2,attempt));
                if(wait<TimeSpan.Zero)wait=TimeSpan.FromSeconds(1);
                if(wait>TimeSpan.FromMinutes(10))throw new HttpRequestException("Xbox requested a long cooldown. Resume later.");
                notice?.Invoke($"Xbox is busy; retrying in {Math.Ceiling(wait.TotalSeconds)} seconds…");
                await delay(wait,token);continue;
            }
            if(!response.IsSuccessStatusCode)throw new HttpRequestException($"Xbox returned HTTP {(int)response.StatusCode}.");
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        }
    }
    public async Task<Game[]> Games(CancellationToken token)
    {
        var games=new Dictionary<string,Game>();var seen=new HashSet<string>();string next="";
        do
        {
            string url=$"https://titlehub.xboxlive.com/users/xuid({Uri.EscapeDataString(xuid)})/titles/titleHistory/decoration/GamePass,TitleHistory,Achievement,Stats,detail,scid,alternateTitleId,titleRecord?maxItems=10000";
            if(next.Length>0)url+="&continuationToken="+Uri.EscapeDataString(next);
            using var doc=await Read(url,"2",token);var root=doc.RootElement;var titles=Get(root,"titles");
            if(titles.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("Xbox did not return a game list.");
            foreach(var t in titles.EnumerateArray())
            {
                string id=S(t,"titleId");if(!ValidId(id))throw new InvalidDataException("A game has no valid Title ID.");
                var devices=Get(t,"devices");string platform=devices.ValueKind==JsonValueKind.Array?string.Join(", ",devices.EnumerateArray().Select(Text)):"";
                int.TryParse(S(Get(t,"achievement"),"totalGamerscore"),out int score);
                games[id]=new Game(id,S(t,"name"),platform,score);
            }
            next=Continuation(root);if(next.Length>0 && !seen.Add(next))throw new InvalidDataException("Xbox repeated a game-list page.");
        }while(next.Length>0);
        return games.Values.ToArray();
    }
    public async Task<Row[]> Achievements(Game game,CancellationToken token)
    {
        var rows=new Dictionary<string,Row>();var seen=new HashSet<string>();string next="";bool legacy=Legacy(game);
        do
        {
            string url=$"https://achievements.xboxlive.com/users/xuid({Uri.EscapeDataString(xuid)})/{(legacy?"titleachievements":"achievements")}?titleId={Uri.EscapeDataString(game.Id)}&maxItems=1000";
            if(next.Length>0)url+="&continuationToken="+Uri.EscapeDataString(next);
            using var doc=await Read(url,legacy?"1":"4",token);var root=doc.RootElement;var items=Get(root,"achievements");
            if(items.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("Xbox did not return an achievement list.");
            foreach(var a in items.EnumerateArray())
            {
                string id=S(a,"id");if(string.IsNullOrWhiteSpace(id))throw new InvalidDataException("An achievement has no ID.");
                string title=S(a,"titleId");
                if(title.Length>0 && title!=game.Id)throw new InvalidDataException("Achievement Title ID does not match the requested game.");
                var associations=Get(a,"titleAssociations");
                if(associations.ValueKind==JsonValueKind.Array && associations.GetArrayLength()>0 && !associations.EnumerateArray().Any(t=>S(t,"id")==game.Id))
                    throw new InvalidDataException("Achievement title associations do not match the requested game.");
                string unlocked=legacy?S(a,"timeUnlocked"):S(Get(a,"progression"),"timeUnlocked");
                string state=S(a,"progressState");
                if(legacy)state=LegacyUnlocked(a)?"Achieved":"NotStarted";
                string score=S(a,"gamerscore");var rewards=Get(a,"rewards");
                if(rewards.ValueKind==JsonValueKind.Array)foreach(var r in rewards.EnumerateArray())if(S(r,"type").Equals("Gamerscore",StringComparison.OrdinalIgnoreCase))score=S(r,"value");
                string description=state=="Achieved"?S(a,"description"):S(a,"lockedDescription");if(description.Length==0)description=S(a,"description");
                double? rarity = a.TryGetProperty("rarity", out var rarityData) && rarityData.ValueKind == JsonValueKind.Object && rarityData.TryGetProperty("currentPercentage", out var percent) ? AchievementRarity.Parse(percent.ToString()) : null;
                var imageUrl = a.TryGetProperty("mediaAssets", out var assets) && assets.ValueKind == JsonValueKind.Array
                    ? assets.EnumerateArray().Where(asset => S(asset,"type") == "Icon").Select(asset => S(asset,"url")).FirstOrDefault(url => !string.IsNullOrWhiteSpace(url)) : null;
                var row=new Row(id,S(a,"name"),description,state=="Achieved"?"Unlocked":state is "NotStarted" or "InProgress"?"Locked":state,score,unlocked,S(a,"serviceConfigId"), GlobalUnlockPercentage: rarity, ImageUrl: imageUrl);
                if(rows.TryGetValue(id,out var old) && old!=row)throw new InvalidDataException("Conflicting duplicate achievement IDs.");
                rows[id]=row;
            }
            next=Continuation(root);if(next.Length>0 && !seen.Add(next))throw new InvalidDataException("Xbox repeated an achievement page.");
        }while(next.Length>0);
        if (legacy && rows.Count > 0)
        {
            // titleachievements is the definition catalogue. Read the user's earned list separately.
            seen.Clear(); next = "";
            do
            {
                string url=$"https://achievements.xboxlive.com/users/xuid({Uri.EscapeDataString(xuid)})/achievements?titleId={Uri.EscapeDataString(game.Id)}&maxItems=1000";
                if(next.Length>0)url+="&continuationToken="+Uri.EscapeDataString(next);
                using var earned=await Read(url,"1",token);
                var items=Get(earned.RootElement,"achievements");
                if(items.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("Xbox did not return legacy account progress.");
                foreach(var a in items.EnumerateArray())
                {
                    string id=S(a,"id"), titleId=S(a,"titleId");
                    if(titleId.Length>0 && titleId!=game.Id)throw new InvalidDataException("Earned achievement belongs to another title.");
                    if(!rows.TryGetValue(id,out var definition))throw new InvalidDataException("Earned achievement has no matching definition ID.");
                    // Presence in this earned-only endpoint proves an offline unlock even without a timestamp.
                    if(Get(a,"unlocked").ValueKind==JsonValueKind.False)throw new InvalidDataException("Earned endpoint returned an explicitly locked achievement.");
                    rows[id]=definition with { Status="Unlocked", UnlockedAt=S(a,"timeUnlocked"),
                        Description=S(a,"description").Length>0?S(a,"description"):definition.Description };
                }
                next=Continuation(earned.RootElement);
                if(next.Length>0 && !seen.Add(next))throw new InvalidDataException("Xbox repeated a legacy progress page.");
            } while(next.Length>0);
            foreach(var id in rows.Keys.ToArray())rows[id]=rows[id] with { LegacyProgressVerified=true };
        }
        if(rows.Count==0 && game.ExpectedScore>0)throw new InvalidDataException("Empty achievement response for a game with gamerscore; left pending.");
        return rows.Values.ToArray();
    }
    static async Task Atomic(string path,string value)
    {
        string temp=path+".tmp";
        await File.WriteAllTextAsync(temp,value,new UTF8Encoding(true));File.Move(temp,path,true);
    }
    static Saved? Load(string path,string account,string id)
    {
        try
        {
            var s=JsonSerializer.Deserialize<Saved>(File.ReadAllText(path));
            return s is {Version:1,Rows:not null,Game:not null} && s.Account==account && s.Game.Id==id && s.Rows.All(r=>r!=null && !string.IsNullOrWhiteSpace(r.Id)) && s.Rows.Select(r=>r.Id).Distinct().Count()==s.Rows.Length && (!Legacy(s.Game) || s.Rows.All(r=>r.LegacyProgressVerified))?s:null;
        }
        catch(Exception e) when(e is IOException or JsonException or UnauthorizedAccessException){return null;}
    }
    static string Csv(string? value)
    {
        string s=value??"";
        // Prevent spreadsheet formula execution in game/achievement text.
        if(s.Length>0 && "=+-@\t\r\n".Contains(s[0]))s="'"+s;
        return "\""+s.Replace("\"","\"\"")+"\"";
    }
    public static async Task WriteCsv(string folder,IEnumerable<Saved> saved)
    {
        var lines=new StringBuilder("Title ID,Title,Platform,Achievement ID,Achievement,Description,Status,Gamerscore,Unlocked UTC,Service Config ID,Scanned UTC\r\n");
        foreach(var s in saved.OrderBy(s=>s.Game.Name,StringComparer.OrdinalIgnoreCase).ThenBy(s=>s.Game.Id))
            foreach(var r in s.Rows)lines.AppendLine(string.Join(",",new[]{s.Game.Id,s.Game.Name,s.Game.Platform,r.Id,r.Name,r.Description,r.Status,r.Score,r.UnlockedAt,r.ServiceConfigId,s.ScannedAt.ToString("O")}.Select(Csv)));
        await Atomic(Path.Combine(folder,"all-achievements.csv"),lines.ToString());
    }
    public async Task<Result> Run(string baseFolder,bool refresh,Action<Progress> report,CancellationToken token)
    {
        string account=AccountKey(xuid),folder=Path.Combine(baseFolder,"AchievementLabs-export-"+account);
        Directory.CreateDirectory(folder);string cache=Path.Combine(folder,"titles");Directory.CreateDirectory(cache);
        // Also prevents two app instances writing the same export at once.
        using var guard=new FileStream(Path.Combine(folder,"export.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var saved=new Dictionary<string,Saved>();
        foreach(string path in Directory.EnumerateFiles(cache,"*.json"))
        {
            string id=Path.GetFileNameWithoutExtension(path);var s=Load(path,account,id);if(s!=null)saved[id]=s;
        }
        int done=0,total=0,added=0,skipped=0,failed=0;bool paused=false;var failures=new List<string>{"Title ID,Title,Error"};
        try
        {
            report(new(0,0,0,0,0,"Loading the full Xbox game list…"));
            var games=await Games(token);total=games.Length;
            foreach(var game in games)
            {
                token.ThrowIfCancellationRequested();
                if(!refresh && saved.ContainsKey(game.Id)){skipped++;done++;report(new(done,total,added,skipped,failed,"Already saved: "+game.Name));continue;}
                report(new(done,total,added,skipped,failed,"Reading: "+game.Name));
                Row[] rows;
                try {rows=await Achievements(game,token);}
                catch(UnauthorizedAccessException){throw;}
                catch(Exception e) when(e is HttpRequestException or InvalidDataException or JsonException || e is OperationCanceledException && !token.IsCancellationRequested)
                {
                    failed++;done++;failures.Add(string.Join(",",new[]{game.Id,game.Name,e is OperationCanceledException?"Request timed out; retry next scan.":e.Message}.Select(Csv)));
                    report(new(done,total,added,skipped,failed,"Could not read: "+game.Name));await delay(TimeSpan.FromSeconds(1),token);continue;
                }
                token.ThrowIfCancellationRequested();
                var entry=new Saved(1,account,game,DateTimeOffset.UtcNow,rows);
                await Atomic(Path.Combine(cache,game.Id+".json"),JsonSerializer.Serialize(entry));saved[game.Id]=entry;
                added++;done++;report(new(done,total,added,skipped,failed,"Saved: "+game.Name));
                await delay(TimeSpan.FromSeconds(1),token);
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){paused=true;}
        finally
        {
            // Completed titles survive cancellation, authentication failures and restarts.
            await WriteCsv(folder,saved.Values);
            var debug = Path.Combine(folder, "debug"); Directory.CreateDirectory(debug);
            await Atomic(Path.Combine(debug,"scan-errors.csv"),string.Join("\r\n",failures));
        }
        return new(added,skipped,failed,paused,folder);
    }
}
