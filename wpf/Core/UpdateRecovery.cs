using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UntamedManager.Core;
public static class UpdateRecovery
{
    public static string? FindPending(string application,string updateRoot)
    {
        application=Path.GetFullPath(application).TrimEnd(Path.DirectorySeparatorChar);
        if(!Directory.Exists(updateRoot))return null;
        var pending=Directory.GetDirectories(updateRoot,"rollback-*").Where(root=>
        {
            if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked recovery directory rejected");
            string file=Path.Combine(root,"update-transaction.json");
            if(!File.Exists(file))return false;
            var state=JsonSerializer.Deserialize<DirectoryUpdateTransaction.State>(File.ReadAllText(file))??throw new IOException("Invalid recovery journal");
            return (state.Phase=="prepared"||state.Phase=="rollingback")&&
                string.Equals(Path.GetFullPath(state.Application).TrimEnd(Path.DirectorySeparatorChar),application,StringComparison.OrdinalIgnoreCase);
        }).ToArray();
        if(pending.Length>1)throw new IOException("Multiple pending updates require manual review");
        return pending.SingleOrDefault();
    }
}
