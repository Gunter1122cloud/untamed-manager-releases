using System.Diagnostics;
using System.Text.Json;
using UntamedManager.Core;

public static class SupervisorWorker
{
    public static async Task Run(string[] args,CancellationToken token)
    {
        string Value(string key,string fallback){int i=Array.IndexOf(args,key);return i<0?fallback:i+1<args.Length?args[i+1]:throw new ArgumentException(key);}
        string profile=Value("--profile","default"),overrideRoot=Value("--root","");
        var store=new ConfigStore(profile,overrideRoot.Length==0?null:overrideRoot);
        using var gate=new Semaphore(1,1,"Global\\UntamedSupervisor-"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(store.Root).ToUpperInvariant()))));
        bool entered=false;
        try
        {
            try{entered=gate.WaitOne(0);}catch(AbandonedMutexException){entered=true;}
            if(!entered)throw new IOException("Supervisor already owns this local profile");
            var channel=new SupervisorChannel(store.Root);var events=new EventJournal(channel.Root);
            var scheduler=new PersistentScheduler(channel.Root);var inspector=new WindowsProcessInspector();var policy=new SupervisorPolicy();
            using var self=Process.GetCurrentProcess();var started=new DateTimeOffset(self.StartTime.ToUniversalTime());
            var watchdogStates=new Dictionary<Guid,SupervisionState>();
            var previous=new Dictionary<Guid,MapObservation>();var cpu=new Dictionary<int,(DateTimeOffset time,TimeSpan cpu,DateTimeOffset start)>();
            var config=store.Read();events.Append(config.LocalHostName,null,"Information","supervisor-start");
            try
            {
                do
                {
                    try
                    {
                        config=store.Read();var observations=new List<MapObservation>();var now=DateTimeOffset.UtcNow;
                        foreach(var map in config.LocalMaps)
                        {
                            token.ThrowIfCancellationRequested();
                            var state=(map.Arguments+" "+map.ApiLoaderArguments).Contains("store",StringComparison.OrdinalIgnoreCase)?
                                new ProcessCheck(false,"Store validation required",null):string.IsNullOrWhiteSpace(map.ApiLoaderPath)?inspector.Inspect(map):inspector.InspectFamily(map);
                            bool maintenance=map.Maintenance||config.ScheduledTasks.Any(x=>x.Enabled&&x.Map==map.Id&&x.Kind==ScheduledKind.Maintenance&&x.DueUtc<=now&&x.UntilUtc>now);
                            if(!watchdogStates.TryGetValue(map.Id,out var watchdog) || now-watchdog.ObservedUtc>=TimeSpan.FromSeconds(Math.Clamp(map.WatchdogIntervalSeconds,5,3600)) || watchdog.Maintenance!=maintenance)
                            {
                                watchdog=policy.Observe(map.Id,state,maintenance,now);watchdogStates[map.Id]=watchdog;
                                events.Append(config.LocalHostName,map.Id,watchdog.AttentionRequired?"Warning":"Information","watchdog-check");
                            }
                            double? load=null,uptime=null;long? ram=null;
                            if(state.Running && state.Identity!=null)
                            {
                                try
                                {
                                    using var process=Process.GetProcessById(state.Identity.Pid);
                                    if(new DateTimeOffset(process.StartTime.ToUniversalTime())!=state.Identity.StartedUtc)throw new IOException("Identity changed");
                                    ram=process.WorkingSet64;uptime=(now-state.Identity.StartedUtc).TotalSeconds;
                                    var total=process.TotalProcessorTime;
                                    if(cpu.TryGetValue(process.Id,out var old)&&old.start==state.Identity.StartedUtc&&(now-old.time).TotalSeconds>0)
                                        load=Math.Clamp(100*(total-old.cpu).TotalSeconds/(now-old.time).TotalSeconds/Environment.ProcessorCount,0,100);
                                    cpu[process.Id]=(now,total,state.Identity.StartedUtc);
                                }catch{ram=null;load=null;uptime=null;}
                            }
                            string rcon="Ismeretlen – nincs hitelesített mérés";
                            if(state.Running&&map.MonitorRcon&&map.RconPort>0)
                            {
                                try
                                {
                                    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(2));
                                    string response=await RconProtocol.Execute(map.RconHost,map.RconPort,SecureStore.Unprotect(map.RconPasswordProtected),"ListPlayers",deadline.Token);
                                    rcon=response.StartsWith("RCON parancs elküldve",StringComparison.Ordinal)?"Nincs ellenőrzött válasz":"Hitelesített válasz; játékosszám nem igazolt";
                                }catch(UnauthorizedAccessException){rcon="RCON hitelesítési hiba";}
                                catch{rcon="RCON nem elérhető vagy időtúllépés";}
                            }
                            string log="Nem elérhető";
                            try
                            {
                                string root=Path.GetFullPath(map.InstallationPath).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
                                if(!root.StartsWith(@"\\")&&Path.GetFullPath(map.LogFilePath).StartsWith(root,StringComparison.OrdinalIgnoreCase)&&File.Exists(map.LogFilePath))
                                    log="Utolsó naplóírás: "+File.GetLastWriteTimeUtc(map.LogFilePath).ToString("O");
                            }catch{}
                            var observation=new MapObservation(map.Id,map.Name,state.Running?true:state.ConfirmedStopped?false:null,
                                string.IsNullOrWhiteSpace(map.ApiLoaderPath)?"Nincs konfigurálva":state.LoaderIdentity!=null?state.Status:"Ismeretlen / indulás alatt",rcon,load,ram,uptime,now,null,maintenance,
                                watchdog.AttentionRequired?"Kézi ellenőrzés szükséges; automatikus beavatkozás tiltva":"Csak megfigyelés",log,"Nincs szerverbeavatkozás",state.Status);
                            if(!previous.TryGetValue(map.Id,out var oldObservation)||oldObservation.Running!=observation.Running||oldObservation.Loader!=observation.Loader)
                                events.Append(config.LocalHostName,map.Id,state.Running?"Information":"Warning","process-observation-changed");
                            if(oldObservation==null||oldObservation.Rcon!=rcon)events.Append(config.LocalHostName,map.Id,"Information","rcon-observation-changed");
                            previous[map.Id]=observation;observations.Add(observation);
                        }
                        int receiptCount=scheduler.Receipts.Length;
                        await scheduler.RunDue(config.ScheduledTasks,config.LocalMaps.Select(x=>x.Id).ToHashSet(),now,task=>
                        {
                            if(task.Kind==ScheduledKind.BackupIntegrity)
                            {
                                string allowed=Path.Combine(channel.Root,"synthetic-backups")+Path.DirectorySeparatorChar;
                                if(!Path.GetFullPath(task.BackupTestPackage).StartsWith(allowed,StringComparison.OrdinalIgnoreCase))throw new IOException("Only synthetic backup verification allowed");
                                TestBackupPackage.Verify(task.BackupTestPackage,task.Map);
                            }
                            events.Append(config.LocalHostName,task.Map,"Information","scheduled-"+task.Kind.ToString().ToLowerInvariant());
                            return Task.CompletedTask;
                        });
                        foreach(var receipt in scheduler.Receipts.Skip(receiptCount))events.Append(config.LocalHostName,null,receipt.Status=="succeeded"?"Information":"Warning","schedule-"+receipt.Status);
                        channel.Write(new(config.LocalHostId,Environment.ProcessId,started,Environment.ProcessPath!,DateTimeOffset.UtcNow,observations.ToArray()));
                        // Compatibility heartbeat for existing consumers; GUI trusts only the authenticated channel.
                        string heartbeat=Path.Combine(store.Root,"supervisor-heartbeat.json");
                        File.WriteAllText(heartbeat+".tmp",JsonSerializer.Serialize(new {hostId=config.LocalHostId,utc=DateTimeOffset.UtcNow,readOnly=true,states=observations.Select(x=>new {x.Map,x.ObservedUtc,x.Error})}));File.Move(heartbeat+".tmp",heartbeat,true);
                    }
                    catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
                    catch(Exception){events.Append(config.LocalHostName,null,"Error","supervisor-observation-failed");if(args.Contains("--once"))throw;}
                    if(args.Contains("--once"))break;
                    try{await Task.Delay(TimeSpan.FromSeconds(5),token);}catch(OperationCanceledException){break;}
                }while(!token.IsCancellationRequested);
            }
            finally{events.Append(config.LocalHostName,null,"Information","supervisor-stop");}
        }
        finally{if(entered)gate.Release();}
    }
}
