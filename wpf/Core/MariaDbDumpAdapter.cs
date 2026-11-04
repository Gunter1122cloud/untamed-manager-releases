using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UntamedManager.Core;

public sealed class MariaDbEndpoint
{
    public int Port {get;}
    internal string DataDirectory {get;}
    internal string User {get;}
    internal string Password {get;}
    public MariaDbEndpoint(int port,string dataDirectory,string user,string password)
    {
        if(port is <1024 or >65535||string.IsNullOrWhiteSpace(user)||user.Length>128||
           string.IsNullOrEmpty(password)||password.Length>1024||user.Contains('\0')||password.Contains('\0'))
            throw new ArgumentException("Explicit local SQL endpoint and dedicated credentials required");
        Port=port;DataDirectory=MariaDbDumpAdapter.FixedPath(dataDirectory);User=user;Password=password;
    }
    public override string ToString()=>"Private local MariaDB endpoint";
}
public sealed record MariaDbDumpManifest(int Schema,Guid Owner,Guid Batch,DateTimeOffset CreatedUtc,
    string Version,string[] Databases,long Bytes,string Sha256,string SchemaFingerprint,string Consistency);
public sealed record MariaDbDumpResult(string Package,MariaDbDumpManifest Manifest);

// Real CLI-backed logical SQL backup/restore. It never starts/stops a SQL service.
// Restore only creates the three selected databases on a verified EMPTY target;
// it cannot replace an existing production database. Cluster-wide orchestration
// and ASA/Store application consistency are separate acceptance requirements.
public sealed class MariaDbDumpAdapter
{
    readonly string client,dump,privateRoot;
    const string Names="'arkshop','arkpermissions','lethalquests'";
    public MariaDbDumpAdapter(string clientExecutable,string dumpExecutable,string privateOptionsRoot)
    {
        client=Tool(clientExecutable,"mariadb.exe");dump=Tool(dumpExecutable,"mariadb-dump.exe");
        privateRoot=FixedPath(privateOptionsRoot);
    }
    public async Task<MariaDbDumpResult> DumpAsync(MariaDbEndpoint source,string destination,Guid owner,
        CancellationToken ct=default)
    {
        if(owner==Guid.Empty)throw new ArgumentException("SQL backup owner required");
        string target=FixedPath(destination),stage=target+".incomplete";
        if(Overlap(privateRoot,source.DataDirectory)||Overlap(target,privateRoot)||
           Overlap(target,source.DataDirectory)||Overlap(stage,source.DataDirectory)||
           Directory.Exists(target)||File.Exists(target)||Directory.Exists(stage)||File.Exists(stage))
            throw new IOException("Fresh SQL backup destination outside server data required");
        using var operation=new UntamedOperationCoordinator().Acquire(source.DataDirectory,UntamedOperationKind.Backup);
        using var options=new PrivateOptions(privateRoot,source);
        await ValidateAsync(source,options,false,ct);
        string schema=await SchemaAsync(options,ct);
        operation.MarkMutationStarted();CreatePrivateDirectory(stage);
        string sql=Path.Combine(stage,"databases.sql");
        await using(var output=new FileStream(sql,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {
            var args=Arguments(options).Concat(new[]{"--single-transaction","--skip-lock-tables",
                "--skip-add-locks","--quick","--routines","--events","--triggers","--hex-blob",
                "--default-character-set=utf8mb4","--databases"}).Concat(MariaDbCompatibility.Databases).ToArray();
            await RunAsync(dump,args,output,null,TimeSpan.FromMinutes(10),2L*1024*1024*1024,ct);
            output.Flush(true);
        }
        if(await SchemaAsync(options,ct)!=schema)throw new IOException("SQL schema changed during dump");
        long bytes=new FileInfo(sql).Length;if(bytes<1)throw new InvalidDataException("Empty SQL dump");
        string hash=Hash(sql);
        var manifest=new MariaDbDumpManifest(1,owner,Guid.NewGuid(),DateTimeOffset.UtcNow,
            "10.11.19",MariaDbCompatibility.Databases.ToArray(),bytes,hash,schema,
            "InnoDB single-transaction logical dump; cross-ASA/Store consistency not established");
        Durable(Path.Combine(stage,"manifest.json"),JsonSerializer.Serialize(manifest));
        Durable(Path.Combine(stage,"complete"),"1");
        Directory.Move(stage,target);Verify(target,owner);operation.Complete();
        return new(target,manifest);
    }
    public async Task RestoreToEmptyAsync(MariaDbEndpoint target,string package,Guid owner,
        CancellationToken ct=default)
    {
        var manifest=Verify(package,owner);string root=FixedPath(package);
        if(Overlap(root,target.DataDirectory)||Overlap(privateRoot,target.DataDirectory)||Overlap(root,privateRoot))
            throw new InvalidDataException("SQL archive, private options and target overlap");
        using var operation=new UntamedOperationCoordinator().Acquire(target.DataDirectory,UntamedOperationKind.Restore);
        using var options=new PrivateOptions(privateRoot,target);
        await ValidateAsync(target,options,true,ct);
        // Hold the actual hashed input against replacement/write for the entire import.
        await using var input=new FileStream(Path.Combine(root,"databases.sql"),FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length!=manifest.Bytes||Convert.ToHexString(SHA256.HashData(input))!=manifest.Sha256)
            throw new InvalidDataException("SQL archive changed before restore");
        input.Position=0;operation.MarkMutationStarted();
        await RunAsync(client,Arguments(options).Concat(new[]{"--binary-mode","--sandbox","--local-infile=0","--skip-reconnect",
            "--default-character-set=utf8mb4"}).ToArray(),Stream.Null,input,TimeSpan.FromMinutes(10),1_048_576,ct);
        var names=(await QueryAsync(options,"SELECT SCHEMA_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME IN ("+Names+") ORDER BY SCHEMA_NAME",ct))
            .Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        if(!names.ToHashSet(StringComparer.Ordinal).SetEquals(MariaDbCompatibility.Databases)||
           await SchemaAsync(options,ct)!=manifest.SchemaFingerprint)
            throw new IOException("Restored SQL schema is not verified; operator review required");
        operation.Complete();
    }
    public static MariaDbDumpManifest Verify(string package,Guid owner)
    {
        string root=FixedPath(package),manifestPath=FixedPath(Path.Combine(root,"manifest.json"));
        if(owner==Guid.Empty||!File.Exists(Path.Combine(root,"complete"))||new FileInfo(manifestPath).Length>65536)
            throw new InvalidDataException("Incomplete SQL package");
        var m=JsonSerializer.Deserialize<MariaDbDumpManifest>(File.ReadAllText(manifestPath));
        string sql=FixedPath(Path.Combine(root,"databases.sql"));
        if(m==null||m.Schema!=1||m.Owner!=owner||m.Batch==Guid.Empty||m.Version!="10.11.19"||
           m.Databases==null||m.Databases.Length!=3||!m.Databases.SequenceEqual(MariaDbCompatibility.Databases)||
           m.Bytes<1||new FileInfo(sql).Length!=m.Bytes||Hash(sql)!=m.Sha256||
           !Regex.IsMatch(m.SchemaFingerprint??"","\\A[A-F0-9]{64}\\z"))
            throw new InvalidDataException("SQL package identity or checksum mismatch");
        return m;
    }
    async Task ValidateAsync(MariaDbEndpoint endpoint,PrivateOptions options,bool restore,CancellationToken ct)
    {
        foreach(string exe in new[]{client,dump})
        {
            using var output=new MemoryStream();
            await RunAsync(exe,new[]{"--no-defaults","--version"},output,null,TimeSpan.FromSeconds(10),65536,ct);
            string text=Encoding.UTF8.GetString(output.ToArray());
            if(!text.Contains("MariaDB",StringComparison.OrdinalIgnoreCase)||
               !Regex.IsMatch(text,@"(?<![0-9.])10\.11\.19(?![0-9.])"))
                throw new InvalidDataException("SQL tools must match deployment baseline");
        }
        string[] identity=(await QueryAsync(options,"SELECT VERSION(),@@port,@@datadir",ct)).Trim().Split('\t');
        if(identity.Length!=3||!identity[0].StartsWith("10.11.19-MariaDB",StringComparison.OrdinalIgnoreCase)||
           identity[1]!=endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)||
           !FixedPath(identity[2]).TrimEnd('\\','/').Equals(endpoint.DataDirectory.TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SQL endpoint identity mismatch");
        var grants=(await QueryAsync(options,"SHOW GRANTS",ct)).Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        ValidateGrants(grants,restore);
        string names=await QueryAsync(options,"SELECT SCHEMA_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME IN ("+Names+") ORDER BY SCHEMA_NAME",ct);
        var found=names.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        if(restore?found.Length!=0:!found.ToHashSet(StringComparer.Ordinal).SetEquals(MariaDbCompatibility.Databases))
            throw new InvalidOperationException(restore?"SQL restore target already contains a selected database":"SQL source database set is incomplete");
        string engines=await QueryAsync(options,"SELECT DISTINCT ENGINE FROM information_schema.TABLES WHERE TABLE_SCHEMA IN ("+Names+") AND TABLE_TYPE='BASE TABLE'",ct);
        if(engines.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Any(x=>x!="InnoDB"))
            throw new InvalidOperationException("Nontransactional SQL tables require a separately coordinated maintenance backup");
    }
    public static void ValidateGrants(IEnumerable<string> grants,bool restore)
    {
        bool any=false;
        foreach(string grant in grants)
        {
            any=true;
            if(grant.Contains("WITH GRANT OPTION",StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("SQL delegation privilege rejected");
            var m=Regex.Match(grant,@"\AGRANT (.+?) ON (\S+) TO ",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1));
            if(!m.Success)throw new UnauthorizedAccessException("SQL roles or unknown grants rejected");
            string privileges=m.Groups[1].Value.ToUpperInvariant(),scope=m.Groups[2].Value.Replace("`","");
            if(scope=="*.*"&&privileges=="USAGE")continue;
            if(!restore&&scope=="mysql.proc"&&privileges=="SELECT")continue;
            if(!MariaDbCompatibility.Databases.Any(db=>scope.Equals(db+".*",StringComparison.Ordinal)))
                throw new UnauthorizedAccessException("SQL account exceeds selected database scope");
            string[] allowed={"ALL PRIVILEGES","SELECT","INSERT","UPDATE","DELETE","CREATE","DROP","INDEX","ALTER",
                "CREATE VIEW","SHOW VIEW","TRIGGER","EVENT","EXECUTE","CREATE ROUTINE","ALTER ROUTINE","LOCK TABLES"};
            if(privileges.Split(',').Any(p=>!allowed.Contains(p.Trim())))throw new UnauthorizedAccessException("SQL privilege not allowed");
        }
        if(!any)throw new UnauthorizedAccessException("SQL grants are unproven");
    }
    async Task<string> SchemaAsync(PrivateOptions options,CancellationToken ct)
    {
        string[] queries={
            "SELECT TABLE_SCHEMA,TABLE_NAME,TABLE_TYPE,COALESCE(ENGINE,'') FROM information_schema.TABLES WHERE TABLE_SCHEMA IN ("+Names+") ORDER BY TABLE_SCHEMA,TABLE_NAME",
            "SELECT TABLE_SCHEMA,TABLE_NAME,COLUMN_NAME,ORDINAL_POSITION,COLUMN_TYPE,IS_NULLABLE,COALESCE(COLUMN_DEFAULT,''),EXTRA FROM information_schema.COLUMNS WHERE TABLE_SCHEMA IN ("+Names+") ORDER BY TABLE_SCHEMA,TABLE_NAME,ORDINAL_POSITION",
            "SELECT ROUTINE_SCHEMA,ROUTINE_NAME,ROUTINE_TYPE,COALESCE(ROUTINE_DEFINITION,'') FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA IN ("+Names+") ORDER BY ROUTINE_SCHEMA,ROUTINE_NAME",
            "SELECT TRIGGER_SCHEMA,TRIGGER_NAME,ACTION_STATEMENT FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA IN ("+Names+") ORDER BY TRIGGER_SCHEMA,TRIGGER_NAME",
            "SELECT EVENT_SCHEMA,EVENT_NAME,EVENT_DEFINITION FROM information_schema.EVENTS WHERE EVENT_SCHEMA IN ("+Names+") ORDER BY EVENT_SCHEMA,EVENT_NAME"};
        var text=new StringBuilder();foreach(string query in queries)text.Append(await QueryAsync(options,query,ct)).Append('\0');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
    async Task<string> QueryAsync(PrivateOptions options,string query,CancellationToken ct)
    {
        using var output=new MemoryStream();
        await RunAsync(client,Arguments(options).Concat(new[]{"--batch","--raw","--skip-column-names","--skip-reconnect","--execute="+query}).ToArray(),output,null,TimeSpan.FromSeconds(15),1_048_576,ct);
        return new UTF8Encoding(false,true).GetString(output.ToArray()).Replace("\r\n","\n");
    }
    static string[] Arguments(PrivateOptions options)=>new[]{"--defaults-file="+options.Path,"--connect-timeout=5"};
    static async Task RunAsync(string executable,IEnumerable<string> args,Stream output,Stream? input,
        TimeSpan maximum,long maxOutput,CancellationToken cancellation)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(maximum);
        var ct=deadline.Token;ct.ThrowIfCancellationRequested();
        var psi=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,
            WorkingDirectory=System.IO.Path.GetDirectoryName(executable)!,RedirectStandardInput=true,
            RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in args)psi.ArgumentList.Add(arg);
        foreach(string key in new[]{"MYSQL_PWD","MARIADB_PWD","MYSQL_HOME","MARIADB_HOME"})psi.Environment.Remove(key);
        using var process=Process.Start(psi)??throw new IOException("SQL client did not start");
        async Task Copy(Stream from,Stream to,long limit)
        {
            byte[] buffer=new byte[65536];long total=0;
            for(;;){int count=await from.ReadAsync(buffer,ct);if(count==0)break;total=checked(total+count);
                if(total>limit){if(!process.HasExited)process.Kill();throw new IOException("SQL client stream limit");}
                await to.WriteAsync(buffer.AsMemory(0,count),ct);}
        }
        async Task Send(){try{if(input!=null)await input.CopyToAsync(process.StandardInput.BaseStream,ct);}finally{process.StandardInput.Close();}}
        try
        {
            await Task.WhenAll(Copy(process.StandardOutput.BaseStream,output,maxOutput),
                Copy(process.StandardError.BaseStream,Stream.Null,65536),Send(),process.WaitForExitAsync(ct)).WaitAsync(ct);
            if(process.ExitCode!=0)throw new IOException("SQL_CLIENT_EXIT_"+process.ExitCode);
        }
        catch{if(!process.HasExited)process.Kill();await process.WaitForExitAsync();throw;}
    }
    sealed class PrivateOptions : IDisposable
    {
        public string Path {get;}
        readonly string root;
        public PrivateOptions(string parent,MariaDbEndpoint endpoint)
        {
            root=FixedPath(System.IO.Path.Combine(parent,"sql-auth-"+Guid.NewGuid().ToString("N")));
            CreatePrivateDirectory(root);
            Path=System.IO.Path.Combine(root,"client.cnf");
            string Quote(string value)=>"\""+value.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r","\\r").Replace("\n","\\n")+"\"";
            Durable(Path,"[client]\nprotocol=tcp\nhost=127.0.0.1\nport="+endpoint.Port+"\nuser="+Quote(endpoint.User)+"\npassword="+Quote(endpoint.Password)+"\n");
        }
        public void Dispose(){if(File.Exists(Path))File.Delete(Path);if(Directory.Exists(root))Directory.Delete(root,false);}
    }
    internal static string FixedPath(string path)
    {
        // Shared validator rejects UNC, streams, raw-device paths, reparse and aliases.
        UntamedOperationCoordinator.ResourceKey(path);string full=System.IO.Path.GetFullPath(path);
        if(File.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Linked SQL file rejected");
        return full;
    }
    static string Tool(string path,string name)
    {string full=FixedPath(path);if(!File.Exists(full)||!System.IO.Path.GetFileName(full).Equals(name,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Explicit MariaDB tool required");return full;}
    static bool Overlap(string a,string b)=>a.Equals(b,StringComparison.OrdinalIgnoreCase)||
        a.StartsWith(b.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)||b.StartsWith(a.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);
    static string Hash(string path){using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);return Convert.ToHexString(SHA256.HashData(file));}
    static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var user=WindowsIdentity.GetCurrent().User??throw new UnauthorizedAccessException("Windows identity missing");
        var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);acl.SetOwner(user);
        acl.AddAccessRule(new FileSystemAccessRule(user,FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }
    static void Durable(string path,string value)
    {using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);byte[] bytes=Encoding.UTF8.GetBytes(value);file.Write(bytes);file.Flush(true);}
}
