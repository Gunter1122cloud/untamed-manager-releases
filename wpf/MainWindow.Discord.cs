using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UntamedManager.Core;
using WinMessageBox=System.Windows.MessageBox;

namespace UntamedManager;
public partial class MainWindow
{
    private void ShowDiscordStatusPage()
    {
        Header("Discord · szerverstátusz",
            "Kizárólag kézi, egyirányú státuszküldés a saját Discord-csatornára. "+
            "Nem távoli RCON és nem Discord-bot admin.");
        var key=new PasswordBox{Height=36,FontSize=14,Background=Panel,
            Foreground=Brushes.White};
        var config=_cluster.Config;
        var status=Label(string.IsNullOrWhiteSpace(config.DiscordWebhookProtected)?
            "Discord webhook nincs beállítva.":
            "Discord webhook DPAPI-titkosítva tárolva. A titkos URL nem jelenik meg.",
            12,Soft);
        Add(Box(V(
            Label("UNTAMED KINGDOM · DISCORD STÁTUSZ",20,Mint,true),
            Label("Az elküldés kizárólag hitelesített és friss helyi "+
                "Supervisor-adatokból történik. A távoli gépről nem állítunk "+
                "bizonyíték nélkül online állapotot. A webhookot nem küldjük "+
                "diagnosztikai ZIP-be és nem naplózzuk.",12,Soft),
            Label("Discord webhook URL · HTTPS, titkos bemenet",12,Aqua),
            key,
            Btn("Webhook biztonságos mentése (Windows DPAPI)",()=>{
                string address=key.Password.Trim();key.Clear();
                try
                {
                    if(!DiscordStatusPublisher.ValidWebhook(address))
                        throw new InvalidDataException("Csak hivatalos Discord webhook cím fogadható el");
                    _cluster.UpdateSettings(c=>
                        c.DiscordWebhookProtected=SecureStore.Protect(address));
                    status.Text="Webhook biztonságosan tárolva. "+
                        "Automatikus üzenetküldés továbbra sincs bekapcsolva.";
                    _events?.Append(_cluster.Config.LocalHostName,null,
                        "Information","discord-webhook-configured");
                }
                catch(Exception ex)
                {
                    status.Text="Webhook mentés elutasítva: "+ex.GetType().Name;
                }
            }),
            Btn("Webhook eltávolítása",()=>{
                if(WinMessageBox.Show(
                    "Eltávolítod a jelenlegi Discord webhookot ebből a helyi profilból?",
                    "Discord kapcsolat",MessageBoxButton.YesNo,MessageBoxImage.Question)
                    !=MessageBoxResult.Yes)return;
                key.Clear();
                _cluster.UpdateSettings(c=>c.DiscordWebhookProtected="");
                status.Text="Webhook eltávolítva; nem küldünk Discordra.";
            }),status)));

        var preview=Label("Még nincs ellenőrzött állapot-előnézet.",12,Soft);
        Add(Box(V(
            Label("Státusz előnézete",18,Mint,true),
            Btn("Friss Supervisor-állapot előnézete",()=>{
                try
                {
                    RefreshSupervision();
                    var snapshot=_supervisorSnapshot??
                        throw new IOException("Nincs friss, hitelesített Supervisor-állapot");
                    if(snapshot.HostId!=_cluster.Config.LocalHostId)
                        throw new UnauthorizedAccessException("Másik gép státusza");
                    preview.Text=DiscordStatusPublisher.Compose(snapshot,
                        _cluster.Config.LocalMaps.Select(x=>x.Id).ToHashSet(),
                        DateTimeOffset.UtcNow);
                }
                catch(Exception)
                {
                    preview.Text="Nem küldhető: hiányzik a friss hitelesített "+
                        "helyi Supervisor-mérés.";
                }
            }),
            Btn("Ellenőrzött állapot elküldése Discordra",async ()=>{
                if(string.IsNullOrEmpty(_cluster.Config.DiscordWebhookProtected))
                {
                    status.Text="Nincs beállított webhook.";
                    return;
                }
                try
                {
                    RefreshSupervision();
                    var snapshot=_supervisorSnapshot??
                        throw new IOException("No verified Supervisor observation");
                    if(snapshot.HostId!=_cluster.Config.LocalHostId)
                        throw new UnauthorizedAccessException("Foreign host snapshot");
                    string content=DiscordStatusPublisher.Compose(snapshot,
                        _cluster.Config.LocalMaps.Select(x=>x.Id).ToHashSet(),
                        DateTimeOffset.UtcNow);
                    if(WinMessageBox.Show(
                        content+"\n\nValóban elküldöd a státuszt a beállított Discord-csatornába?",
                        "Discord · egyszeri küldés",
                        MessageBoxButton.YesNo,MessageBoxImage.Question)
                        !=MessageBoxResult.Yes)return;
                    string webhook=SecureStore.Unprotect(
                        _cluster.Config.DiscordWebhookProtected);
                    // Isolated no-redirect HTTP handler prevents any webhook
                    // token from being followed to an unrelated domain.
                    using var client=new HttpClient(
                        new HttpClientHandler{AllowAutoRedirect=false}){
                        Timeout=TimeSpan.FromSeconds(12)};
                    var receipt=await DiscordStatusPublisher.SendAsync(
                        client,webhook,snapshot,
                        _cluster.Config.LocalMaps.Select(x=>x.Id).ToHashSet());
                    status.Text="Discord megerősítette az egyszeri állapotüzenetet. "+
                        "Üzenetazonosító: "+receipt.MessageId+
                        " · mapok: "+receipt.Maps;
                    _events?.Append(_cluster.Config.LocalHostName,null,
                        "Information","discord-status-confirmed");
                }
                catch(Exception ex)
                {
                    // Do not log raw HttpRequestException or secret URL.
                    status.Text="Discord nem igazolta az üzenet mentését. "+
                        "Hibatípus: "+ex.GetType().Name+
                        ". Ismeretlen státuszú küldést nem ismétlünk automatikusan.";
                }
            }),
            preview,
            Label("A Discord-küldés nem jelenti a karaktertranszfer, "+
                "a Vault-kompatibilitás vagy az ASA éles vezérlésének "+
                "hitelesítését. A gomb semmilyen szerverműveletet nem indít.",12,Soft))));
    }
}

