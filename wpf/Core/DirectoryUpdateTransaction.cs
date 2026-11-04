using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;

namespace UntamedManager.Core;

public sealed class DirectoryUpdateTransaction
{
    public sealed record State(string Application, string Phase, Dictionary<string,string> Hashes);
    private readonly string root;
    private readonly string? expectedApplication;
    private string Journal=>Path.Combine(root,"update-transaction.json");
    private string BindingFile=>Path.Combine(root,"update-owner.dat");
    private sealed record OwnerBinding(string Root,string Application,string SnapshotHash);
    private static string SnapshotHash(Dictionary<string,string> hashes)
    {
        using var bytes=new MemoryStream();using var writer=new BinaryWriter(bytes,Encoding.UTF8,true);
        foreach(var item in hashes.OrderBy(x=>x.Key,StringComparer.Ordinal))
        {writer.Write(item.Key);writer.Write(item.Value);}
        writer.Flush();return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }
    public DirectoryUpdateTransaction(string backupRoot,string? expectedApplication=null)
    {
        CheckPath(backupRoot);root=Path.GetFullPath(backupRoot).TrimEnd(Path.DirectorySeparatorChar);
        if(expectedApplication!=null)
        {CheckPath(expectedApplication);this.expectedApplication=Path.GetFullPath(expectedApplication).TrimEnd(Path.DirectorySeparatorChar);}
    }
    private static void CheckPath(string path)
    {
        if(!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") ||
            path.Length<3||!char.IsAsciiLetter(path[0])||path[1]!=':'||
            path[2] is not ('\\' or '/')||path[3..].Contains(':')||path.Contains('~')||
            new DriveInfo(Path.GetPathRoot(path)!).DriveType!=DriveType.Fixed)
            throw new IOException("Updater requires fixed local paths");
        if(path[3..].Split(new[]{'\\','/'},StringSplitOptions.RemoveEmptyEntries)
            .Any(p=>p.EndsWith('.')||p.EndsWith(' ')))throw new IOException("Updater rejects path aliases");
        var chain=new Stack<DirectoryInfo>();
        for(var dir=new DirectoryInfo(path);dir!=null;dir=dir.Parent)chain.Push(dir);
        foreach(var dir in chain)
            if(dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint)!=0)
                throw new IOException("Updater rejects linked paths");
        if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
            throw new IOException("Updater rejects linked files");
    }
    private static Dictionary<string,string> Scan(string path)
    {
        CheckPath(path);
        var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string dir)
        {
            CheckPath(dir);
            foreach(string file in Directory.GetFiles(dir))
            {
                if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked file rejected");
                using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);
                result.Add(Path.GetRelativePath(path,file),Convert.ToHexString(SHA256.HashData(stream)));
            }
            foreach(string child in Directory.GetDirectories(dir))Visit(child);
        }
        Visit(path);return result;
    }
    private static void Verify(string path, Dictionary<string,string> expected)
    {
        var actual=Scan(path);
        if(actual.Count!=expected.Count || expected.Any(x=>!actual.TryGetValue(x.Key,out var hash)||hash!=x.Value))
            throw new IOException("Full application snapshot hash mismatch");
    }
    private static void Copy(string source,string target,Dictionary<string,string> files)
    {
        Directory.CreateDirectory(target);
        foreach(var file in files)
        {
            string dest=Path.Combine(target,file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(Path.Combine(source,file.Key),dest,false);
        }
        // Preserve empty directories as part of the full application tree.
        foreach(var dir in Directory.GetDirectories(source,"*",SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target,Path.GetRelativePath(source,dir)));
        Verify(target,files);
    }
    private void Write(State state)
    {
        CheckPath(root);CheckPath(Journal);
        string temp=Journal+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(stream,state);stream.Flush(true);}
            File.Move(temp,Journal,true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public void Prepare(string application)
    {
        CheckPath(application);
        application=Path.GetFullPath(application).TrimEnd(Path.DirectorySeparatorChar);
        CheckPath(application);CheckPath(root);
        if(root.StartsWith(application+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
            application.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
            root.Equals(application,StringComparison.OrdinalIgnoreCase))
            throw new IOException("Application and snapshot paths overlap");
        if(expectedApplication!=null&&!expectedApplication.Equals(application,StringComparison.OrdinalIgnoreCase))
            throw new IOException("Unexpected update application");
        if(File.Exists(Journal)||File.Exists(BindingFile)||Directory.Exists(Path.Combine(root,"snapshot")))throw new IOException("Existing update transaction");
        Directory.CreateDirectory(root);
        var hashes=Scan(application);
        Copy(application,Path.Combine(root,"snapshot"),hashes);
        Verify(application,hashes);
        byte[] binding=ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new OwnerBinding(root,application,SnapshotHash(hashes)))),null,DataProtectionScope.CurrentUser);
        using(var owner=new FileStream(BindingFile,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {owner.Write(binding);owner.Flush(true);}
        Write(new(application,"prepared",hashes));
    }
    public void Commit()
    {
        var state=Read();
        if(state.Phase=="committed")return;
        if(state.Phase!="prepared")throw new IOException("Only a prepared update can be committed");
        Write(state with {Phase="committed"});
    }
    public State InspectState()=>Read();
    private State Read()
    {
        CheckPath(root);CheckPath(Journal);CheckPath(BindingFile);
        if(new FileInfo(Journal).Length>16*1024*1024||new FileInfo(BindingFile).Length>65536)
            throw new IOException("Oversized update recovery metadata");
        OwnerBinding binding;
        try
        {
            byte[] bytes=ProtectedData.Unprotect(File.ReadAllBytes(BindingFile),null,DataProtectionScope.CurrentUser);
            binding=JsonSerializer.Deserialize<OwnerBinding>(bytes)??throw new IOException("Missing update owner binding");
        }
        catch(Exception e) when(e is CryptographicException or JsonException)
        {throw new IOException("Update owner binding cannot be verified");}
        var state=JsonSerializer.Deserialize<State>(File.ReadAllText(Journal))??throw new IOException("Invalid update journal");
        if(string.IsNullOrWhiteSpace(state.Application)||string.IsNullOrWhiteSpace(binding.Root)||
           string.IsNullOrWhiteSpace(binding.Application))throw new IOException("Missing update recovery owner identity");
        CheckPath(state.Application);
        if(!root.Equals(binding.Root,StringComparison.OrdinalIgnoreCase)||
           !state.Application.Equals(binding.Application,StringComparison.OrdinalIgnoreCase)||
           (expectedApplication!=null&&!expectedApplication.Equals(state.Application,StringComparison.OrdinalIgnoreCase))||
           state.Hashes==null||state.Hashes.Count>50000||state.Phase is not ("prepared" or "committed" or "rollingback" or "rolledback"))
            throw new IOException("Update recovery ownership or phase differs");
        if(state.Hashes.Any(x=>string.IsNullOrWhiteSpace(x.Key)||x.Value==null||
            x.Value.Length!=64||x.Value.Any(c=>!Uri.IsHexDigit(c)))||
           SnapshotHash(state.Hashes)!=binding.SnapshotHash)
            throw new IOException("Update snapshot inventory differs from protected owner binding");
        if(state.Application.Equals(root,StringComparison.OrdinalIgnoreCase)||
           state.Application.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
           root.StartsWith(state.Application+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new IOException("Update recovery paths overlap");
        return state;
    }
    // Windows Defender/indexers and a just-exited WPF process can briefly keep
    // file handles open after health smoke. Preserve fail-closed semantics:
    // retry only rename collisions; never delete a locked app or bypass hashes.
    private static void MoveWithBoundedRetry(string source,string target)
    {
        CheckPath(source);CheckPath(target);
        for(int attempt=0;attempt<12;attempt++)
        {
            try
            {
                Directory.Move(source,target);
                return;
            }
            catch(Exception ex) when(
                (ex is IOException || ex is UnauthorizedAccessException) &&
                attempt<11 && Directory.Exists(source) && !Directory.Exists(target))
            {
                System.Threading.Thread.Sleep(250*(attempt+1));
            }
        }
    }

    public void Rollback()
    {
        var state=Read();
        if(state.Phase=="committed"||state.Phase=="rolledback")return;
        CheckPath(state.Application);CheckPath(root);
        string snapshot=Path.Combine(root,"snapshot");Verify(snapshot,state.Hashes);
        string staging=state.Application+".restore-"+Guid.NewGuid().ToString("N");
        Copy(snapshot,staging,state.Hashes);
        Write(state with {Phase="rollingback"});
        if(Directory.Exists(state.Application))
            MoveWithBoundedRetry(state.Application,
                state.Application+".failed-"+Guid.NewGuid().ToString("N"));
        MoveWithBoundedRetry(staging,state.Application);
        Verify(state.Application,state.Hashes);
        Write(state with {Phase="rolledback"});
    }
}
