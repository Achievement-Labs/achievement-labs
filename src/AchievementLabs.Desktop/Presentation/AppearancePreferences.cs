using System.Text.Json;

namespace AchievementLabs.Desktop.Presentation;

public sealed class AppearancePreferences
{
    public string BackgroundMode {get;set;}="Game artwork";
    public bool PersistentGameArtwork {get;set;}=true;
    public string CustomBackgroundPath {get;set;}="";
    public double GlassTransparency {get;set;}=35;
    public double GlassBlur {get;set;}=30;
    public string GlassColour {get;set;}="#111E27";
    public bool OledMode {get;set;}
    public void Normalize()
    {
        if(!new[]{"Game artwork","Custom image","No artwork"}.Contains(BackgroundMode))BackgroundMode="Game artwork";
        GlassTransparency=double.IsFinite(GlassTransparency)?Math.Clamp(GlassTransparency,0,85):35;
        GlassBlur=double.IsFinite(GlassBlur)?Math.Clamp(GlassBlur,0,80):30;
        GlassColour=System.Text.RegularExpressions.Regex.IsMatch(GlassColour??"","^#[0-9a-fA-F]{6}$")?GlassColour!.ToUpperInvariant():"#111E27";
        CustomBackgroundPath??="";
    }
    public static AppearancePreferences Load(string path)
    {
        try{var p=JsonSerializer.Deserialize<AppearancePreferences>(File.ReadAllText(path))??new();p.Normalize();return p;}catch{return new();}
    }
    public void Save(string path)
    {
        Normalize();Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp=path+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(this,new JsonSerializerOptions {WriteIndented=true}));File.Move(temp,path,true);
    }
}
