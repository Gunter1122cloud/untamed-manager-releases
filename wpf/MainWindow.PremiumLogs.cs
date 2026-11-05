using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UntamedManager.Core;

namespace UntamedManager;

internal sealed record PremiumLogRow(string Content,string Level,Brush LevelBrush);

public partial class MainWindow
{
    private Guid? _preferredLogMapId;

    private void ShowPremiumLogs()
    {
        Header("Naplóközpont","Maponkénti, csak olvasható ASA-napló · keresés, hibaszűrés és automatikus frissítés.");
        var maps=_cluster.Config.LocalMaps.ToArray();
        if(maps.Length==0)
        {
            Add(Box(V(Label("Nincs helyi naplóforrás",18,Mint,true),
                Label("A Szerverek oldalon regisztrált helyi map naplóútvonalát tudjuk megnyitni. LAN-peer naplóját nem kérjük le.",12,Soft),
                Btn("Szerverek",()=>Navigate("servers")))));
            return;
        }

        var selector=new ComboBox{
            ItemsSource=maps,DisplayMemberPath="Name",
            SelectedItem=maps.FirstOrDefault(m=>m.Id==_preferredLogMapId)??maps[0],
            Width=220,Margin=new Thickness(0,0,10,9)
        };
        var search=new TextBox{Width=220,Height=36,Margin=new Thickness(0,0,10,9),
            ToolTip="Keress a betöltött, titokszűrt naplósorokban (legfeljebb 128 karakter)"};
        var filter=new ComboBox{
            ItemsSource=new[]{"Összes sor","Hibák","Figyelmeztetések","Információ"},
            SelectedIndex=0,Width=164,Margin=new Thickness(0,0,10,9)
        };
        var rows=new ListView{
            MinHeight=330,MaxHeight=680,
            ItemTemplate=(DataTemplate)System.Windows.Application.Current.FindResource("PremiumLogRow")
        };
        var summary=Label("Még nincs helyi naplókivonat.",12,Soft);
        var source=Label("Az útvonal kizárólag a kiválasztott helyi map konfigurációjából származik.",11,Soft);
        var status=Label("Frissítés kézzel vagy 5 másodpercenként. Egyetlen lekérés legfeljebb 256 KiB.",12,Soft);
        var footer=Label("Naplósor ≠ ASA folyamat-/RCON-igazolás, SaveComplete vagy sikeres clustermentés.",11,Soft);
        LocalLogSnapshot? snapshot=null;
        bool reading=false,auto=false;
        int visibleCount=0;
        var timer=new DispatcherTimer(DispatcherPriority.Background,Dispatcher){
            Interval=TimeSpan.FromSeconds(5)
        };
        Button copy=null!,refresh=null!,autoButton=null!;
        LocalMap CurrentMap()=>selector.SelectedItem as LocalMap
            ??throw new InvalidOperationException("Válassz helyi mapot.");
        void Render()
        {
            if(snapshot==null)
            {
                rows.ItemsSource=Array.Empty<PremiumLogRow>();
                summary.Text="Nincs ellenőrzött naplókivonat.";
                visibleCount=0;copy.IsEnabled=false;
                return;
            }
            LocalLogLevel? category=(string?)filter.SelectedItem switch{
                "Hibák"=>LocalLogLevel.Error,
                "Figyelmeztetések"=>LocalLogLevel.Warning,
                "Információ"=>LocalLogLevel.Info,
                _=>null
            };
            try
            {
                var result=LocalLogCenter.Filter(snapshot,search.Text,category,1000);
                var items=result.Select(line=>new PremiumLogRow(
                    line.Content,line.Level switch{
                        LocalLogLevel.Error=>"HIBA",
                        LocalLogLevel.Warning=>"FIGYELMEZTETÉS",
                        _=>"INFO"
                    },line.Level switch {
                        LocalLogLevel.Error=>new SolidColorBrush(Color.FromRgb(214,106,114)),
                        LocalLogLevel.Warning=>new SolidColorBrush(Color.FromRgb(216,169,86)),
                        _=>new SolidColorBrush(Color.FromRgb(91,196,183))
                    })).ToArray();
                rows.ItemsSource=items;
                visibleCount=items.Length;
                summary.Text="Megjelenítve: "+items.Length+" sor / "+
                    snapshot.Lines.Count+" beolvasott sor · "+
                    (snapshot.Truncated?"korlátozott naplóvég":"fájl vége")+
                    " · "+(snapshot.TotalBytes/1024d).ToString("F1")+" KiB fájl";
                copy.IsEnabled=!reading&&visibleCount>0;
                if(items.Length>0)rows.ScrollIntoView(items[^1]);
            }
            catch(Exception ex)
            {
                WriteLog("premium-log-filter",ex);
                status.Text="Érvénytelen keresési feltétel. Legfeljebb 128 karakter használható.";
            }
        }

        async Task Read()
        {
            if(reading||_page!="logs"||_demo!=null)return;
            var map=CurrentMap();
            if(!_cluster.Config.LocalMaps.Contains(map))
            {
                status.Text="A naplóforrás nem helyi maphoz tartozik.";return;
            }
            reading=true;
            refresh.IsEnabled=false;
            copy.IsEnabled=false;
            try
            {
                var result=await Task.Run(()=>LocalLogCenter.Read(map));
                if(_page!="logs"||_demo!=null||CurrentMap().Id!=map.Id)return;
                snapshot=result;
                source.Text="Helyi napló: "+result.FileName+
                    " · utolsó fájlmódosítás: "+result.LastModifiedUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss");
                status.Text="Frissítve: "+DateTime.Now.ToString("HH:mm:ss")+
                    " · csak olvasás · titokszűrés aktív · ASA állapotát nem minősíti.";
                Render();
            }
            catch(Exception ex)
            {
                WriteLog("premium-log-read",ex);
                snapshot=null;
                source.Text="Helyi naplóútvonal vagy olvasási jogosultság nem igazolható.";
                status.Text="Napló nem olvasható: "+ex.GetType().Name+
                    ". A map/Store/SQL adatai változatlanok.";
                Render();
            }
            finally{reading=false;refresh.IsEnabled=true;copy.IsEnabled=visibleCount>0;}
        }
        void StopTimer()
        {
            auto=false;timer.Stop();autoButton.Content="Automatikus frissítés: KI";
        }
        refresh=Btn("Napló frissítése",async()=>await Read());
        autoButton=Btn("Automatikus frissítés: KI",()=>{
            if(auto){StopTimer();return;}
            auto=true;autoButton.Content="Automatikus frissítés: BE · 5 mp";
            timer.Start();_ = Read();
        });
        copy=Btn("Szűrt kivonat másolása",()=>{
            try
            {
                var selected=(rows.ItemsSource as System.Collections.Generic.IEnumerable<PremiumLogRow>)
                    ?.Select(x=>x.Content).ToArray()??Array.Empty<string>();
                if(selected.Length==0)return;
                string body="Untamed Manager · csak olvasható, titokszűrt ASA naplókivonat\n"+
                    "Nem igazolja a mentésbefejezést, RCON readiness-t vagy ASA folyamatot.\n"+
                    string.Join("\n",selected);
                if(body.Length>60_000)body=body[..60_000]+
                    "\n[CLIPBOARD TRUNCATED]";
                Clipboard.SetText(UntamedPresentation.Redact(body));
                status.Text="A szűrt, titokszűrt naplókivonat a vágólapra került.";
            }
            catch(Exception ex)
            {
                WriteLog("premium-log-copy",ex);
                status.Text="A vágólap nem érhető el; semmilyen fájlt nem módosítottunk.";
            }
        });
        copy.IsEnabled=false;
        timer.Tick+=(_,_)=>{
            if(_page!="logs"||_demo!=null){StopTimer();return;}
            if(!reading)_=Read();
        };
        selector.SelectionChanged+=(_,_)=>{
            StopTimer();
            snapshot=null;
            if(selector.SelectedItem is LocalMap map)_preferredLogMapId=map.Id;
            source.Text="Új map kiválasztva. A napló csak kézi megnyitás után jelenik meg.";
            Render();
            _=Read();
        };
        search.TextChanged+=(_,_)=>Render();
        filter.SelectionChanged+=(_,_)=>Render();
        var toolbar=new WrapPanel{Margin=new Thickness(0,0,0,7)};
        toolbar.Children.Add(selector);toolbar.Children.Add(search);toolbar.Children.Add(filter);
        var actions=new WrapPanel{Margin=new Thickness(0,2,0,9)};
        actions.Children.Add(refresh);actions.Children.Add(autoButton);actions.Children.Add(copy);
        var top=Box(V(Label("LOG CENTER  /  HELYI ASA NAPLÓK",17,Mint,true),
            Label("Valódi, helyi forrás · keresés, szintszűrés, frissítés · titokszűrés",12,Soft),
            toolbar,actions));
        var main=Box(V(Label("NAPLÓSOROK",17,Mint,true),source,summary,rows,footer,status));
        Add(top);Add(main);
        Add(new Expander{
            Header="Haladó · történeti ASA / ArkApi indítási naplóelemzés",
            Padding=new Thickness(12),Margin=new Thickness(0,2,8,9),
            IsExpanded=false,Content=CaptureHistoricalPanel()
        });
        _preferredLogMapId=CurrentMap().Id;
        _=Read();

        StackPanel CaptureHistoricalPanel()
        {
            int start=PageBody.Children.Count;
            ShowHistoricalLogEvidencePanel();
            var panel=new StackPanel();
            foreach(var control in PageBody.Children.Cast<UIElement>().Skip(start).ToArray())
            {
                PageBody.Children.Remove(control);
                panel.Children.Add(control);
            }
            return panel;
        }
    }
}
