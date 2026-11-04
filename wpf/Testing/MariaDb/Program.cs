using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UntamedManager.Core;

if(!OperatingSystem.IsWindows()||Environment.GetEnvironmentVariable("GITHUB_ACTIONS")!="true"||args.Length!=1)
    throw new InvalidOperationException("Real SQL acceptance runs only on the separate Windows CI runner");
string bin=Path.GetFullPath(args[0]);
string client=Path.Combine(bin,"mariadb.exe"),dump=Path.Combine(bin,"mariadb-dump.exe");
string install=Path.Combine(bin,File.Exists(Path.Combine(bin,"mariadb-install-db.exe"))?"mariadb-install-db.exe":"mysql_install_db.exe");
string server=Path.Combine(bin,File.Exists(Path.Combine(bin,"mariadbd.exe"))?"mariadbd.exe":"mysqld.exe");
string root=Path.Combine(Path.GetTempPath(),"Untamed-Sql-Integration-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string password="Aa9!"+Guid.NewGuid().ToString("N")+"#\"\\'";
var engines=new List<(Process Process,int Port,string Data)>();
void Check(bool value,string reason){if(!value)throw new Exception("SQL TEST FAILED: "+reason);Console.WriteLine("PASS real-sql "+reason);}
int FreePort(){var l=new TcpListener(IPAddress.Loopback,0);l.Start();int port=((IPEndPoint)l.LocalEndpoint).Port;l.Stop();return port;}
async Task<string> Command(string exe,IEnumerable<string> arguments,string? input=null)
{
    var psi=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,
        RedirectStandardOutput=true,RedirectStandardError=true,StandardInputEncoding=new UTF8Encoding(false),
        StandardOutputEncoding=new UTF8Encoding(false),WorkingDirectory=bin};
    foreach(string arg in arguments)psi.ArgumentList.Add(arg);
    using var process=Process.Start(psi)!;
    var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
    try{
        if(input!=null)await process.StandardInput.WriteAsync(input);process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        await error;if(process.ExitCode!=0)throw new IOException("Fixture SQL client exit "+process.ExitCode);
        return (await output).Replace("\r\n","\n");
    }finally{if(!process.HasExited){process.Kill();await process.WaitForExitAsync();}}
}
Task<string> Admin(int port,string sql)=>Command(client,new[]{"--no-defaults","--protocol=tcp","--host=127.0.0.1",
    "--port="+port,"--user=root","--connect-timeout=2","--default-character-set=utf8mb4","--batch","--raw","--skip-column-names"},sql);
string SqlString(string text)=>"'"+text.Replace("\\","\\\\").Replace("'","''")+"'";
async Task<(int Port,string Data)> Engine(string name,bool source)
{
    string data=Path.Combine(root,name);Directory.CreateDirectory(data);
    await Command(install,new[]{"--datadir="+data});
    int port=FreePort();var psi=new ProcessStartInfo(server){UseShellExecute=false,CreateNoWindow=true,
        RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=bin};
    foreach(string a in new[]{"--no-defaults","--basedir="+Path.GetDirectoryName(bin),"--datadir="+data,
        "--port="+port,"--bind-address=127.0.0.1","--skip-name-resolve","--skip-log-bin","--event-scheduler=OFF",
        "--innodb-buffer-pool-size=64M","--max-connections=16","--console"})psi.ArgumentList.Add(a);
    var process=Process.Start(psi)!;engines.Add((process,port,data));
    _=process.StandardOutput.ReadToEndAsync();_=process.StandardError.ReadToEndAsync();
    bool ready=false;for(int attempt=0;attempt<60&&!process.HasExited;attempt++)
    {try{await Admin(port,"SELECT 1;");ready=true;break;}catch(IOException){await Task.Delay(500);}}
    Check(ready,"owned standalone engine starts without Windows service installation");
    string grants="CREATE USER 'sqlactor'@'127.0.0.1' IDENTIFIED BY "+SqlString(password)+";\n";
    foreach(string db in MariaDbCompatibility.Databases)
    {
        if(source)grants+="CREATE DATABASE `"+db+"` CHARACTER SET utf8mb4;\n";
        grants+="GRANT ALL PRIVILEGES ON `"+db+"`.* TO 'sqlactor'@'127.0.0.1';\n";
    }
    if(source)grants+="GRANT SELECT ON mysql.proc TO 'sqlactor'@'127.0.0.1';\n";
    await Admin(port,grants);return(port,data);
}
async Task<string> Actor(int port,string sql)
{
    string options=Path.Combine(root,"fixture-auth-"+Guid.NewGuid()+".cnf");
    string quote="\""+password.Replace("\\","\\\\").Replace("\"","\\\"")+"\"";
    File.WriteAllText(options,"[client]\nprotocol=tcp\nhost=127.0.0.1\nport="+port+"\nuser=sqlactor\npassword="+quote+"\n");
    try{return await Command(client,new[]{"--defaults-file="+options,"--default-character-set=utf8mb4","--batch","--raw","--skip-column-names"},sql);}
    finally{File.Delete(options);}
}
try
{
    var source=await Engine("source",true);var target=await Engine("target",false);
    foreach(string db in MariaDbCompatibility.Databases)
        await Actor(source.Port,"CREATE TABLE `"+db+"`.items(id INT PRIMARY KEY,label VARCHAR(100),payload LONGBLOB) ENGINE=InnoDB;\n"+
            "INSERT INTO `"+db+"`.items VALUES (1,'Árvíztűrő 🦖',0x000AFF80);\n"+
            "CREATE VIEW `"+db+"`.v_items AS SELECT id,label FROM `"+db+"`.items;\n"+
            "CREATE FUNCTION `"+db+"`.plus_one(x INT) RETURNS INT DETERMINISTIC RETURN x+1;\n"+
            "CREATE PROCEDURE `"+db+"`.sample() SELECT 42;\n"+
            "CREATE TRIGGER `"+db+"`.before_item BEFORE INSERT ON `"+db+"`.items FOR EACH ROW SET NEW.id=NEW.id;\n"+
            "CREATE EVENT `"+db+"`.inert_event ON SCHEDULE EVERY 1 DAY DO SET @untamed_test=1;\n");
    var adapter=new MariaDbDumpAdapter(client,dump,Path.Combine(root,"private-options"));
    foreach(string grant in (await Actor(source.Port,"SHOW GRANTS;" )).Split('\n'))
    {
        var scope=System.Text.RegularExpressions.Regex.Match(grant,@"\bON (\S+) TO ");
        if(scope.Success)Console.WriteLine("Fixture grant scope: "+scope.Groups[1].Value);
    }
    var sourceEndpoint=new MariaDbEndpoint(source.Port,source.Data,"sqlactor",password);
    var targetEndpoint=new MariaDbEndpoint(target.Port,target.Data,"sqlactor",password);
    Guid owner=Guid.NewGuid();string package=Path.Combine(root,"logical-backup");
    var result=await adapter.DumpAsync(sourceEndpoint,package,owner);
    Check(result.Manifest.Bytes>0&&MariaDbDumpAdapter.Verify(package,owner).Databases.Length==3,"real CLI dump packages all three databases with SHA-256");
    await adapter.RestoreToEmptyAsync(targetEndpoint,package,owner);
    foreach(string db in MariaDbCompatibility.Databases)
    {
        string restored=await Actor(target.Port,"SELECT label,HEX(payload) FROM `"+db+"`.items WHERE id=1;\nSELECT `"+db+"`.plus_one(41);\nCALL `"+db+"`.sample();\nSELECT COUNT(*) FROM `"+db+"`.v_items;\n");
        Check(restored=="Árvíztűrő 🦖\t000AFF80\n42\n42\n1\n","actual restored Unicode/blob rows, view, function and procedure in "+db);
        string objects=await Admin(target.Port,"SELECT COUNT(*) FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA='"+db+"';\nSELECT COUNT(*) FROM information_schema.EVENTS WHERE EVENT_SCHEMA='"+db+"';\n");
        Check(objects=="1\n1\n","trigger and event definitions restored in "+db);
    }
    bool rejected=false;try{await adapter.RestoreToEmptyAsync(targetEndpoint,package,owner);}catch(InvalidOperationException){rejected=true;}
    Check(rejected,"existing target databases are never overwritten");
    foreach(string unsafeGrant in new[]{"GRANT ALL PRIVILEGES ON *.* TO x","GRANT FILE ON *.* TO x","GRANT role TO x","GRANT SELECT ON `otherdb`.* TO x"})
    {rejected=false;try{MariaDbDumpAdapter.ValidateGrants(new[]{unsafeGrant},true);}catch(UnauthorizedAccessException){rejected=true;}Check(rejected,"unscoped SQL privilege rejected");}
    string sqlFile=Path.Combine(package,"databases.sql");File.AppendAllText(sqlFile,"-- corruption");
    rejected=false;try{MariaDbDumpAdapter.Verify(package,owner);}catch(InvalidDataException){rejected=true;}
    Check(rejected,"modified SQL archive is refused before import");
    Check(!Directory.GetFiles(Path.Combine(root,"private-options"),"client.cnf",SearchOption.AllDirectories).Any(),"temporary credential files are removed after SQL operations");
    Console.WriteLine("PASS REAL MariaDB 10.11.19 dump/empty-target restore; no production database accessed");
}
finally
{
    foreach(var engine in engines)
    {
        try{if(!engine.Process.HasExited)await Admin(engine.Port,"SHUTDOWN;");}catch{}
        try{if(!engine.Process.HasExited&&!engine.Process.WaitForExit(10000)){engine.Process.Kill();engine.Process.WaitForExit(10000);}}
        finally{engine.Process.Dispose();}
    }
}
