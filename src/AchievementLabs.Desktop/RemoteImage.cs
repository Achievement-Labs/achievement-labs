using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace AchievementLabs.Desktop;

public sealed class RemoteImage : Avalonia.Controls.Image
{
    private static readonly HttpClient Client = CreateClient();
    public static readonly StyledProperty<string?> UrlProperty = AvaloniaProperty.Register<RemoteImage, string?>(nameof(Url));
    private int loadVersion;
    private Bitmap? ownedBitmap;

    static RemoteImage() => UrlProperty.Changed.AddClassHandler<RemoteImage>((image, _) => image.BeginLoad());

    public string? Url { get => GetValue(UrlProperty); set => SetValue(UrlProperty, value); }

    private void BeginLoad()
    {
        var version = ++loadVersion;
        ReplaceBitmap(null, version);
        _ = LoadAsync(Url, version);
    }

    private async Task LoadAsync(string? value, int version)
    {
        value = NormalizeUrl(value);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            ReplaceBitmap(null, version);
            return;
        }
        try
        {
            if (uri.IsFile)
            {
                var localBitmap = await Task.Run(() => File.Exists(uri.LocalPath) ? new Bitmap(uri.LocalPath) : null);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ReplaceBitmap(localBitmap, version));
                return;
            }
            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) { ReplaceBitmap(null, version); return; }
            var bitmap = await FetchBitmapAsync(Client, uri);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ReplaceBitmap(bitmap, version));
        }
        catch { }
    }

    internal static async Task<Bitmap> FetchBitmapAsync(HttpClient http, Uri uri, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 8_388_608) throw new InvalidDataException("Artwork is too large.");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream();
        var buffer = new byte[16384]; int count;
        while ((count = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (bytes.Length + count > 8_388_608) throw new InvalidDataException("Artwork is too large.");
            bytes.Write(buffer, 0, count);
        }
        bytes.Position = 0;
        return await Task.Run(() => new Bitmap(bytes), ct);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AchievementLabs/0.9");
        return client;
    }

    private static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.StartsWith("//", StringComparison.Ordinal)) return "https:" + value;
        return value.Replace("{width}", "512", StringComparison.OrdinalIgnoreCase)
            .Replace("{height}", "512", StringComparison.OrdinalIgnoreCase)
            .Replace("{formatter}", "", StringComparison.OrdinalIgnoreCase)
            .Replace("{ext}", "jpg", StringComparison.OrdinalIgnoreCase);
    }

    private void ReplaceBitmap(Bitmap? bitmap, int version)
    {
        if (version != loadVersion) { bitmap?.Dispose(); return; }
        var previous = ownedBitmap;
        ownedBitmap = bitmap;
        Source = bitmap;
        previous?.Dispose();
    }
}
