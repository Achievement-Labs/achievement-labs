using Newtonsoft.Json.Linq;

namespace AchievementLabs.Core;

internal static class EventCatalogSchema
{
    private static readonly HashSet<string> TitleKeys = new(StringComparer.Ordinal)
        { "TitleId", "TitleName", "Devices", "ServiceConfigId", "Enabled", "FullySupported", "PartiallySupported", "Achievements" };
    public static void ValidateTitle(JObject title)
    {
        if (title.Properties().Any(p => !TitleKeys.Contains(p.Name)) || title["Achievements"] is not JObject achievements)
            throw new InvalidDataException("Title data contains non-runtime fields or invalid Achievements.");
        foreach (var achievement in achievements.Properties())
        {
            if (!EventCatalogStorage.ValidId(achievement.Name) || achievement.Value is not JObject mapping || mapping.Count == 0)
                throw new InvalidDataException("Invalid achievement mapping.");
            var direct = mapping.Properties().All(p => p.Value is JObject op && op["ReplacementType"] != null);
            if (direct) ValidateRequest(mapping);
            else
            {
                int number = 0;
                foreach (var request in mapping.Properties())
                {
                    if (request.Name != "Request" + ++number || request.Value is not JObject operations)
                        throw new InvalidDataException("Requests must be contiguous Request1..RequestN objects.");
                    ValidateRequest(operations);
                }
            }
        }
    }
    private static void ValidateRequest(JObject request)
    {
        if (request.Count == 0) throw new InvalidDataException("Empty replacement request.");
        foreach (var property in request.Properties())
        {
            if (property.Value is not JObject op || op["Target"]?.Type != JTokenType.String || string.IsNullOrEmpty((string?)op["Target"]))
                throw new InvalidDataException("Only replacement operation objects belong inside a request.");
            var type = (string?)op["ReplacementType"];
            if (type is not ("Replace" or "RangeInt" or "RangeFloat" or "StupidFuckingLDAPTimestamp"))
                throw new InvalidDataException("Unsupported replacement type.");
            if (type == "Replace" && op["Replacement"]?.Type is not (JTokenType.String or JTokenType.Integer or JTokenType.Float))
                throw new InvalidDataException("Invalid replacement value.");
        }
    }
}
