using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UntamedManager.Core;

/// <summary>Portable, read-only-data MOD profile: no server path, credentials,
/// Store session, SQL, transfer file or LAN remote write.</summary>
public sealed record PortableModProfile(int Schema,string Label,
    string[] EnabledOrder,string[] Passive,string[] Disabled,string Sha256,
    bool RuntimeLoadedVerified);

public static class PortableModProfileCodec
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static readonly Regex Identifier=new(@"\A[0-9]{5,12}\z",
        RegexOptions.Compiled|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    private static string Digest(string[][] lists)
    {
        using var output=new MemoryStream();
        using(var writer=new BinaryWriter(output,Encoding.UTF8,true))
        foreach(var list in lists)
        {
            writer.Write(list.Length);
            foreach(var item in list)
            {
                var bytes=Encoding.UTF8.GetBytes(item);
                writer.Write(bytes.Length);writer.Write(bytes);
            }
        }
        return Convert.ToHexString(SHA256.HashData(output.ToArray()));
    }
    public static void Validate(PortableModProfile profile)
    {
        if(profile==null||profile.Schema!=1||profile.RuntimeLoadedVerified||
           string.IsNullOrWhiteSpace(profile.Label)||profile.Label.Length>80||
           profile.Label.Any(c=>char.IsControl(c))||
           profile.EnabledOrder==null||profile.Passive==null||profile.Disabled==null)
            throw new InvalidDataException("Invalid portable mod profile");
        foreach(var list in new[]{profile.EnabledOrder,profile.Passive,profile.Disabled})
            if(list.Length>500 || list.Any(id=>id==null||!Identifier.IsMatch(id)) ||
               list.Distinct(StringComparer.Ordinal).Count()!=list.Length)
                throw new InvalidDataException("Invalid mod profile project IDs");
        if(profile.Passive.Except(profile.EnabledOrder).Any()||
           profile.Disabled.Intersect(profile.EnabledOrder).Any()||
           profile.Sha256==null||profile.Sha256.Length!=64 ||
           !Digest(new[]{profile.EnabledOrder,profile.Passive,profile.Disabled})
               .Equals(profile.Sha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Portable mod profile state/checksum mismatch");
    }
    public static PortableModProfile FromMap(LocalMap map,string label)
    {
        ArgumentNullException.ThrowIfNull(map);
        var state=ModStateController.Read(map);
        var order=state.EnabledOrder.ToArray();
        var passive=state.Passive.ToArray();
        var disabled=state.Disabled.ToArray();
        var result=new PortableModProfile(1,label,order,passive,disabled,
            Digest(new[]{order,passive,disabled}),false);
        Validate(result);
        return result;
    }
    public static string Serialize(PortableModProfile profile)
    {
        Validate(profile);
        return JsonSerializer.Serialize(profile,Json);
    }
    public static PortableModProfile Parse(string content)
    {
        if(string.IsNullOrWhiteSpace(content)||content.Length>100000)
            throw new InvalidDataException("Portable mod profile missing/oversized");
        var value=JsonSerializer.Deserialize<PortableModProfile>(content,Json)
            ??throw new InvalidDataException("Portable mod profile missing");
        Validate(value);
        return value;
    }
}
