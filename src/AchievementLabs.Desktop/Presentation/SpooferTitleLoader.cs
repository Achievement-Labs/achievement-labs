using System.Reflection;
using System.Runtime.CompilerServices;

namespace AchievementLabs.Desktop.Presentation;

public static class SpooferTitleLoader
{
    const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    sealed class State {public bool Loading;}
    static readonly ConditionalWeakTable<object,State> States=new();
    static object? Get(object model,string property)=>model.GetType().GetProperty(property,Flags)?.GetValue(model);
    static void Set(object model,string property,object? value)=>model.GetType().GetProperty(property,Flags)?.SetValue(model,value);
    public static void Begin(object model)=>_=LoadAsync(model);
    public static async Task LoadAsync(object model)
    {
        var state=States.GetOrCreateValue(model);
        if(state.Loading)return;
        var game=Get(model,"SelectedGame");
        var id=game==null?null:Get(game,"Id")?.ToString();
        if(string.IsNullOrEmpty(id) || id!=Get(model,"PresenceTitleId")?.ToString() || (Get(model,"IsSpoofer") is not true && Get(model,"IsQueues") is not true))return;
        state.Loading=true;
        try
        {
            Set(model,"SpoofTitleName",Get(game!,"Name")?.ToString()??id);
            Set(model,"SpoofTitleDetails","Title ID: "+id+"\nLoading Xbox title information…");
            Set(model,"SpoofTitleImageUrl",Get(game!,"ImageUrl"));
            if((AppContext.TryGetSwitch("AchievementLabs.OfflineChecks",out var offline) && offline) || Get(model,"CanLookupSpoofTitle") is not true){Set(model,"SpoofTitleDetails","Title ID: "+id+"\nConnect Xbox and use Look up title for full details.");return;}
            var task=(Task?)model.GetType().GetMethod("LookupSpoofTitleAsync",Flags)?.Invoke(model,null);
            if(task!=null)await task;
            if(Get(model,"PresenceTitleId")?.ToString()==id && Get(model,"SpoofTitleDetails")?.ToString()?.Contains("Loading Xbox title information…")==true)
                Set(model,"SpoofTitleDetails","Title ID: "+id+"\nFull details unavailable. Use Look up title to retry.");
        }
        catch
        {
            if(Get(model,"PresenceTitleId")?.ToString()==id)
                Set(model,"Notice","Could not load the title details automatically. Use Look up title to retry.");
        }
        finally{state.Loading=false;}
    }
}
