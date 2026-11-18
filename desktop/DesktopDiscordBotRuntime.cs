using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UntamedDesktop;

/// <summary>Local-only native UI for the explicitly provisioned existing bot.
/// No generic command, arbitrary task name, token or path crosses WebView or Discord.
/// Windows Task Scheduler owns the lifetime, independent of Manager updates.</summary>
internal static class DesktopDiscordBotRuntime
{
    internal const string TaskName="Obsidian-DiscordBot";
    internal const string Marker="Obsidian Discord bot runtime v1 (local administrator provisioned)";

    private static T WithTask<T>(Func<dynamic,T> action)
    {
        object? service=null,folder=null,task=null,definition=null,registration=null;
        try
        {
            var type=Type.GetTypeFromProgID("Schedule.Service",throwOnError:true)!;
            service=Activator.CreateInstance(type)!;
            dynamic scheduler=service;
            scheduler.Connect();
            folder=scheduler.GetFolder(@"\");
            task=((dynamic)folder).GetTask(TaskName);
            definition=((dynamic)task).Definition;
            registration=((dynamic)definition).RegistrationInfo;
            if(!string.Equals((string)((dynamic)registration).Description,Marker,StringComparison.Ordinal))
                throw new InvalidOperationException("Unrecognized bot task");
            return action((dynamic)task);
        }
        finally
        {
            foreach(var obj in new[]{registration,definition,task,folder,service})
                if(obj!=null&&Marshal.IsComObject(obj))
                    Marshal.ReleaseComObject(obj);
        }
    }

    internal static string StateLabel(int state)=>state switch
    {
        1=>"Letiltva",2=>"Sorban áll",3=>"Leállítva / indítható",
        4=>"Fut (Discord-kapcsolat nincs igazolva)",_=>"Ismeretlen"
    };
    internal static bool CanStart(int state)=>state==3;
    internal static bool CanStop(int state)=>state==2||state==4;

    internal static void Show(IWin32Window owner,bool mutationsAllowed)
    {
        using var dialog=new Form{
            Text="Obsidian · Discord bot",StartPosition=FormStartPosition.CenterParent,
            Width=660,Height=340,MinimumSize=new Size(590,320),
            MaximizeBox=false,MinimizeBox=false
        };
        var flow=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,
            WrapContents=false,AutoScroll=true,Padding=new Padding(15)};
        var status=new Label{Width=580,Height=42,Text="Állapot lekérdezése…"};
        var explanation=new Label{Width=580,Height=90,Text=
            "A meglévő botot a Windows háttérfeladata futtatja, külön konzol nélkül. "+
            "A Manager bezárása/frissítése nem állítja le. A botot csak egyetlen szervergépen konfiguráld. "+
            "A token és botkonfiguráció nem kerül a Managerbe."};
        var buttons=new FlowLayoutPanel{Width=580,Height=42};
        var start=new Button{Text="Indítás",Width=115,Enabled=false};
        var stop=new Button{Text="Leállítás",Width=115,Enabled=false};
        var refresh=new Button{Text="Frissítés",Width=115};
        var note=new Label{Width=580,Height=55,Text=
            "Első beállítás: a Codex futtassa a Register-DiscordBotRuntime.ps1 segédletet. "+
            "A leállítás megszakíthatja a bot folyamatban lévő műveletét; nem állít le ASA mapot."};
        void Reload()
        {
            start.Enabled=false;stop.Enabled=false;
            try
            {
                var result=WithTask<(int State,int LastResult)>(t=>((int)t.State,(int)t.LastTaskResult));
                status.Text=StateLabel(result.State)+" · Windows eredmény: 0x"+unchecked((uint)result.LastResult).ToString("X8");
                start.Enabled=mutationsAllowed&&CanStart(result.State);
                stop.Enabled=mutationsAllowed&&CanStop(result.State);
            }
            catch{status.Text="A helyi botfeladat nincs beállítva, vagy nem ellenőrizhető.";}
        }
        void Change(bool run)
        {
            if(!mutationsAllowed)return;
            if(MessageBox.Show(dialog,run?"Elindítod a meglévő Discord botot?":
                "Leállítod a helyi Discord botfeladatot? A futó botművelet megszakadhat.",
                "Discord bot",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
            try
            {
                bool accepted=WithTask<bool>(t=>{
                    int state=(int)t.State;
                    if(run)
                    {
                        if(!CanStart(state))return false;
                        object instance=t.Run(null);
                        if(Marshal.IsComObject(instance))Marshal.ReleaseComObject(instance);
                    }
                    else
                    {
                        if(!CanStop(state))return false;
                        t.Stop(0);
                    }
                    return true;
                });
                if(!accepted)MessageBox.Show(dialog,"Az állapot megváltozott; frissítsd a panelt.");
            }
            catch{MessageBox.Show(dialog,"A Windows nem fogadta el a műveletet. Ellenőrizd a helyi feladatot.");}
            Reload();
        }
        start.Click+=(_,_)=>Change(true);stop.Click+=(_,_)=>Change(false);
        refresh.Click+=(_,_)=>Reload();
        buttons.Controls.AddRange(new Control[]{start,stop,refresh});
        flow.Controls.AddRange(new Control[]{status,explanation,buttons,note});
        dialog.Controls.Add(flow);
        using var timer=new System.Windows.Forms.Timer{Interval=3000};
        timer.Tick+=(_,_)=>Reload();
        dialog.Shown+=(_,_)=>{Reload();timer.Start();};
        dialog.FormClosed+=(_,_)=>timer.Stop();
        dialog.ShowDialog(owner);
    }
}
