using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace AchievementLabs.Desktop.Presentation;

using Image = Avalonia.Controls.Image;

public sealed class WideArtwork : IDisposable
{
    public const string StateOfDecayArtwork="https://xboxwire.thesourcemediaassets.com/sites/2/2020/02/SOD2-JuggEd_Social_1920x1080_JPG.jpg";
    static readonly HttpClient Http=new(new HttpClientHandler {AutomaticDecompression=DecompressionMethods.All}) {Timeout=TimeSpan.FromSeconds(15)};
    static readonly Dictionary<string,string> UrlCache=new();
    readonly object model;readonly Image fallback,target;readonly bool preview;
    CancellationTokenSource? request;Bitmap? owned;string title="";bool disposed,hasWide;
    public WideArtwork(Window owner,object model,Image fallback,Image target,bool preview)
    {
        this.model=model;this.fallback=fallback;this.target=target;this.preview=preview || (AppContext.TryGetSwitch("AchievementLabs.OfflineChecks",out var offline) && offline);
        if(model is INotifyPropertyChanged npc)npc.PropertyChanged+=Changed;
        fallback.PropertyChanged+=FallbackChanged;
        owner.Closed+=(_,_)=>Dispose();
        _=UpdateAsync();
    }
    void Changed(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName is "PresenceTitleId" or "SpoofTitleImageUrl")_=UpdateAsync();
    }
    void FallbackChanged(object? sender,Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if(e.Property==Image.SourceProperty && !hasWide)target.Source=fallback.Source;
    }
    async Task UpdateAsync()
    {
        var id=model.GetType().GetProperty("PresenceTitleId")?.GetValue(model)?.ToString()??"";
        if(!uint.TryParse(id,out var number)||number==0)return;
        // A property refresh for the same title must not restart an in-flight download.
        if(title==id && (request!=null || hasWide))return;
        title=id;hasWide=false;request?.Cancel();request?.Dispose();
        var cts=new CancellationTokenSource();request=cts;
        target.Source=null;owned?.Dispose();owned=null;
        try
        {
            if(preview){target.Source=fallback.Source;return;}
            byte[]? bytes=null;
            if(id=="1938834516")
            {
                var local=Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!,"sod2-full-artwork.jpg");
                bytes=File.Exists(local)?await File.ReadAllBytesAsync(local,cts.Token):await FetchAsync(StateOfDecayArtwork,cts.Token);
            }
            else if(!preview)
            {
                var url=await ResolveAsync(id,cts.Token);
                if(url!=null)bytes=await FetchAsync(url,cts.Token);
            }
            if(bytes==null || disposed || request!=cts || cts.IsCancellationRequested)return;
            using var stream=new MemoryStream(bytes);
            var bitmap=new Bitmap(stream);
            if(disposed || request!=cts){bitmap.Dispose();return;}
            owned=bitmap;hasWide=true;target.Source=bitmap;
        }
        catch(OperationCanceledException) { }
        catch { if(request==cts && !disposed)target.Source=fallback.Source; }
        finally
        {
            if(request==cts){request=null;cts.Dispose();if(!hasWide && !disposed)target.Source=fallback.Source;}
        }
    }
    public static string? SelectWide(JsonElement product)
    {
        if(!product.TryGetProperty("LocalizedProperties",out var locales))return null;
        var candidates=new List<(string Purpose,int Width,int Height,string Url)>();
        foreach(var locale in locales.EnumerateArray())
        {
            if(!locale.TryGetProperty("Images",out var images))continue;
            foreach(var image in images.EnumerateArray())
            {
                if(!image.TryGetProperty("Width",out var w)||!image.TryGetProperty("Height",out var h)||!image.TryGetProperty("Uri",out var u))continue;
                var width=w.GetInt32();var height=h.GetInt32();var url=u.GetString()??"";
                var purpose=image.TryGetProperty("ImagePurpose",out var p)?p.GetString()??"":"";
                if(width>=1280 && height>0 && (double)width/height>=1.5 && (double)width/height<=2.3)
                    candidates.Add((purpose,width,height,url));
            }
        }
        string[] priority={"TitledHeroArt","SuperHeroArt","Hero","FeaturePromotionalArt"};
        var best=candidates.Where(c=>priority.Contains(c.Purpose))
            .OrderBy(c=>Array.IndexOf(priority,c.Purpose)).ThenByDescending(c=>c.Width).FirstOrDefault();
        return Normalize(best.Url);
    }
    static string? Normalize(string? url)
    {
        if(string.IsNullOrWhiteSpace(url))return null;
        if(url.StartsWith("//"))url="https:"+url;
        return Uri.TryCreate(url,UriKind.Absolute,out var parsed)&&parsed.Scheme=="https"?url:null;
    }
    public static async Task<string?> ResolveAsync(string id,CancellationToken ct)
    {
        if(!uint.TryParse(id,out _))return null;
        lock(UrlCache)if(UrlCache.TryGetValue(id,out var cached))return cached;
        using var stream=await Http.GetStreamAsync($"https://displaycatalog.mp.microsoft.com/v7.0/products/lookup?alternateId=XboxTitleId&Value={id}&market=US&languages=en-US",ct);
        using var json=await JsonDocument.ParseAsync(stream,cancellationToken:ct);
        if(!json.RootElement.TryGetProperty("Products",out var products))return null;
        foreach(var product in products.EnumerateArray())
        {
            var found=SelectWide(product);
            if(found==null && product.TryGetProperty("ProductId",out var productId))
            {
                using var detail=await Http.GetStreamAsync($"https://displaycatalog.mp.microsoft.com/v7.0/products/{Uri.EscapeDataString(productId.GetString()??"")}?market=US&languages=en-US",ct);
                using var full=await JsonDocument.ParseAsync(detail,cancellationToken:ct);
                if(full.RootElement.TryGetProperty("Product",out var fullProduct))found=SelectWide(fullProduct);
            }
            if(found!=null){lock(UrlCache)UrlCache[id]=found;return found;}
        }
        return null;
    }
    static async Task<byte[]> FetchAsync(string url,CancellationToken ct)
    {
        using var response=await Http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,ct);
        response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength is >16_777_216)throw new InvalidDataException("Artwork exceeds size limit.");
        using var input=await response.Content.ReadAsStreamAsync(ct);
        using var output=new MemoryStream();var buffer=new byte[65536];int count;
        while((count=await input.ReadAsync(buffer,ct))>0){if(output.Length+count>16_777_216)throw new InvalidDataException("Artwork exceeds size limit.");output.Write(buffer,0,count);}
        return output.ToArray();
    }
    public void Dispose()
    {
        disposed=true;request?.Cancel();request?.Dispose();request=null;
        if(model is INotifyPropertyChanged npc)npc.PropertyChanged-=Changed;
        fallback.PropertyChanged-=FallbackChanged;target.Source=null;owned?.Dispose();owned=null;
    }
}
