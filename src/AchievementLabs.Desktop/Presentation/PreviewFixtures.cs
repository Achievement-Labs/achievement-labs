using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AchievementLabs.Desktop.Presentation;

/// <summary>Local, generated artwork for the explicit offline visual-check mode.</summary>
internal static class PreviewFixtures
{
    internal static string ArtworkDirectory {get;}=Path.Combine(Path.GetTempPath(),"AchievementLabs-VisualFixtures");
    internal static void EnsureArtwork()
    {
        Directory.CreateDirectory(ArtworkDirectory);
        foreach(var (file,name,colour) in new[]{("sod2-full-artwork.jpg","STATE OF DECAY 2","#31403B"),("forza.jpg","FORZA HORIZON 3","#20465D"),("halo.jpg","HALO: THE MASTER CHIEF COLLECTION","#343353")})
        {
            var labels=new StackPanel {Margin=new Thickness(48),Spacing=18,VerticalAlignment=VerticalAlignment.Center};
            labels.Children.Add(new TextBlock {Text="ACHIEVEMENT LABS",Foreground=Brushes.Aquamarine,FontSize=16,LetterSpacing=4});
            labels.Children.Add(new TextBlock {Text=name,Foreground=Brushes.White,FontSize=40,FontWeight=FontWeight.Bold,TextWrapping=TextWrapping.Wrap});
            labels.Children.Add(new TextBlock {Text="OFFLINE DEMONSTRATION · SAMPLE DATA",Foreground=Brushes.LightGray,FontSize=16});
            var poster=new Border {Width=960,Height=540,Child=labels,Background=new LinearGradientBrush {StartPoint=new RelativePoint(0,0,RelativeUnit.Relative),EndPoint=new RelativePoint(1,1,RelativeUnit.Relative),GradientStops=new GradientStops {new GradientStop(Color.Parse(colour),0),new GradientStop(Color.Parse("#0C171D"),1)}}};
            poster.Measure(new Size(960,540));poster.Arrange(new Rect(0,0,960,540));
            using var bitmap=new RenderTargetBitmap(new PixelSize(960,540),new Vector(96,96));bitmap.Render(poster);
            bitmap.Save(Path.Combine(ArtworkDirectory,file),new PngBitmapEncoderOptions());
        }
    }
}
