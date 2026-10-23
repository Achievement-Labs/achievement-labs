using System.Reflection;
namespace AchievementLabs.MultiSelect;

public static class TokenPresentation
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    public static bool CanInstall(object model,object session,string previous,bool manual)
    {
        var input=model.GetType().GetProperty("EventTokenInput",Flags);
        var current=input?.GetValue(model) as string??"";
        var active=session.GetType().GetProperty("EventsToken",Flags)?.GetValue(session) as string??"";
        // Preserve both manual edits and saved/manual active tokens, including when the box is empty.
        return manual || (string.IsNullOrWhiteSpace(current) || current==previous) &&
            (string.IsNullOrWhiteSpace(active) || active==previous);
    }
    public static bool Install(object model,object session,AutomaticEventToken.Grant grant,string previous,bool manual)
    {
        if(!CanInstall(model,session,previous,manual))return false;
        model.GetType().GetProperty("EventTokenInput",Flags)?.SetValue(model,grant.Value);
        session.GetType().GetProperty("EventsToken",Flags)!.SetValue(session,grant.Value);
        return true;
    }
    public static void ClearOwnInput(object model,string installed)
    {
        var input=model.GetType().GetProperty("EventTokenInput",Flags);
        if(installed.Length>0 && Equals(input?.GetValue(model),installed))input?.SetValue(model,"");
    }
}
