using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
namespace AchievementLabs.Core;

/// Portable queue data only. Account authorization and event tokens are never encoded.
public static class QueueQrTransfer
{
    public const int MaximumBytes = 128 * 1024;
    public static string[] Frames(string json, string owner)
    {
        var transfer=QueueTransfer.Parse(json,owner);
        if(transfer.Games.Count==0)throw new InvalidDataException("Save a game queue before exporting.");
        var data=Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(transfer,Formatting.None));
        if(data.Length>MaximumBytes)throw new InvalidDataException("These queues are too large for QR transfer. Use the queue sync file instead.");
        var digest=Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();var text=Convert.ToBase64String(data);var count=(text.Length+799)/800;
        return Enumerable.Range(0,count).Select(i=>$"ALQ1|{digest}|{i}|{count}|{text.Substring(i*800,Math.Min(800,text.Length-i*800))}").ToArray();
    }
    public sealed class Collector
    {
        private string? digest;private int total;private readonly Dictionary<int,string> pieces=[];
        public string Progress=>$"{pieces.Count} / {total} queue codes collected";
        public string? Add(string frame,string owner)
        {
            if(frame.Length>1000)throw new InvalidDataException("QR code is too large.");var a=frame.Split('|');
            if(a.Length!=5||a[0]!="ALQ1"||!System.Text.RegularExpressions.Regex.IsMatch(a[1],"^[a-f0-9]{64}$")||!int.TryParse(a[2],out var i)||!int.TryParse(a[3],out var n)||n<1||n>256||i<0||i>=n||a[4].Length<1||a[4].Length>800)throw new InvalidDataException("Choose Achievement Labs queue QR codes.");
            if(digest!=null&&(digest!=a[1]||total!=n))throw new InvalidDataException("Different queue transfer detected. Start a new import.");
            if(pieces.TryGetValue(i,out var previous)&&previous!=a[4])throw new InvalidDataException("Conflicting QR code.");
            digest=a[1];total=n;pieces[i]=a[4];if(pieces.Count!=total)return null;
            var data=Convert.FromBase64String(string.Concat(Enumerable.Range(0,total).Select(x=>pieces[x])));
            if(data.Length>MaximumBytes||Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()!=digest)throw new InvalidDataException("Queue QR checksum failed.");
            var transfer=QueueTransfer.Parse(new UTF8Encoding(false,true).GetString(data),owner);
            return JsonConvert.SerializeObject(transfer);
        }
    }
}
