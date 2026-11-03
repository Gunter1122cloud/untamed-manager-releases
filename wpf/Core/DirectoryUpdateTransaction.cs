using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace UntamedManager.Core;

public sealed class DirectoryUpdateTransaction
{
    public sealed record State(string Application, string Phase, Dictionary<string,string> Hashes);
    private readonly string root;
    private string Journal=>Path.Combine(root,"update-transaction.json");
    public DirectoryUpdateTransaction(string backupRoot) { root=Path.GetFullPath(backupRoot); }
    private static void CheckPath(string path)
    {
        if(!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") ||
            new DriveInfo(Path.GetPathRoot(path)!).DriveType!=DriveType.Fixed)
            throw new IOException("Updater requires fixed local paths");
        var chain=new Stack<DirectoryInfo>();
        for(var dir=new DirectoryInfo(path);dir!=null;dir=dir.Parent)chain.Push(dir);
        foreach(var dir in chain)
            if(dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint)!=0)
                throw new IOException("Updater rejects linked paths");
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
        string temp=Journal+".tmp";
        using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None))
        {JsonSerializer.Serialize(stream,state);stream.Flush(true);}
        File.Move(temp,Journal,true);
    }
    public void Prepare(string application)
    {
        application=Path.GetFullPath(application).TrimEnd(Path.DirectorySeparatorChar);
        CheckPath(application);CheckPath(root);
        if(root.StartsWith(application+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
            application.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||root==application)
            throw new IOException("Application and snapshot paths overlap");
        if(File.Exists(Journal)||Directory.Exists(Path.Combine(root,"snapshot")))throw new IOException("Existing update transaction");
        Directory.CreateDirectory(root);
        var hashes=Scan(application);
        Copy(application,Path.Combine(root,"snapshot"),hashes);
        Verify(application,hashes);
        Write(new(application,"prepared",hashes));
    }
    public void Commit()
    {
        var state=Read();Write(state with {Phase="committed"});
    }
    private State Read()=>JsonSerializer.Deserialize<State>(File.ReadAllText(Journal))??throw new IOException("Invalid update journal");
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
            Directory.Move(state.Application,state.Application+".failed-"+Guid.NewGuid().ToString("N"));
        Directory.Move(staging,state.Application);
        Verify(state.Application,state.Hashes);
        Write(state with {Phase="rolledback"});
    }
}
