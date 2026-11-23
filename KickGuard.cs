using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using GuardText;

namespace KickGuard;

// Kaynak 2026-10-21'de sunucudaki KickGuard.dll'den geri çıkarıldı (masaüstündeki klasörde yanlışlıkla EmptyReset.cs vardı).
public class KickGuardConfig : BasePluginConfig
{
    // Bu yetkisi olan oyuncular (admin, moderatör) kontrol edilmez (kullanıcı 2026-10-25: "admine küfürde serbest olsun").
    // Boş bırakılırsa herkes kontrol edilir.
    [JsonPropertyName("AdminExemptFlag")]
    public string AdminExemptFlag { get; set; } = "@css/generic";

    // Ek ifadeler: mesajın boşluksuz, sadeleştirilmiş hâlinde aranır. Sadece 6+ harfli, net ifadeler alınır
    // (eski "vs", "cik", "misin" gibi kısa kalıplar "1 vs 1", "açık", "gelir misin" gibi masum mesajları da yakalıyordu).
    // Asıl algılama Detector.cs'te: çık/git/ayrıl fiilinin emir ve rica hâlleri, yazım hataları dahil.
    [JsonPropertyName("Patterns")]
    public List<string> Patterns { get; set; } = new() { "vs atiyoz cikar", "sana zahmet cik", "yerini bosalt", "get out" };

    // BanMinutes / BanCommand artık kullanılmıyor (3. ihlal ban değil); eski ayar dosyası bozulmasın diye duruyor
    [JsonPropertyName("BanMinutes")]
    public int BanMinutes { get; set; } = 1440;

    // 2. ihlal: yazı susturma (gag). SimpleAdmin komutuyla verilir ki panelde (sa_mutes) görünsün (kullanıcı 2026-10-23:
    // "çık diyenler gag yemiyor, panelde görünmüyor"). Eskiden 2. ihlal sadece kickid ile atıyordu, hiçbir yere kaydolmuyordu.
    [JsonPropertyName("GagMinutes")]
    public int GagMinutes { get; set; } = 60;

    [JsonPropertyName("GagCommand")]
    public string GagCommand { get; set; } = "css_gag #{userid} {minutes} \"{reason}\"";

    // 3. ihlal: ban yerine uzun yazı susturma (kullanıcı 2026-10-25: "3. kezde ban olmasın, 1 gün gag olsun")
    [JsonPropertyName("LongGagMinutes")]
    public int LongGagMinutes { get; set; } = 1440;
    [JsonPropertyName("KickCommand")]
    public string KickCommand { get; set; } = "kickid {userid}";

    [JsonPropertyName("BanCommand")]
    public string BanCommand { get; set; } = "css_ban #{userid} {minutes} \"{reason}\"";
}

public class KickGuard : BasePlugin, IPluginConfig<KickGuardConfig>
{
    public override string ModuleName => "KickGuard";
    public override string ModuleVersion => "2.12.0";
    public override string ModuleAuthor => "Claude";
    public override string ModuleDescription => "Başkalarını sunucudan çıkmaya zorlayan oyunculara uyarı ve yazı susturma (gag) uygular.";

    public KickGuardConfig Config { get; set; } = new();

    private KickDetector _detector = new(null);
    private KickHistory _history = new(new KickDetector(null));
    private readonly Dictionary<ulong, int> _strikes = new();

    public void OnConfigParsed(KickGuardConfig config)
    {
        Config = config;
        _detector = new KickDetector(config.Patterns);
        _history = new KickHistory(_detector);
    }

    public override void Load(bool hotReload)
    {
        AddCommandListener("say", OnSay);
        AddCommandListener("say_team", OnSay);
        // Yükleme sırasında önümüzdeki T3MenuAPI takım mesajlarını sonraki eklentilere geçirmiyor (2026-11-03: KickGuard hiç
        // TEAM mesajı yakalamamıştı). Mesaj oyunda yine yayınlandığı için OnPlayerChat tetikleniyor: komut dinleyicisine
        // ulaşmayan takım mesajları buradan kontrol edilir. Mesaj gizlenemez ama uyarı/susturma uygulanır.
        RegisterListener<Listeners.OnPlayerChat>((player, message, teamChat) =>
        {
            if (!teamChat || player == null || !player.IsValid) return;
            if (_seen.TryGetValue(player.SteamID, out var s) && s.msg == message && (DateTime.Now - s.at).TotalSeconds < 2) return;
            Handle(player, message, true);
        });
    }

    // Komut dinleyicisine ulaşan son mesaj (OnPlayerChat aynı mesajı ikinci kez cezalandırmasın)
    private readonly Dictionary<ulong, (string msg, DateTime at)> _seen = new();

    private HookResult OnSay(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || player.IsBot)
            return HookResult.Continue;
        _seen[player.SteamID] = (info.GetArg(1), DateTime.Now);
        return Handle(player, info.GetArg(1), info.GetArg(0) == "say_team") ? HookResult.Handled : HookResult.Continue;
    }

    // Yakalanırsa uyarı/susturma uygular ve true döner
    private bool Handle(CCSPlayerController player, string message, bool team)
    {
        if (player.IsBot)
            return false;
        if (!string.IsNullOrWhiteSpace(Config.AdminExemptFlag) && AdminManager.PlayerHasPermissions(player, Config.AdminExemptFlag))
            return false;

        var hit = _history.Check(player.SteamID, DateTime.Now, message);
        if (hit == null)
            return false;

        var steamId = player.SteamID;
        _strikes.TryGetValue(steamId, out var count);
        count++;
        _strikes[steamId] = count;

        var name = player.PlayerName;
        Console.WriteLine($"[KickGuard] {name} ({steamId}) ihlal {count}: '{hit}'");
        // Panelin canlı chat'ine ve Discord'a uyarı notu (kullanıcı 2026-10-25: "küfür eklentisi gibi onu da yazsın")
        var note = count == 1 ? "1. kez uyarıldı" : count == 2 ? $"2. kez: {Config.GagMinutes} dk susturuldu" : $"{count}. kez: {Sure(Config.LongGagMinutes)} susturuldu";
        ChatNote.Write(ModuleDirectory, "Çık baskısı", name, steamId, team, message, note);

        switch (count)
        {
            case 1:
                player.PrintToChat($" {ChatColors.Red}[Uyarı]{ChatColors.Default} Diğer oyuncuları sunucudan çıkmaya zorlamak yasak. Bir daha olursa {Config.GagMinutes} dk, üçüncüsünde {Sure(Config.LongGagMinutes)} susturulursun. (1/3)");
                break;
            case 2:
                Server.PrintToChatAll($" {ChatColors.Red}[KickGuard]{ChatColors.Default} {name} oyuncuları sunucudan çıkmaya zorladığı için {Config.GagMinutes} dk susturuldu.");
                Run(Config.GagCommand, player, "Oyunculari cikmaya zorlamak (2. uyari)", Config.GagMinutes);
                break;
            default:
                Server.PrintToChatAll($" {ChatColors.Red}[KickGuard]{ChatColors.Default} {name} tekrar ettiği için {Sure(Config.LongGagMinutes)} susturuldu.");
                Run(Config.GagCommand, player, "Oyunculari cikmaya zorlamak (3. uyari)", Config.LongGagMinutes);
                _strikes[steamId] = 0; // ceza sonrası sayaç sıfırlanır (SwearGuard gibi)
                break;
        }
        return true;
    }

    [ConsoleCommand("css_cezasifirla", "Tüm ihlal sayaçlarını sıfırlar (sadece sunucu konsolundan)")]
    public void OnResetStrikes(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null) return;
        _strikes.Clear();
        command.ReplyToCommand("[KickGuard] Tüm ihlal sayaçları sıfırlandı.");
    }

    // Panelin "Pasif yap" düğmesi için (2026-11-03): css_reloadadmins admins.json'dan çıkarılan admini bellekten silmiyor,
    // yetkisi sunucu yeniden başlayana kadar kalıyordu. Bu komut o SteamID'nin yetkilerini hemen kaldırır (sadece konsoldan/RCON).
    [ConsoleCommand("css_adminsil", "Verilen SteamID64'ün admin yetkilerini bellekten siler (sadece sunucu konsolundan)")]
    public void OnRemoveAdmin(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null) return;
        if (!ulong.TryParse(command.GetArg(1), out var id) || id < 76561190000000000UL) { command.ReplyToCommand("[KickGuard] Kullanım: css_adminsil <steamid64>"); return; }
        AdminManager.RemovePlayerAdminData(new CounterStrikeSharp.API.Modules.Entities.SteamID(id));
        command.ReplyToCommand($"[KickGuard] {id} admin yetkileri bellekten silindi.");
    }

    private void Run(string template, CCSPlayerController player, string reason, int minutes)
    {
        if (player.UserId is not int userId) return;
        var cmd = template
            .Replace("{userid}", userId.ToString())
            .Replace("{steamid}", player.SteamID.ToString())
            .Replace("{minutes}", minutes.ToString())
            .Replace("{reason}", reason);
        Console.WriteLine($"[KickGuard] komut: {cmd}");
        Server.ExecuteCommand(cmd);
    }

    private static string Sure(int minutes) => minutes % 1440 == 0 ? $"{minutes / 1440} gün" : minutes % 60 == 0 ? $"{minutes / 60} saat" : $"{minutes} dk";
}
