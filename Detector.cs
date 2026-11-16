namespace GuardText;

// "Sunucudan çık" baskısı algılama. Kısa kelime aramak yerine anlam kalıbı arar:
//  - çık / git / ayrıl / defol / leave fiilinin emir ya da rica hâli (çıksana, çıkın, gider misin, ayrılsana...)
//  - "çıkar / gider / ayrılır / çıkabilir" + "mısın/misin/musun/mi" sorusu
//  - "müsaade eder misin", "çıkarsan", "çıksan" gibi rica kalıpları
// "Ben çıktım", "çıkıyorum", "açık", "1v1 atalım mı" gibi masum mesajlar yakalanmaz.
// Ayar dosyasındaki ExtraPhrases mesajın boşluksuz hâlinde aranır (sadece 6+ harfli, net ifadeler yaz).
public class KickDetector
{
    // Tek başına emir/rica olan kelimeler (sadeleştirilmiş: ı->i, ç->c..., tekrar eden harf teke iner)
    public static readonly string[] Imperatives =
    {
        "cik", "ciksana", "ciksane", "ciksene", "ciksaniza", "ciksanize", "ciksenize", "cikin", "cikiniz", "cikarsan",
        "cikarsaniz", "ciksan", "ciksaniz", "ciksana", "cikivermisin", "cikiver", "cikiverin",
        "git", "gitsene", "gitsana", "gitsenize", "gidin", "gidiniz", "gidersen", "gitsen", "gitseniz", "gidiver",
        "ayril", "ayrilsana", "ayrilsene", "ayrilin", "ayrilsan", "ayrilirsan", "ayrilirsaniz",
        "defol", "defolsana", "defolun", "terket", "terketsene", "terkedin", "leave", "quit", "disconnect",
        "cikarmisin", "cikarmisiniz", "cikabilirmisin", "cikabilirmisiniz", "cikarsanmi", "ciksanmi", "cikmazmisin",
        "gidermisin", "gidebilirmisin", "gidermisiniz", "ayrilirmisin", "ayrilabilirmisin", "musadeedermisin", "musadeeder"
    };
    // Soruyla birlikte rica olan fiil hâlleri ("çıkar mısın")
    public static readonly string[] AskStems =
    {
        "cikar", "cikabilir", "cikmaz", "cikmayacak", "cikiver", "gider", "gidebilir", "gitmez", "ayrilir", "ayrilabilir",
        "ayrilmaz", "terkeder", "musade", "musaade", "izinverir", "yerverir", "yerimizeverir", "leave"
    };
    // "mısıni", "musunu" (2026-11-01, sohbet kaydından: "ksnks çıkar mısıni")
    public static readonly string[] AskWords = { "misin", "misiniz", "musun", "musunuz", "misn", "msn", "musn", "misini", "musunu", "misinz" };
    // Sadece bunlar "mu/mı" ile rica sayılır ("awp çıkar mı" gibi masum sorular yakalanmasın)
    private static readonly HashSet<string> AskMu = new() { "cikabiliyon", "cikabiliyonuz", "cikabilirsin", "gidebiliyon", "ayrilabiliyon",
        "cikabilcen", "cikabilcenmi", "cikabilecen", "cikabilcekmisin" };
    // Bitişik yazılmış "çıkabilcen mi" hâlleri tek kelime
    private static readonly HashSet<string> AskJoined = new() { "cikabilcenmi", "cikabilecenmi", "cikabilcekmisin", "cikabilecekmisin", "cikabilirmisn" };
    // "bro i play vs pls": vs/1v1 isteği + rica kelimesi (2026-10-25, oyuncu isteğini mesajlara bölmüştü)
    private static readonly HashSet<string> VsWords = new() { "vs", "1v1", "1vs1", "2v2", "2vs2", "3v3", "1v1e", "vsye" };
    private static readonly HashSet<string> PleaseWords = new() { "pls", "plz", "please", "plis", "pliz", "pleas" };
    private static readonly HashSet<string> Negations = new() { "not", "no", "nope", "dont", "yok", "degil", "yasak", "olmaz" };
    // İngilizce "step out / go out / go away" (2026-10-30, Discord kaydından: "could you guys step out?", "bro go out").
    // "i/we/lets/to go out" kendinden bahseder; "go out long/mid" harita taktiğidir, ikisi de sayılmaz.
    private static readonly HashSet<string> GoVerbs = new() { "step", "go", "get" };
    private static readonly HashSet<string> OutWords = new() { "out", "outside", "away" };
    private static readonly HashSet<string> SelfWords = new() { "i", "im", "ill", "we", "lets", "to", "gona", "wil" };
    private static readonly HashSet<string> Callouts = new() { "long", "short", "mid", "middle", "a", "b", "site", "ramp", "banana", "tunel", "tunels", "door", "doors", "window", "spawn", "smoke", "flash" };

    // Çık baskısı değil (2026-10-25, Discord sohbet kaydından): "skin çıkarsana", "eksiklerin giderse"
    private static readonly HashSet<string> NotKick = new() { "cikarsana", "cikarsane", "cikarsaniza", "giderse" };
    // Vs atanlara karşı savunma cümleleri: "gidin özel sunucu kiralayın", "gidin atölye haritasında atın"
    private static readonly string[] Defensive = { "kirala", "atolye", "ozelsunucu", "esporcu" };

    private readonly HashSet<string> _imp, _ask, _askWords;
    private readonly List<string> _extra;

    public KickDetector(IEnumerable<string>? extraPhrases)
    {
        _imp = Imperatives.Select(TextMatch.Normalize).ToHashSet();
        _ask = AskStems.Select(TextMatch.Normalize).ToHashSet();
        _askWords = AskWords.Select(TextMatch.Normalize).ToHashSet();
        // Kısa ifadeler (eski "vs", "cik", "misin" gibi) masum mesajları da yakaladığı için 6 harften kısası alınmaz
        _extra = (extraPhrases ?? Enumerable.Empty<string>()).Select(TextMatch.Normalize).Where(p => p.Length >= 6).Distinct().ToList();
    }

    // Soru ekinden önceki fiil: tam ya da 5+ harfli kökte tek yazım hatası (2026-11-01: "çıakr mısın" harf yer değiştirmiş,
    // "ckar mısın" harf eksik). TextMatch.Near 6 harften kısaya bakmadığı için "çıkar" kaçıyordu. Arkadan soru eki
    // geldiği için kısa kökte de güvenli: "yapar mısın", "gelir misin" hiçbir köke tek hatayla dönmüyor.
    private bool AskStem(string t)
    {
        if (_ask.Contains(t)) return true;
        foreach (var p in _ask)
        {
            if (p.Length < 5) continue;
            if (t.Length == p.Length)
            {
                int d = 0; while (d < t.Length && t[d] == p[d]) d++;
                if (d + 1 < t.Length && t[d] == p[d + 1] && t[d + 1] == p[d] && t[(d + 2)..] == p[(d + 2)..]) return true;   // yer değiştirme
            }
            else if (t.Length + 1 == p.Length)
                for (int k = 0; k < p.Length; k++)
                    if (p.Remove(k, 1) == t) return true;                                                        // tek harf eksik
        }
        return false;
    }

    public string? Check(string? message)
    {
        var t = TextMatch.Tokens(message);
        var defensive = Defensive.Any(d => string.Concat(t).Contains(d));
        for (int i = 0; i < t.Count; i++)
        {
            if (NotKick.Contains(t[i])) continue;
            if (defensive && (t[i] == "gidin" || t[i] == "gidiniz" || t[i] == "git")) continue;
            if (_imp.Contains(t[i])) return t[i];
            if (AskJoined.Contains(t[i])) return t[i];
            foreach (var w in _imp) if (w.Length >= 7 && TextMatch.Near(t[i], w)) return w;   // yazım hatası (ciksnaa, gitsnee)
            if (AskStem(t[i]) && i + 1 < t.Count && _askWords.Contains(t[i + 1])) return t[i] + " " + t[i + 1];
            if (_ask.Contains(t[i]) && i + 2 < t.Count && t[i + 1] == "eder" && _askWords.Contains(t[i + 2])) return t[i] + " eder " + t[i + 2];
            // "çıkabiliyon mu", "müsade var mı" (2026-10-24, sunucu sohbet kaydından)
            if (AskMu.Contains(t[i]) && i + 1 < t.Count && (t[i + 1] == "mu" || t[i + 1] == "mi")
                && !(i > 0 && (t[i - 1] == "basa" || t[i - 1] == "basina"))) return t[i] + " " + t[i + 1];   // "hileyle başa çıkabiliyon mu"
            if (GoVerbs.Contains(t[i]) && i + 1 < t.Count && OutWords.Contains(t[i + 1])
                && !(i > 0 && SelfWords.Contains(t[i - 1])) && !(i + 2 < t.Count && Callouts.Contains(t[i + 2])))
                return t[i] + " " + t[i + 1];
            if ((t[i] == "musade" || t[i] == "musaade" || t[i] == "musaide") && i + 2 < t.Count && t[i + 1] == "var" && (t[i + 2] == "mi" || t[i + 2] == "mu"))
                return t[i] + " var " + t[i + 2];
        }
        // "this server for not vs", "vs yasak" gibi itirazlar sayılmaz
        if (t.Any(VsWords.Contains) && t.Any(PleaseWords.Contains) && !t.Any(Negations.Contains)) return "vs + pls";
        var compact = string.Concat(t);
        foreach (var p in _extra) if (TextMatch.ContainsNear(compact, p)) return p;
        return null;
    }
}

// Mesajlara bölünmüş çık baskısı (2026-10-25: "bro" / "i play" / "vs" / "pls" ayrı ayrı yazılmıştı): oyuncunun son
// WindowSeconds içindeki mesajları birleştirilip bir de öyle bakılır. Yakalanınca o oyuncunun geçmişi silinir ki aynı
// söz için ikinci kez ceza yazılmasın. Eklenti ve test programı (guardtest) aynı sınıfı kullanır.
public class KickHistory
{
    public const double WindowSeconds = 20;
    private const int MaxMessages = 5;
    private readonly KickDetector _detector;
    private readonly Dictionary<ulong, List<(DateTime at, string msg)>> _recent = new();

    public KickHistory(KickDetector detector) { _detector = detector; }

    public string? Check(ulong player, DateTime now, string message)
    {
        if (!_recent.TryGetValue(player, out var list)) _recent[player] = list = new();
        list.RemoveAll(x => (now - x.at).TotalSeconds > WindowSeconds);
        var hit = _detector.Check(message);
        if (hit == null && list.Count > 0)
        {
            var joined = string.Join(" ", list.Select(x => x.msg).TakeLast(MaxMessages - 1).Append(message));
            hit = _detector.Check(joined);
            if (hit != null) hit += " (bölünmüş)";
        }
        if (hit != null) list.Clear(); else list.Add((now, message));
        return hit;
    }

    public void Forget(ulong player) => _recent.Remove(player);
}
