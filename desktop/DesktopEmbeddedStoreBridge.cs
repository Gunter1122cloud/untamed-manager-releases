using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;

namespace UntamedDesktop;

/// <summary>
/// Explicit local file-picker for reading an OFFLINE COPY of an ASA world.
/// The web UI cannot supply a path or call a Store writer. Only public,
/// bounded record counts leave this bridge; no survivor/player identities.
/// </summary>
public sealed class DesktopEmbeddedStoreBridge : IDisposable
{
    private readonly Func<string?> pickCopy;
    private readonly SemaphoreSlim serial=new(1,1);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public DesktopEmbeddedStoreBridge(Func<string?>? picker=null)=>
        pickCopy=picker??PickNative;

    private static string? PickNative()
    {
        using var dialog=new OpenFileDialog{
            Title="Válaszd ki az ASA .ark világmentés KÜLÖN, offline másolatát",
            Filter="ASA world backup (*.ark)|*.ark",
            CheckFileExists=true,Multiselect=false,
            RestoreDirectory=true
        };
        return dialog.ShowDialog()==DialogResult.OK?dialog.FileName:null;
    }

    public async Task<string> HandleAsync(string raw)
    {
        string? id=null;
        try
        {
            if(raw.Length is <2 or >900)
                return Error(null,"UM-STORE-COPY-REQUEST");
            using var message=JsonDocument.Parse(raw);
            var data=message.RootElement;
            if(data.ValueKind!=JsonValueKind.Object||
                !data.TryGetProperty("id",out var requestId)||
                requestId.ValueKind!=JsonValueKind.String||
                !Guid.TryParse(requestId.GetString(),out var parsedId)||
                parsedId==Guid.Empty||
                !data.TryGetProperty("method",out var method)||
                method.ValueKind!=JsonValueKind.String||
                method.GetString()!="store.inspectCopy")
                return Error(null,"UM-STORE-COPY-REQUEST");
            id=parsedId.ToString("D");
            // The renderer is never allowed to pass a filesystem path.
            if(data.TryGetProperty("path",out _)||data.TryGetProperty("root",out _)||
                data.TryGetProperty("file",out _))
                return Error(id,"UM-STORE-COPY-PATH-DENIED");
            await serial.WaitAsync();
            try
            {
                string? path=pickCopy();
                if(string.IsNullOrWhiteSpace(path))
                    return Error(id,"UM-STORE-COPY-CANCELLED");
                var report=await Task.Run(()=>EmbeddedStoreReadOnlyInspector.InspectOfflineCopy(path));
                return Ok(id,report);
            }
            finally{serial.Release();}
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or InvalidDataException
            or JsonException or SqliteException)
        {
            return Error(id,"UM-STORE-COPY-INSPECT-FAILED");
        }
    }

    private static string Ok(string id,object data)=>
        JsonSerializer.Serialize(new{id,ok=true,data},Json);
    private static string Error(string? id,string code)=>
        JsonSerializer.Serialize(new{id,ok=false,error=code},Json);
    public void Dispose()=>serial.Dispose();
}
