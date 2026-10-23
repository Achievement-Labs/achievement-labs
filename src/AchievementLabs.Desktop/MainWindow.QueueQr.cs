using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AchievementLabs.Core;
using System.Runtime.InteropServices;
namespace AchievementLabs.Desktop;
public partial class MainWindow
{
    private static byte[] QueueQrPng(string frame)
    {
        using var generator=new QRCoder.QRCodeGenerator();using var data=generator.CreateQrCode(frame,QRCoder.QRCodeGenerator.ECCLevel.M);using var qr=new QRCoder.PngByteQRCode(data);return qr.GetGraphic(6);
    }
    public static string? ReadQueueQrImage(Stream stream)
    {
        using var source=SkiaSharp.SKBitmap.Decode(stream) ?? throw new InvalidDataException("Could not decode QR image.");
        if(source.Width<1||source.Height<1||(long)source.Width*source.Height>16_000_000)throw new InvalidDataException("QR image must be smaller than 16 megapixels.");
        using var bitmap=source.Copy(SkiaSharp.SKColorType.Bgra8888) ?? throw new InvalidDataException("Could not read QR pixels.");
        var pixels=new byte[bitmap.Width*bitmap.Height*4];
        for(var y=0;y<bitmap.Height;y++)Marshal.Copy(IntPtr.Add(bitmap.GetPixels(),y*bitmap.RowBytes),pixels,y*bitmap.Width*4,bitmap.Width*4);
        var reader=new ZXing.BarcodeReaderGeneric { AutoRotate=true,Options=new ZXing.Common.DecodingOptions { TryHarder=true,PossibleFormats=[ZXing.BarcodeFormat.QR_CODE] } };
        return reader.Decode(new ZXing.RGBLuminanceSource(pixels,bitmap.Width,bitmap.Height,ZXing.RGBLuminanceSource.BitmapFormat.BGRA32))?.Text;
    }
    private async void ExportQueueQr(object? sender,RoutedEventArgs e)
    {
        try {
            var json=model.XboxQueue.ExportQueueSyncFile();var owner=Newtonsoft.Json.Linq.JObject.Parse(json).Value<string>("ownerXuid")!;var frames=QueueQrTransfer.Frames(json,owner);
            var dialog=new Window { Title="Queue QR transfer",Width=570,Height=760,MinWidth=470,MinHeight=600,WindowStartupLocation=WindowStartupLocation.CenterOwner };
            var image=new Avalonia.Controls.Image { Width=410,Height=410,Stretch=Stretch.Uniform };var status=new TextBlock();var index=0;Bitmap? bitmap=null;
            void Show(){using var stream=new MemoryStream(QueueQrPng(frames[index]));var old=bitmap;bitmap=new Bitmap(stream);image.Source=bitmap;old?.Dispose();status.Text=$"Queue code {index+1} of {frames.Length}";}
            var timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(0.5) };var cycle=new CheckBox { Content="Cycle automatically",IsChecked=true };timer.Tick+=(_,_)=>{if(cycle.IsChecked==true){index=(index+1)%frames.Length;Show();}};
            var previous=new Button { Content="Previous" };previous.Click+=(_,_)=>{index=(index+frames.Length-1)%frames.Length;Show();};var next=new Button { Content="Next" };next.Click+=(_,_)=>{index=(index+1)%frames.Length;Show();};
            var save=new Button { Content="Save all QR images" };save.Click+=async(_,_)=>{try{var folders=await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title="Save queue QR images" });if(folders.FirstOrDefault()?.TryGetLocalPath() is not {} folder)return;var prefix="Labs-Queue-"+Guid.NewGuid().ToString("N")[..8];for(var i=0;i<frames.Length;i++)await File.WriteAllBytesAsync(Path.Combine(folder,$"{prefix}-{i+1:D3}.png"),QueueQrPng(frames[i]));status.Text=$"Saved {frames.Length} QR images.";}catch(Exception ex){status.Text=ex.Message;}};
            var close=new Button { Content="Close" };close.Click+=(_,_)=>dialog.Close();
            dialog.Content=new ScrollViewer { Content=new StackPanel { Margin=new Thickness(20),Spacing=12,Children={new TextBlock { Text="On mobile, open Auto Unlock → Saved game queues → Scan queue QR. Keep the camera on these codes until all are collected.",TextWrapping=TextWrapping.Wrap },image,status,new StackPanel { Orientation=Avalonia.Layout.Orientation.Horizontal,Spacing=10,Children={previous,next,cycle} },save,new TextBlock { Text="Includes saved games, achievements, progress and delays for this account. No sign-in or event tokens. Imported queues require review and Start.",TextWrapping=TextWrapping.Wrap },close} } };
            Show();timer.Start();try{await dialog.ShowDialog(this);}finally{timer.Stop();image.Source=null;bitmap?.Dispose();}
        }catch(Exception ex){model.Notice=ex.Message;}
    }
    private async void ImportQueueQr(object? sender,RoutedEventArgs e)
    {
        try {
            var json=model.XboxQueue.ExportQueueSyncFile();var owner=Newtonsoft.Json.Linq.JObject.Parse(json).Value<string>("ownerXuid")!;
            var files=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title="Select all queue QR images shared from mobile or PC",AllowMultiple=true,FileTypeFilter=[new FilePickerFileType("QR images") { Patterns=["*.png","*.jpg","*.jpeg"] }] });
            if(files.Count==0)return;if(files.Count>256)throw new InvalidDataException("Select at most 256 queue QR images.");
            var collector=new QueueQrTransfer.Collector();string? complete=null;
            foreach(var file in files){await using var input=await file.OpenReadAsync();using var bytes=new MemoryStream();var buffer=new byte[8192];int n;while((n=await input.ReadAsync(buffer))>0){if(bytes.Length+n>16*1024*1024)throw new InvalidDataException("QR image is too large.");await bytes.WriteAsync(buffer.AsMemory(0,n));}bytes.Position=0;var frame=ReadQueueQrImage(bytes)??throw new InvalidDataException("No readable QR in "+file.Name);complete=collector.Add(frame,owner);model.Notice=collector.Progress;await Task.Yield();}
            if(complete==null)throw new InvalidDataException(collector.Progress+". Import all images from the same transfer together.");
            var transfer=QueueTransfer.Parse(complete,owner);var review=new Window { Title="Review queue import",Width=500,Height=350,WindowStartupLocation=WindowStartupLocation.CenterOwner };
            var import=new Button { Content="Import saved queues" };var cancel=new Button { Content="Cancel" };var accepted=false;import.Click+=(_,_)=>{accepted=true;review.Close();};cancel.Click+=(_,_)=>review.Close();
            review.Content=new StackPanel { Margin=new Thickness(24),Spacing=16,Children={new TextBlock { Text=$"Import {transfer.Games.Count} game queues / {transfer.Games.Sum(x=>x.Items.Count)} achievements?",TextWrapping=TextWrapping.Wrap },new TextBlock { Text="Matching saved game queues will be replaced after verifying achievements for this account. Nothing starts automatically.",TextWrapping=TextWrapping.Wrap },import,cancel} };await review.ShowDialog(this);if(!accepted)return;
            await model.XboxQueue.ImportQueueSyncFileAsync(complete);model.Notice=model.XboxQueue.StatusText;
        }catch(Exception ex){model.Notice=ex.Message;}
    }
}
