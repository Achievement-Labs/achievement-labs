using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;

namespace AchievementLabs.MultiSelect;

// Xbox One Ghosts has no achievement 51. Its DLC Xbox IDs are 52..92.
// The observed legacy event catalog numbers its templates sequentially 1..91.
public static class EventIdMapping
{
    public const string GhostsTitleId="572802557";
    enum Shape { Unknown, Sequential, CorrectXboxIds }
    static readonly ConcurrentDictionary<string,Shape> Shapes=new();
    static readonly string[] Sequential=Enumerable.Range(1,91).Select(i=>i.ToString(CultureInfo.InvariantCulture)).ToArray();
    static readonly string[] XboxIds=Enumerable.Range(1,50).Concat(Enumerable.Range(52,41)).Select(i=>i.ToString(CultureInfo.InvariantCulture)).ToArray();
    static bool Same(IEnumerable<string> actual,string[] expected)
    {
        var ids=actual.ToArray();return ids.Length==expected.Length && new HashSet<string>(ids,StringComparer.Ordinal).SetEquals(expected);
    }
    public static async Task<T> NormalizeCatalog<T>(Task<T> source,string titleId)
    {
        if(EditionMapping.Supports(titleId))return await EditionMapping.NormalizeCatalog(source,titleId).ConfigureAwait(false);
        T value=await source.ConfigureAwait(false);
        if(titleId!=GhostsTitleId)return value;
        Shapes[titleId]=Shape.Unknown;
        PropertyInfo? property=value?.GetType().GetProperty("AchievementIds");
        if(property?.GetValue(value) is not IEnumerable<string> ids)throw new InvalidOperationException("Ghosts event catalog format is not recognised. No ID correction was applied.");
        if(Same(ids,Sequential))
        {
            property.SetValue(value,XboxIds.ToArray());
            Shapes[titleId]=Shape.Sequential;
        }
        else if(Same(ids,XboxIds))Shapes[titleId]=Shape.CorrectXboxIds;
        else throw new InvalidOperationException("Ghosts event catalog IDs have changed. Reload the title after its event mapping is checked.");
        return value;
    }
    public static string PayloadId(string titleId,string xboxId)
    {
        if(EditionMapping.Supports(titleId))return EditionMapping.PayloadId(titleId,xboxId);
        if(titleId!=GhostsTitleId)return xboxId;
        if(!Shapes.TryGetValue(titleId,out var shape) || shape==Shape.Unknown)
            throw new InvalidOperationException("Load or refresh Ghosts achievements before submitting, so its event mapping can be verified.");
        if(!int.TryParse(xboxId,NumberStyles.None,CultureInfo.InvariantCulture,out int id) || id<1 || id>92 || id==51)
            throw new InvalidOperationException("This is not a recognised Ghosts Xbox achievement ID.");
        return (shape==Shape.Sequential && id>=52 ? id-1 : id).ToString(CultureInfo.InvariantCulture);
    }
    public static Task<T> ValidatePayloads<T>(Task<T> source,string titleId,string xboxId)
        => EditionMapping.ValidatePayloads(source,titleId,xboxId);
}
