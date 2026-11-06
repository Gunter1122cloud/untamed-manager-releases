using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UntamedManager;

public enum ManagerStartupStage { InitializingProfile, InitializingInterface, Ready }

// The actual Untamed Kingdom artwork is a packaged project resource. There is
// no fabricated percentage, network-ready claim, or decorative fake operation.
public sealed class ManagerSplashWindow : Window
{
    private readonly TextBlock stage;
    public ManagerStartupStage CurrentStage {get;private set;}=
        ManagerStartupStage.InitializingProfile;

    public ManagerSplashWindow()
    {
        Title="Untamed Manager – indítás";
        Width=930;Height=510;MinWidth=930;MinHeight=510;
        WindowStyle=WindowStyle.None;
        ResizeMode=ResizeMode.NoResize;
        ShowInTaskbar=false;
        Topmost=true;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;
        Background=new SolidColorBrush(Color.FromRgb(7,25,27));

        var root=new Grid{ClipToBounds=true};
        root.Children.Add(new Image{
            Source=new BitmapImage(new Uri(
                "pack://application:,,,/Assets/kingdom-hero.jpg",UriKind.Absolute)),
            Stretch=Stretch.UniformToFill,Opacity=0.68
        });
        root.Children.Add(new Border{
            Background=new LinearGradientBrush(
                new GradientStopCollection{
                    new(Color.FromArgb(248,3,17,19),0),
                    new(Color.FromArgb(224,6,41,38),0.65),
                    new(Color.FromArgb(120,8,35,38),1)
                },new Point(0,0.55),new Point(1,0.35)),
            IsHitTestVisible=false
        });

        var body=new StackPanel{
            VerticalAlignment=VerticalAlignment.Bottom,
            Margin=new Thickness(58,30,58,48)
        };
        body.Children.Add(new TextBlock{
            Text="UNTAMED KINGDOM",Foreground=new SolidColorBrush(
                Color.FromRgb(117,248,211)),FontFamily=new FontFamily("Segoe UI"),
            FontSize=19,FontWeight=FontWeights.SemiBold,
            Margin=new Thickness(0,0,0,10)
        });
        body.Children.Add(new TextBlock{
            Text="UNTAMED MANAGER",Foreground=Brushes.White,
            FontFamily=new FontFamily("Segoe UI"),FontSize=48,
            FontWeight=FontWeights.Bold,Margin=new Thickness(0,0,0,8)
        });
        body.Children.Add(new TextBlock{
            Text="Az ARK: Survival Ascended cluster helyi kezelője",
            Foreground=new SolidColorBrush(Color.FromRgb(212,235,226)),
            FontSize=16,Margin=new Thickness(0,0,0,22)
        });
        stage=new TextBlock{
            Foreground=new SolidColorBrush(Color.FromRgb(171,237,219)),
            FontSize=14,
            Text="Helyi profil előkészítése...",
            Margin=new Thickness(0,0,0,12)
        };
        body.Children.Add(stage);
        body.Children.Add(new ProgressBar{
            Height=5,IsIndeterminate=true,Maximum=100,Minimum=0,
            Foreground=new SolidColorBrush(Color.FromRgb(112,242,197)),
            Background=new SolidColorBrush(Color.FromRgb(27,73,67)),
            BorderThickness=new Thickness(0)
        });
        body.Children.Add(new TextBlock{
            Text="Az ASA szerverek nem indulnak vagy állnak le a betöltőképernyő miatt.",
            Foreground=new SolidColorBrush(Color.FromRgb(172,190,185)),
            FontSize=11,Margin=new Thickness(0,15,0,0)
        });
        root.Children.Add(body);
        Content=root;
    }

    public void SetStage(ManagerStartupStage value)
    {
        CurrentStage=value;
        stage.Text=value switch{
            ManagerStartupStage.InitializingProfile=>"Helyi profil előkészítése...",
            ManagerStartupStage.InitializingInterface=>"Felület és helyi clusteradatok betöltése...",
            ManagerStartupStage.Ready=>"A kezelőfelület megnyílt.",
            _=>"Helyi adatok betöltése..."
        };
    }
}
