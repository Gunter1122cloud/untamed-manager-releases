using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using UntamedManager.Core;

namespace UntamedManager;

public partial class MainWindow
{
    private readonly DispatcherTimer _hostResourceTimer=new(){
        Interval=TimeSpan.FromSeconds(30)
    };
    private bool _resourceProbeBusy;
    private LocalHostResourceSnapshot? _latestLocalResource;
    private void InitializeLocalResourceTelemetry()
    {
        _hostResourceTimer.Tick+=async (_,_)=>await RefreshLocalResourcesAsync();
        Loaded+=async (_,_)=>{
            if(App.IsSmokeTest || App.IsSupervisionTest || App.IsDemoStart || _demo!=null)return;
            _hostResourceTimer.Start();
            await RefreshLocalResourcesAsync();
        };
        Closed+=(_,_)=>_hostResourceTimer.Stop();
    }
    private async Task RefreshLocalResourcesAsync()
    {
        if(_resourceProbeBusy || _demo!=null)return;
        _resourceProbeBusy=true;
        try
        {
            var data=await Task.Run(LocalHostResourceProbe.Read);
            if(_demo==null)
            {
                _latestLocalResource=data;
                LocalHostResourceStatus.Text=data.Display(_cluster.Config.LocalHostName);
                RefreshDashboardMapCards();
            }
        }
        catch(Exception ex)
        {
            WriteLog("local-resource-metrics",ex);
            if(_demo==null)
                LocalHostResourceStatus.Text="Helyi CPU / RAM: nem mérhető; ellenőrizd a Windows WMI szolgáltatást.";
        }
        finally{_resourceProbeBusy=false;}
    }
}
