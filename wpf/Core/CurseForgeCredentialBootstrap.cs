using System;
using System.IO;
using System.Text;

namespace UntamedManager.Core;

/// <summary>
/// One-time, per-Windows-user credential provisioning. A private local script
/// produces DPAPI ciphertext; GitHub, installer and diagnostics never contain
/// the operator's secret. No HTTP, mod or ASA operations are performed here.
/// </summary>
public static class CurseForgeCredentialBootstrap
{
    public const string FileName="curseforge-key-import.dpapi";

    public static bool TryImport(ClusterService cluster,string localDirectory)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        if(string.IsNullOrWhiteSpace(localDirectory))
            throw new ArgumentException("A private local directory is required",nameof(localDirectory));
        string path=Path.Combine(localDirectory,FileName);
        if(!File.Exists(path))return false;

        // Import is opt-in by placing a private file. Do not overwrite an
        // existing credential: use the UI's explicit remove operation first.
        if(!string.IsNullOrWhiteSpace(cluster.Config.CurseForgeApiKeyProtected))
            return false;

        var info=new FileInfo(path);
        if((info.Attributes & FileAttributes.ReparsePoint)!=0 ||
           info.Length<16 || info.Length>4096)
            throw new InvalidDataException("Invalid private CurseForge import file");

        string protectedValue=File.ReadAllText(path,Encoding.UTF8).Trim();
        // Unprotect before persisting, so the Windows user identity and data
        // integrity are checked. Never log or expose decrypted input.
        byte[] encrypted=Convert.FromBase64String(protectedValue);
        if(encrypted.Length<16 || encrypted.Length>2048)
            throw new InvalidDataException("Invalid private CurseForge import payload");

        string candidate=SecureStore.Unprotect(protectedValue);
        CurseForgeCatalog.ValidateApiKey(candidate);
        cluster.UpdateSettings(c=>c.CurseForgeApiKeyProtected=protectedValue);
        File.Delete(path);
        return true;
    }
}
