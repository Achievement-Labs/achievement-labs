using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AchievementLabs.Core;

/// <summary>Account-partitioned display snapshots. Contains no authorization or sign-in state.</summary>
public sealed class AccountLibraryCache(string directory)
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(10);
    public sealed record CachedTitle(string Id, string Name, string[] Devices, int Completed, int Total, int Score,
        bool ProgressKnown, bool? Mythic, string? Image, DateTime? LastPlayed);
    public sealed record Snapshot(int Version, string Account, DateTimeOffset CheckedAt, CachedTitle[] Titles,
        int? Mythics, Dictionary<string,string> Profile)
    {
        public bool Fresh => DateTimeOffset.UtcNow - CheckedAt <= FreshFor;
        public TitlesList ToTitles() => new() { AchievementSummary = new() { MythicsUnlocked = Mythics },
            Titles = Titles.Select(t => new Title { TitleId=t.Id,Name=t.Name,Devices=t.Devices.ToList(),DisplayImage=t.Image,
                Achievement=t.ProgressKnown ? new() { CurrentAchievements=t.Completed,TotalAchievements=t.Total,CurrentGamerscore=t.Score,IsMythic=t.Mythic } : null,
                TitleHistory=t.LastPlayed is { } played ? new() { LastTimePlayed=played } : null }).ToList() };
    }
    private static string Key(string account)
    {
        if (!ulong.TryParse(account,out var id) || id == 0) throw new ArgumentException("Invalid account identity.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account))).ToLowerInvariant();
    }
    public async Task<Snapshot?> LoadAsync(string account, CancellationToken ct)
    {
        var key = Key(account); var path=Path.Combine(directory,"library-snapshot-"+key+".json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024) return null;
            var saved=JsonSerializer.Deserialize<Snapshot>(await File.ReadAllTextAsync(path,ct));
            if (saved is not { Version:1, Titles:not null, Profile:not null } || saved.Account!=key || saved.Titles.Length>20000 ||
                saved.CheckedAt > DateTimeOffset.UtcNow.AddMinutes(1) || saved.CheckedAt < DateTimeOffset.UtcNow.AddDays(-30) ||
                saved.Titles.Any(t=>t is null || !uint.TryParse(t.Id,out var id) || id==0 || t.Name is null || t.Devices is null || t.Completed<0 || t.Total<0 || t.Score<0) ||
                saved.Titles.Select(t=>t.Id).Distinct().Count()!=saved.Titles.Length) return null;
            return saved;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public async Task SaveAsync(string account, TitlesList titles, BasicProfile? profile, CancellationToken ct)
    {
        var key=Key(account);
        if (titles.Xuid is { Length:>0 } owner && owner!=account) throw new InvalidDataException("Library account mismatch.");
        var rows=titles.Titles.Where(t=>uint.TryParse(t.TitleId,out var id) && id>0).GroupBy(t=>t.TitleId).Select(g=>g.First()).Select(t=>new CachedTitle(
            t.TitleId!,t.Name??t.TitleId!,t.Devices.ToArray(),t.Achievement?.CurrentAchievements??0,t.Achievement?.TotalAchievements??0,
            t.Achievement?.CurrentGamerscore??0,t.Achievement!=null,t.Achievement?.BaseComplete,t.DisplayImage,t.TitleHistory?.LastTimePlayed)).ToArray();
        var previous=profile==null ? await LoadAsync(account,ct) : null;
        var labels=(profile?.ProfileUsers.FirstOrDefault()?.Settings??[]).Where(p=>p.Id is "Gamertag" or "Gamerscore" or "GameDisplayPicRaw")
            .GroupBy(p=>p.Id!).ToDictionary(g=>g.Key,g=>g.First().Value??"");
        if (labels.Count==0 && previous!=null) labels=previous.Profile;
        var json=JsonSerializer.Serialize(new Snapshot(1,key,DateTimeOffset.UtcNow,rows,titles.AchievementSummary?.MythicsUnlocked,labels));
        if (Encoding.UTF8.GetByteCount(json)>16*1024*1024) throw new InvalidDataException("Library snapshot too large.");
        Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,"library-snapshot-"+key+".json");var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try { await File.WriteAllTextAsync(temporary,json,ct);File.Move(temporary,path,true); }
        finally { if(File.Exists(temporary))File.Delete(temporary); }
    }
}
