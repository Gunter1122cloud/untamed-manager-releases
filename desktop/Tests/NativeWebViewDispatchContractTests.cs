using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UntamedDesktop;

/// <summary>
/// CI-wide audit of the REAL React -> WebView2 host -> native bridge method
/// contract. This deliberately reads the checked-out production sources,
/// not a hand-maintained, easily stale "test" allowlist. Never sends RCON,
/// starts/stops ASA, writes Kova data, or calls mutating RPCs.
/// </summary>
internal static class NativeWebViewDispatchContractTests
{
    private static void Check(bool valid,string reason)
    {
        if(!valid)throw new InvalidOperationException(
            "OBSIDIAN WEBVIEW DISPATCH CONTRACT FAILED: "+reason);
        Console.WriteLine("PASS production WebView2 dispatch: "+reason);
    }

    private static string RepoRoot()
    {
        foreach(var candidate in new[]{
            Directory.GetCurrentDirectory(),AppContext.BaseDirectory})
        {
            DirectoryInfo? dir=new DirectoryInfo(candidate);
            while(dir!=null)
            {
                if(File.Exists(Path.Combine(dir.FullName,"desktop-ui","src","bridge.ts"))&&
                   File.Exists(Path.Combine(dir.FullName,"desktop","ObsidianWindow.cs")))
                    return dir.FullName;
                dir=dir.Parent;
            }
        }
        throw new DirectoryNotFoundException(
            "The native dispatcher contract test requires a source checkout");
    }

    private static HashSet<string> Methods(string source)=>
        Regex.Matches(source,"\"([a-z][A-Za-z0-9]*\\.[A-Za-z0-9]+)\"")
            .Cast<Match>().Select(m=>m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static string Between(string source,string from,string to)
    {
        int a=source.IndexOf(from,StringComparison.Ordinal);
        int b=a<0?-1:source.IndexOf(to,a+from.Length,StringComparison.Ordinal);
        if(a<0||b<=a)throw new InvalidDataException(
            "Native route source delimiter changed: "+from);
        return source[a..b];
    }

    public static void Run()
    {
        string root=RepoRoot();
        string Read(string relative)=>File.ReadAllText(
            Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
        string react=Read("desktop-ui/src/bridge.ts");
        string host=Read("desktop/ObsidianWindow.cs");
        string fallback=Read("desktop/DesktopReadOnlyBridge.cs");
        string historical=Read("desktop/DesktopHistoricalStoreImportBridge.cs");

        // Obsidian ships a separately built WPF/Core-compatible rollback
        // updater and compiles legacy test projects. CI must also run when
        // those dependencies change, not just when desktop/ changes.
        string workflow=Read(".github/workflows/build-obsidian-desktop.yml");
        foreach(var watched in new[]{
            "desktop/**","desktop-ui/**","wpf/Core/**",
            "wpf/Updater/**","wpf/Testing/**","wpf/Assets/**"})
        {
            int occurrences=Regex.Matches(workflow,
                Regex.Escape("\""+watched+"\"")).Count;
            Check(occurrences==2,
                "both main push and PR triggers watch dependency: "+watched);
        }

        var declared=Methods(Between(react,"type WireRequest =","type WireResponse ="));
        var requested=Methods(react[react.IndexOf(
            "export const managerBridge =",StringComparison.Ordinal)..]);
        Check(declared.Count>=200 && declared.SetEquals(requested),
            "all "+requested.Count+" React managerBridge requests are present in WireRequest");

        string routeSource=Between(host,"credentialRoute = name is",
            "catch (JsonException)");
        string originalRoute=Between(host,
            "internal static bool IsHistoricalStoreMethod(",
            "internal static Task<string> DispatchHistoricalOrReadOnlyAsync");
        var native=Methods(routeSource);
        native.UnionWith(Methods(originalRoute));
        var defaultAllow=Methods(Between(fallback,
            "if (method is not (","return Error(id, \"UM-METHOD-DENIED\");"));
        var everyRouted=new HashSet<string>(native,StringComparer.Ordinal);
        everyRouted.UnionWith(defaultAllow);
        var missing=requested.Except(everyRouted).OrderBy(x=>x).ToArray();
        var unexpected=everyRouted.Except(declared).OrderBy(x=>x).ToArray();
        Check(missing.Length==0,
            "no React method falls through as UM-METHOD-DENIED: "+
            string.Join(", ",missing));
        Check(unexpected.Length==0,
            "no native route exists outside the React wire contract: "+
            string.Join(", ",unexpected));
        Check(native.Intersect(defaultAllow).Count()==0,
            "read-only fallback and privileged native dispatcher never conflict");

        // Every method in the historical native handler must be forwarded
        // by the very predicate used by the real WebMessageReceived event.
        var historicalAllowed=Methods(Between(historical,
            "if(method is not (","return Fail(id,\"UM-METHOD-DENIED\");"));
        var unroutedHistorical=historicalAllowed.Where(m=>
            !ObsidianWindow.IsHistoricalStoreMethod(m)).ToArray();
        Check(unroutedHistorical.Length==0,
            "all original Kova native methods are forwarded, including live 5-map preview/apply: "+
            string.Join(", ",unroutedHistorical));
        Check(ObsidianWindow.IsHistoricalStoreMethod(
            "historical.operationJournalInventory")&&
              !ObsidianWindow.RequiresGlobalBridgeGate(
            "historical.operationJournalInventory")&&
              ObsidianWindow.IsHistoricalStoreMethod(
            "historical.originalLiveBatchPreview")&&
              ObsidianWindow.IsHistoricalStoreMethod(
            "historical.originalLiveBatchApply")&&
              !ObsidianWindow.IsHistoricalStoreMethod(
            "historical.unsafeOperation"),
            "real dispatcher includes both live 5-map methods and denies unknown methods");

        // The previous plugin JSON, ZIP installer and runtime-toggle routes
        // remain parseable by older UI clients but MUST fail closed at the
        // actual WebView2 dispatcher. Only folder-open/inventory is allowed.
        string[] forbiddenPlugins={
            "plugins.togglePreview","plugins.toggleApply",
            "plugins.zipSelect","plugins.zipApply",
            "plugins.zipRollbackSelect","plugins.zipRollbackApply",
            "plugins.jsonOpen","plugins.jsonPreview",
            "plugins.jsonSave","plugins.jsonClose"
        };
        Check(forbiddenPlugins.All(ObsidianWindow.IsManualOnlyDeniedPluginMethod)&&
              !ObsidianWindow.IsManualOnlyDeniedPluginMethod("plugins.openFolder")&&
              !ObsidianWindow.IsManualOnlyDeniedPluginMethod("plugins.inventory")&&
              !ObsidianWindow.IsManualOnlyDeniedPluginMethod("plugins.state"),
            "shipping WebView2 accepts manual plugin folder/inventory but denies retired writes");
        var pluginRequestId=Guid.NewGuid();
        using(var refused=System.Text.Json.JsonDocument.Parse(
            ObsidianWindow.NativeErrorResponse(
                System.Text.Json.JsonSerializer.Serialize(
                    new{id=pluginRequestId.ToString("D")}),
                "UM-PLUGIN-MANUAL-FOLDER-ONLY")))
        {
            Check(refused.RootElement.GetProperty("id").GetString()==
                    pluginRequestId.ToString("D")&&
                  !refused.RootElement.GetProperty("ok").GetBoolean()&&
                  refused.RootElement.GetProperty("error").GetString()==
                    "UM-PLUGIN-MANUAL-FOLDER-ONLY",
                "refused plugin mutation yields correlated, explicit error without file changes");
        }

        // Ensure each named routing group points to an actual compiled
        // handler AND that the target handler contains its requested method.
        // This catches copied/pasted route groups wired to a wrong bridge,
        // not only forgotten route names.
        var targets=Regex.Matches(host,
            @"(?<flag>\w+Route)\s*\?\s*await\s+_(?<field>\w+)\.HandleAsync\(incoming\)")
            .Cast<Match>().ToDictionary(
                m=>m.Groups["flag"].Value,
                m=>m.Groups["field"].Value,StringComparer.Ordinal);
        var groups=Regex.Matches(routeSource,
            @"(?<flag>\w+Route)\s*=\s*name\s+is(?<body>.*?);",
            RegexOptions.Singleline).Cast<Match>().ToArray();
        var duplicate=groups.SelectMany(g=>Methods(g.Groups["body"].Value))
            .GroupBy(x=>x,StringComparer.Ordinal).Where(g=>g.Count()>1)
            .Select(g=>g.Key).ToArray();
        Check(duplicate.Length==0,
            "each privileged method selects only one dispatcher route: "+
            string.Join(", ",duplicate));
        var handlerIssues=new List<string>();
        foreach(var group in groups)
        {
            var flag=group.Groups["flag"].Value;
            if(!targets.TryGetValue(flag,out string? fieldName))
            {
                handlerIssues.Add(flag+" has no dispatcher destination");
                continue;
            }
            var field=typeof(ObsidianWindow).GetField("_"+fieldName,
                BindingFlags.Instance|BindingFlags.NonPublic);
            if(field==null)
            {
                handlerIssues.Add(flag+" has no compiled native handler field");
                continue;
            }
            string path="desktop/"+field.FieldType.Name+".cs";
            if(!File.Exists(Path.Combine(root,path)))
            {
                handlerIssues.Add(flag+" handler source missing: "+path);
                continue;
            }
            var implemented=Methods(Read(path));
            foreach(var method in Methods(group.Groups["body"].Value))
                if(!implemented.Contains(method))
                    handlerIssues.Add(method+" forwarded to "+path+
                        " but handler does not name that method");
        }
        Check(handlerIssues.Count==0,
            "all "+groups.Length+" privileged route groups target existing native handlers: "+
            string.Join("; ",handlerIssues.Take(20)));
        Check(requested.SetEquals(everyRouted),
            "all "+requested.Count+" production React methods have one eligible host dispatcher route");

        // The signed ASA API CORE binary updater already has a native handler.
        // Regression: it must actually be reachable from the shipped React
        // Plugin Center for eligible local maps, not only exist as a dead
        // standalone source file. Plugin DLL installation stays manual-only.
        string pluginCenter=Read("desktop-ui/src/App.tsx");
        string apiCoreUi=Read("desktop-ui/src/AsaApiCoreInstaller.tsx");
        Check(pluginCenter.Contains(
                "import { AsaApiCoreInstaller } from \"./AsaApiCoreInstaller\";",
                StringComparison.Ordinal)&&
              pluginCenter.Contains("<AsaApiCoreInstaller key={selected}",
                StringComparison.Ordinal)&&
              pluginCenter.Contains("!owned.storeProtected&&!owned.freshStoreEnrolled",
                StringComparison.Ordinal)&&
              pluginCenter.Contains("!owned.observedReadOnlyImported",
                StringComparison.Ordinal),
            "ASA API CORE install/rollback component is mounted for eligible local maps only");
        foreach(string operation in new[]{
            "apiCoreRelease","apiCorePreview","apiCoreApply",
            "apiCoreRollbackCandidates","apiCoreRollbackPreview",
            "apiCoreRollbackApply"})
            Check(apiCoreUi.Contains("managerBridge."+operation+"(",
                    StringComparison.Ordinal),
                "ASA API CORE UI calls production native method: "+operation);
        Check(pluginCenter.Contains("Pluginmappa megnyitása",
                StringComparison.Ordinal)&&
              !pluginCenter.Contains("<PluginZipInstaller",
                StringComparison.Ordinal),
            "CORE binary installer does not reintroduce automated plugin ZIP installation");

        // Three independent native Loader lifecycle paths must not impose
        // older 2–3-minute false-failure windows on large modded ASA worlds.
        // All use the same bounded read-only family/RCON verification period;
        // nothing here permits background Process.Start or command replay.
        Check(FreshStoreApiPairObserver.StartupObservationWindow==
                TimeSpan.FromMinutes(10),
            "production ASA Loader-child observation window is ten minutes");
        foreach(var path in new[]{
            "desktop/DesktopFreshAsaRestartBridge.cs",
            "desktop/DesktopHistoricalRestartBridge.cs",
            "desktop/DesktopHistoricalActiveStartBridge.cs"})
        {
            Check(Read(path).Contains(
                    "FreshStoreApiPairObserver.StartupObservationWindow",
                    StringComparison.Ordinal),
                "native Loader lifecycle uses shared bounded readiness interval: "+path);
        }
    }
}
