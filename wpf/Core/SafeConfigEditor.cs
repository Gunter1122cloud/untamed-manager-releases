using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UntamedManager.Core;

// Distinguish optimistic-concurrency conflicts from file locks and I/O failures.
// Still derives from IOException for historical callers and regression tests.
public sealed class ConfigEtagConflictException : IOException
{
    public ConfigEtagConflictException(string message) : base(message) { }
}
public sealed class ConfigMergeConflictException : IOException
{
    public ConfigMergeConflictException() : base("INI three-way merge conflict") { }
}
public sealed record IniRebaseResult(ConfigSnapshot Fresh,string Content,int MergedLines);
public enum ConfigKind { GameIni, PluginJson }
public sealed record ConfigSnapshot(string Path,ConfigKind Kind,string Sha256,string Text,
    string EncodingId,string LineEnding,bool HasBom,long Bytes);
public sealed record ConfigPreview(string File,string OldSha256,string NewSha256,
    int OldLines,int NewLines,bool Changed,bool Valid,string Description);

// Deliberately a local stopped/test-profile editor. No Legacy Store, UNC,
// junction, symlink or remote profile may enter the replacement path.
public static class SafeConfigEditor
{
    public static ConfigSnapshot Read(string path,string mapInstallation,ConfigKind kind,
        ProcessCheck state,bool maintenance=false)
    {
        ValidatePath(path,mapInstallation,kind,state,maintenance);
        var bytes=File.ReadAllBytes(path);
        if(bytes.Length>2_000_000)throw new InvalidDataException("Config file exceeds 2 MB");
        var (enc,name,bom)=EncodingOf(bytes);
        int offset=bom?enc.GetPreamble().Length:0;
        string content=enc.GetString(bytes,offset,bytes.Length-offset);
        ValidateContent(content,kind);
        string ending=content.Contains("\r\n",StringComparison.Ordinal)?"\r\n":
            content.Contains('\n')?"\n":content.Contains('\r')?"\r":Environment.NewLine;
        return new(Path.GetFullPath(path),kind,Hex(bytes),content,name,ending,bom,bytes.Length);
    }

    public static ConfigPreview Preview(ConfigSnapshot snapshot,string edited)
    {
        if(snapshot==null)throw new ArgumentNullException(nameof(snapshot));
        var content=Normalize(edited,snapshot.LineEnding);
        ValidateContent(content,snapshot.Kind);
        byte[] output=Encode(snapshot,content);
        return new(snapshot.Path,snapshot.Sha256,Hex(output),
            snapshot.Text.Split('\n').Length,content.Split('\n').Length,
            !snapshot.Sha256.Equals(Hex(output),StringComparison.Ordinal),true,
            "Exact encoding/BOM/newline preserved; external hash will be checked before save");
    }

    public static string Save(ConfigSnapshot baseline,string edited,string mapInstallation,
        ProcessCheck current,bool maintenance=false,Func<ProcessCheck>? recheck=null)
    {
        using var operation=new UntamedOperationCoordinator().Acquire(mapInstallation,UntamedOperationKind.Configuration);
        ValidatePath(baseline.Path,mapInstallation,baseline.Kind,current,maintenance);
        var preview=Preview(baseline,edited);
        if(!preview.Changed)return "";
        if(!Hex(File.ReadAllBytes(baseline.Path)).Equals(baseline.Sha256,
            StringComparison.Ordinal))
            throw new ConfigEtagConflictException("Config changed externally (ETag mismatch)");
        string target=baseline.Path;
        string token=Guid.NewGuid().ToString("N");
        string tmp=target+".untamed-stage-"+token;
        string backup=target+".untamed-"+token+".bak";
        try
        {
            operation.MarkMutationStarted();
            var data=Encode(baseline,Normalize(edited,baseline.LineEnding));
            File.WriteAllBytes(tmp,data);
            if(!Hex(File.ReadAllBytes(tmp)).Equals(preview.NewSha256,StringComparison.Ordinal))
                throw new IOException("Staged config checksum mismatch");
            if(!Hex(File.ReadAllBytes(target)).Equals(baseline.Sha256,StringComparison.Ordinal))
                throw new ConfigEtagConflictException("Config changed during staging");
            // The UI passes a fresh OS process inspection here, immediately
            // before the atomic replacement. An externally started map must
            // not inherit the earlier 'stopped' decision from editor opening.
            if(recheck!=null)
                ValidatePath(target,mapInstallation,baseline.Kind,recheck(),maintenance);
            File.Replace(tmp,target,backup,true);
            if(!Hex(File.ReadAllBytes(target)).Equals(preview.NewSha256,StringComparison.Ordinal))
                throw new IOException("Applied config checksum mismatch; manual recovery required: "+backup);
            operation.Complete();
            return backup;
        }
        finally{if(File.Exists(tmp))File.Delete(tmp);}
    }

    /// <summary>
    /// Safe optimistic rebase of an INI edit onto a newer on-disk snapshot.
    /// It never writes files. Only VALUE changes on EXISTING, positionally
    /// stable key=value lines may be merged. Unrelated external edits of an
    /// existing comment are preserved if the operator did not edit that line.
    /// Headers, arrays, added/removed/reordered lines or ambiguous structural
    /// changes require manual review. Conflicting same-line edits fail closed.
    /// Duplicate ASA INI keys remain distinct positional entries.
    /// </summary>
    public static IniRebaseResult RebaseIni(ConfigSnapshot original,
        string edited,ConfigSnapshot current)
    {
        if(original==null||current==null||
           original.Kind!=ConfigKind.GameIni||
           current.Kind!=ConfigKind.GameIni||
           !Path.GetFullPath(original.Path).Equals(
               Path.GetFullPath(current.Path),StringComparison.OrdinalIgnoreCase)||
           original.EncodingId!=current.EncodingId||
           original.HasBom!=current.HasBom)
            throw new ConfigMergeConflictException();
        var before=Normalize(original.Text,"\n").Split('\n');
        var user=Normalize(edited,"\n").Split('\n');
        var disk=Normalize(current.Text,"\n").Split('\n');
        if(before.Length!=user.Length||before.Length!=disk.Length)
            throw new ConfigMergeConflictException();
        // A peer edit may not alter the section or key identity of a line.
        // Preserving same linecount alone is insufficient: a renamed header
        // would relocate all subsequent values to a different ASA INI section.
        for(int i=0;i<before.Length;i++)
        {
            if(before[i]==disk[i])continue;
            string line=before[i].TrimStart();
            string freshLine=disk[i].TrimStart();
            if((line.StartsWith(';')||line.StartsWith('#')) &&
               (freshLine.StartsWith(';')||freshLine.StartsWith('#')))
            {
                // Another editor may update a comment without changing the
                // section or any ASA key. Preserve those disk bytes exactly,
                // but never silently merge competing edits to the comment.
                if(user[i]!=before[i])throw new ConfigMergeConflictException();
                continue;
            }
            int eq=before[i].IndexOf('=');
            if(eq<=0||line.StartsWith('[')||
               line.StartsWith(';')||line.StartsWith('#')||
               !disk[i].StartsWith(before[i][..(eq+1)],
                   StringComparison.Ordinal))
                throw new ConfigMergeConflictException();
        }
        int editedCount=0;
        for(int i=0;i<before.Length;i++)
        {
            if(before[i]==user[i])continue;
            editedCount++;
            string oldLine=before[i];
            int equals=oldLine.IndexOf('=');
            if(equals<=0||oldLine.TrimStart().StartsWith(';')||
               oldLine.TrimStart().StartsWith('#')||
               oldLine.TrimStart().StartsWith('['))
                throw new ConfigMergeConflictException();
            // The full left-hand side, including leading whitespace, must
            // be unchanged on both sides; it protects per-occurrence identity.
            string prefix=oldLine[..(equals+1)];
            if(!user[i].StartsWith(prefix,StringComparison.Ordinal)||
               !disk[i].StartsWith(prefix,StringComparison.Ordinal))
                throw new ConfigMergeConflictException();
            // The on-disk writer may have changed the exact same value. Do
            // not choose a winner silently even when both edits are valid.
            if(disk[i]!=oldLine&&disk[i]!=user[i])
                throw new ConfigMergeConflictException();
            if(disk[i]==oldLine)disk[i]=user[i];
        }
        string result=string.Join(current.LineEnding,disk);
        ValidateContent(result,ConfigKind.GameIni);
        return new(current,result,editedCount);
    }

    public static string SetIniValue(ConfigSnapshot baseline,string section,string key,
        string value,int occurrence=0)
    {
        if(baseline.Kind!=ConfigKind.GameIni ||
           string.IsNullOrWhiteSpace(section)||string.IsNullOrWhiteSpace(key) ||
           section.IndexOfAny(new[]{'[',']','\r','\n'})>=0 ||
           key.IndexOfAny(new[]{'=','\r','\n'})>=0 ||
           value.Contains('\n')||value.Contains('\r')||
           occurrence<0)
            throw new InvalidDataException("Invalid section/key/value/occurrence");
        string normalized=Normalize(baseline.Text,"\n");
        string[] lines=normalized.Split('\n');
        bool inside=false;int match=0;bool found=false;
        for(int i=0;i<lines.Length;i++)
        {
            var line=lines[i].Trim();
            if(line.StartsWith("[")&&line.EndsWith("]"))
            {
                inside=string.Equals(line[1..^1],section,StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if(!inside || line.StartsWith(";")||line.StartsWith("#"))continue;
            int equals=line.IndexOf('=');
            if(equals<=0 ||
               !line[..equals].Trim().Equals(key,StringComparison.OrdinalIgnoreCase))
                continue;
            if(match++==occurrence)
            {
                string original=lines[i];
                int rawEquals=original.IndexOf('=');
                lines[i]=original[..(rawEquals+1)]+value;
                found=true;break;
            }
        }
        if(!found)throw new InvalidDataException("INI key/occurrence not found; no implicit additions");
        return string.Join(baseline.LineEnding,lines);
    }

    public static string[] FindIniKeys(ConfigSnapshot baseline,string section)
    {
        if(baseline.Kind!=ConfigKind.GameIni)throw new InvalidDataException("INI snapshot required");
        bool inside=false;var result=new List<string>();
        foreach(var line in baseline.Text.Replace("\r\n","\n").Replace('\r','\n').Split('\n'))
        {
            var raw=line.Trim();
            if(raw.StartsWith("[")&&raw.EndsWith("]"))
            {inside=raw[1..^1].Equals(section,StringComparison.OrdinalIgnoreCase);continue;}
            int eq=raw.IndexOf('=');
            if(inside&&eq>0&&!raw.StartsWith(';')&&!raw.StartsWith('#'))
                result.Add(raw[..eq].Trim());
        }
        return result.ToArray();
    }

    private static void ValidatePath(string path,string root,ConfigKind kind,
        ProcessCheck state,bool maintenance)
    {
        if(!Enum.IsDefined(kind))throw new InvalidDataException("Unsupported configuration kind");
        if(state==null || !state.ConfirmedStopped || state.Running || maintenance)
            throw new InvalidOperationException("Config editing requires confirmed stopped local map");
        if(string.IsNullOrWhiteSpace(path)||string.IsNullOrWhiteSpace(root) ||
           !Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(root) ||
           path.StartsWith(@"\\") || root.StartsWith(@"\\"))
            throw new InvalidDataException("Explicit fixed-disk local paths required");
        string p=Path.GetFullPath(path),r=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if(!p.StartsWith(r+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
           new DriveInfo(Path.GetPathRoot(r)!).DriveType!=DriveType.Fixed)
            throw new UnauthorizedAccessException("Config must be inside registered local map root");
        if(p.Contains(VaultProtocol.ProtectedNameToken,StringComparison.OrdinalIgnoreCase) ||
           p.Contains(".legacy-store",StringComparison.OrdinalIgnoreCase) ||
           p.Contains("ClusterRoot",StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Untamed Cluster Vault metadata/config modification prohibited");
        for(var d=new DirectoryInfo(Path.GetDirectoryName(p)!);d!=null;d=d.Parent)
            if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Junction or linked config directory");
        if(!File.Exists(p) || (File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Config missing or linked");
        string name=Path.GetFileName(p);
        if(kind==ConfigKind.GameIni && name is not ("Game.ini" or "GameUserSettings.ini"))
            throw new InvalidDataException("Not a Game.ini or GameUserSettings.ini");
        if(kind==ConfigKind.PluginJson && (!name.EndsWith(".json",StringComparison.OrdinalIgnoreCase) ||
            !p.Contains(Path.DirectorySeparatorChar+"Plugins"+Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Plugin JSON must belong to a local plugin directory");
    }

    private static void ValidateContent(string value,ConfigKind kind)
    {
        if(!Enum.IsDefined(kind))throw new InvalidDataException("Unsupported configuration kind");
        if(value==null || value.Length>2_000_000 || value.Contains('\0'))
            throw new InvalidDataException("Invalid text length or NUL");
        if(kind==ConfigKind.PluginJson)
        {
            using var parsed=JsonDocument.Parse(value,new JsonDocumentOptions{
                AllowTrailingCommas=true,CommentHandling=JsonCommentHandling.Skip});
            if(parsed.RootElement.ValueKind!=JsonValueKind.Object)
                throw new InvalidDataException("Plugin JSON root must be object");
            RejectDuplicateProperties(parsed.RootElement);
        }
    }
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in element.EnumerateObject())
            {
                // JsonElement decodes escaped property names. A second
                // \u0061 therefore conflicts with a literal a as well.
                if(!names.Add(property.Name))
                    throw new InvalidDataException("Duplicate plugin JSON property");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if(element.ValueKind==JsonValueKind.Array)
            foreach(var value in element.EnumerateArray())RejectDuplicateProperties(value);
    }
    private static string Normalize(string content,string newline)
    {
        if(content==null)throw new ArgumentNullException(nameof(content));
        return content.Replace("\r\n","\n").Replace('\r','\n').Replace("\n",newline);
    }
    private static (Encoding Encoding,string Name,bool Bom) EncodingOf(byte[] bytes)
    {
        if(bytes.Length>=3 && bytes[0]==239 && bytes[1]==187 && bytes[2]==191)
            return (new UTF8Encoding(true,true),"utf-8",true);
        if(bytes.Length>=2 && bytes[0]==255 && bytes[1]==254)
            return (new UnicodeEncoding(false,true,true),"utf-16le",true);
        if(bytes.Length>=2 && bytes[0]==254 && bytes[1]==255)
            return (new UnicodeEncoding(true,true,true),"utf-16be",true);
        return (new UTF8Encoding(false,true),"utf-8",false);
    }
    private static byte[] Encode(ConfigSnapshot snapshot,string content)
    {
        var enc=snapshot.EncodingId switch{
            "utf-16le"=>(Encoding)new UnicodeEncoding(false,snapshot.HasBom,true),
            "utf-16be"=>new UnicodeEncoding(true,snapshot.HasBom,true),
            "utf-8"=>new UTF8Encoding(snapshot.HasBom,true),
            _=>throw new InvalidDataException("Unsupported encoding")
        };
        return enc.GetPreamble().Concat(enc.GetBytes(content)).ToArray();
    }
    private static string Hex(byte[] data)=>Convert.ToHexString(SHA256.HashData(data));
}

