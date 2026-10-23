using System.IO;
using Newtonsoft.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace AchievementLabs.Core;
public sealed class QueueTransfer
{
    [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonProperty("kind")] public string Kind { get; set; } = "AchievementLabsQueueTransfer";
    [JsonProperty("ownerXuid")] public string OwnerXuid { get; set; } = "";
    [JsonProperty("games")] public List<TransferGame> Games { get; set; } = [];
    public void Validate(string owner)
    {
        static bool Numeric(string? text) => text != null && Regex.IsMatch(text,@"^\d{1,20}$");
        static bool Delay(double value) => double.IsFinite(value) && value >= 0 && value <= 31536000;
        if (SchemaVersion != 1 || Kind != "AchievementLabsQueueTransfer" || !Numeric(owner) || OwnerXuid != owner || Games == null || Games.Count > 500 || Games.Any(x=>x == null || x.Items == null || x.Items.Any(i=>i == null)) || Games.Select(x=>x.TitleId).Distinct().Count()!=Games.Count || Games.Sum(x=>x.Items?.Count ?? 0)>50000) throw new InvalidDataException("Queue transfer account, version or size does not match.");
        foreach (var game in Games)
        {
            if (!Numeric(game.TitleId) || game.Name == null || game.Name.Length>256 || game.Items == null || game.Items.Count==0 || game.Items.Select(x=>x.AchievementId).Distinct().Count()!=game.Items.Count) throw new InvalidDataException("Invalid transferred game queue.");
            foreach (var item in game.Items)
                if (!Numeric(item.AchievementId) || item.Name == null || item.Name.Length>256 || item.Status == null || item.Status.Length>512 || !Delay(item.DelaySeconds) || item.RemainingDelaySeconds is double left && !Delay(left)) throw new InvalidDataException("Invalid achievement or delay in queue transfer.");
        }
    }
    public static QueueTransfer Parse(string json,string owner)
    {
        if (Encoding.UTF8.GetByteCount(json)>8*1024*1024) throw new InvalidDataException("Queue sync file exceeds 8 MB.");
        var value=JsonConvert.DeserializeObject<QueueTransfer>(json) ?? throw new InvalidDataException("Invalid queue file.");value.Validate(owner);return value;
    }
}
public sealed class TransferGame
{
    [JsonProperty("titleId")] public string TitleId { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("items")] public List<TransferAchievement> Items { get; set; } = [];
}
public sealed class TransferAchievement
{
    [JsonProperty("achievementId")] public string AchievementId { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("delaySeconds")] public double DelaySeconds { get; set; }
    [JsonProperty("remainingDelaySeconds")] public double? RemainingDelaySeconds { get; set; }
    [JsonProperty("status")] public string Status { get; set; } = "Waiting";
}
