using Newtonsoft.Json.Linq;

namespace AchievementLabs.Core;

/// <summary>Reads metadata only. True means event-based; false means title-based; null means unconfirmed.</summary>
public static class LibrarySupportDiscovery
{
    public static bool? Classify(JObject response)
    {
        if (response["achievements"] is not JArray { Count: > 0 } rows) return null;
        bool complete = true;
        foreach (var row in rows)
        {
            if (row is not JObject item || !Guid.TryParse((string?)item["serviceConfigId"], out var scid) || scid == Guid.Empty
                || item["progression"] is not JObject progress || progress["requirements"] is not JArray { Count: > 0 } requirements)
            { complete = false; continue; }
            foreach (var requirement in requirements)
            {
                if (requirement is not JObject value || !Guid.TryParse((string?)value["id"], out var id)) complete = false;
                else if (id != Guid.Empty) return true;
            }
        }
        if (!string.IsNullOrEmpty((string?)response["pagingInfo"]?["continuationToken"])) return null;
        return complete ? false : null;
    }

}
