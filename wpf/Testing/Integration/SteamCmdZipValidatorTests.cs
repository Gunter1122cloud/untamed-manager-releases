using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using UntamedManager.Core;

public static class SteamCmdZipValidatorTests
{
    private static void Check(bool condition,string reason)
    {
        if(!condition)throw new Exception("STEAM ZIP SAFETY: "+reason);
        Console.WriteLine("PASS SteamCMD ZIP: "+reason);
    }
    private static MemoryStream Zip(params (string Name,byte[] Bytes)[] entries)
    {
        var output=new MemoryStream();
        using(var archive=new ZipArchive(output,ZipArchiveMode.Create,true))
            foreach(var file in entries)
            {
                var entry=archive.CreateEntry(file.Name);
                using var target=entry.Open();
                target.Write(file.Bytes);
            }
        output.Position=0;
        return output;
    }
    public static async Task Run()
    {
        byte[] exe=new byte[]{(byte)'M',(byte)'Z',0,0,1,2,3,4};
        using(var zip=Zip(("steamcmd.exe",exe),("readme.txt",new byte[]{1,2})))
        {
            var extracted=await SteamCmdZipValidator.ExtractExecutableAsync(zip);
            Check(extracted.SequenceEqual(exe),
                "one root steamcmd.exe can be validated offline without writing files");
        }
        using(var zip=Zip(("../steal.txt",new byte[]{2}),("steamcmd.exe",exe)))
        {
            bool refused=false;
            try{await SteamCmdZipValidator.ExtractExecutableAsync(zip);}
            catch(InvalidDataException){refused=true;}
            Check(refused,"ZIP entry path traversal is rejected even if ignored during extraction");
        }
        using(var zip=Zip(("nested/steamcmd.exe",exe)))
        {
            bool refused=false;
            try{await SteamCmdZipValidator.ExtractExecutableAsync(zip);}
            catch(InvalidDataException){refused=true;}
            Check(refused,"only steamcmd.exe at archive root may become a Windows executable");
        }
        using(var zip=Zip(("steamcmd.exe",exe),("STEAMCMD.EXE",exe)))
        {
            bool refused=false;
            try{await SteamCmdZipValidator.ExtractExecutableAsync(zip);}
            catch(InvalidDataException){refused=true;}
            Check(refused,"duplicate case-variant executables are rejected");
        }
        using(var zip=Zip(("steamcmd.exe",new byte[]{65,66,67})))
        {
            bool refused=false;
            try{await SteamCmdZipValidator.ExtractExecutableAsync(zip);}
            catch(InvalidDataException){refused=true;}
            Check(refused,"non-Windows PE payload is rejected");
        }
        using(var zip=Zip(("steamcmd.exe",exe)))
        {
            using(var archive=new ZipArchive(zip,ZipArchiveMode.Update,true))
                archive.GetEntry("steamcmd.exe")!.ExternalAttributes=(0xA000<<16);
            zip.Position=0;
            bool refused=false;
            try{await SteamCmdZipValidator.ExtractExecutableAsync(zip);}
            catch(InvalidDataException){refused=true;}
            Check(refused,"symbolic-link entry metadata cannot masquerade as steamcmd.exe");
        }
    }
}
