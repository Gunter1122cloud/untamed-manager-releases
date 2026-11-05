using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntamedManager.Core;

namespace UntamedDesktop;

/// <summary>
/// Explicit opt-in credential-only write surface; independent from read-only
/// server, RCON, backup, Store and Steam management. No raw key is returned.
/// Requests are already restricted to the packaged local WebView2 origin.
/// </summary>
public sealed class DesktopCredentialBridge : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(22) };
    private readonly CurseForgeCatalog _catalog;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public DesktopCredentialBridge() => _catalog = new CurseForgeCatalog(_http);

    public async Task<string> HandleAsync(string raw)
    {
        string? id = null;
        try
        {
            if (raw.Length is < 2 or > 4096) return Error(null, "UM-INVALID-MESSAGE");
            using var doc = JsonDocument.Parse(raw);
            var request = doc.RootElement;
            if (request.ValueKind != JsonValueKind.Object ||
                !request.TryGetProperty("id", out var rawId) ||
                rawId.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(rawId.GetString(), out var parsed) ||
                !request.TryGetProperty("method", out var rawMethod) ||
                rawMethod.ValueKind != JsonValueKind.String)
                return Error(null, "UM-INVALID-MESSAGE");
            id = parsed.ToString("D");
            string? method = rawMethod.GetString();
            if (method is not ("curseforge.credentialStatus" or
                              "curseforge.credentialSave" or
                              "curseforge.credentialClear"))
                return Error(id, "UM-METHOD-DENIED");

            string? supplied = null;
            if (method == "curseforge.credentialSave")
            {
                if (!request.TryGetProperty("credential", out var rawKey) ||
                    rawKey.ValueKind != JsonValueKind.String)
                    return Error(id, "UM-INVALID-CREDENTIAL");
                supplied = rawKey.GetString();
                if (supplied == null || supplied.Length is < 1 or > 512)
                    return Error(id, "UM-INVALID-CREDENTIAL");
                try { CurseForgeCatalog.ValidateApiKey(supplied); }
                catch (ArgumentException) { return Error(id, "UM-INVALID-CREDENTIAL"); }
            }

            await _gate.WaitAsync();
            try
            {
                if (method == "curseforge.credentialStatus")
                {
                    // Never expose key bytes, even to the privileged local UI.
                    bool configured = !string.IsNullOrEmpty(new ConfigStore().Read().CurseForgeApiKeyProtected);
                    return Success(id, new { configured });
                }

                if (method == "curseforge.credentialSave")
                {
                    // Check actual provider acceptance BEFORE committing any config.
                    // On 401, timeout or invalid response the old credential survives.
                    await _catalog.VerifyAsaKeyAsync(supplied!);
                }

                // The read + save pair uses the existing CAS hash and process mutex.
                // Concurrent WPF config edits are rejected instead of overwritten.
                var store = new ConfigStore();
                var config = store.Read();
                config.CurseForgeApiKeyProtected = method == "curseforge.credentialClear"
                    ? "" : SecureStore.Protect(supplied!);
                store.Save(config);
                return Success(id, new {
                    configured = method != "curseforge.credentialClear",
                    verifiedNow = method == "curseforge.credentialSave"
                });
            }
            finally { _gate.Release(); }
        }
        catch (HttpRequestException) { return Error(id, "UM-CURSEFORGE-VERIFY-FAILED"); }
        catch (TaskCanceledException) { return Error(id, "UM-CURSEFORGE-TIMEOUT"); }
        catch (Exception)
        {
            // Never serialize raw native exceptions (which may contain secrets).
            return Error(id, "UM-CREDENTIAL-OPERATION-FAILED");
        }
    }

    private static string Success(string id, object data) =>
        JsonSerializer.Serialize(new { id, ok = true, data }, Json);
    private static string Error(string? id, string code) =>
        JsonSerializer.Serialize(new { id, ok = false, error = code }, Json);
    public void Dispose() { _gate.Dispose(); _http.Dispose(); }
}
