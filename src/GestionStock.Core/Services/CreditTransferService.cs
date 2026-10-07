using System.Collections.Concurrent;
using System.IO;
using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using GestionStock.Core.Data;
using GestionStock.Core.Domain;
using Microsoft.EntityFrameworkCore;

// Pour tester ExtractQuoted/ParseLastSms (analyse de texte pure, sans matériel) depuis les tests.
[assembly: InternalsVisibleTo("GestionStock.Tests")]

namespace GestionStock.Core.Services;

/// <summary>
/// Dialogue avec un modem GSM par port série pour un transfert de crédit : une requête USSD est une session en deux
/// échanges (le code envoyé, une invite de confirmation reçue, un chiffre renvoyé pour confirmer, un message final
/// reçu), et pour certains opérateurs (Mobilis), la confirmation réelle arrive séparément par SMS. Abstraction pour
/// permettre de tester <see cref="CreditTransferService"/> sans matériel réel.
/// </summary>
public interface IModemPort
{
    /// <summary>Envoie une requête USSD et renvoie le texte de la réponse (contenu d'un « +CUSD: » ), ou null si le modem ne répond pas ou renvoie une erreur.</summary>
    string? SendUssd(string comPort, string ussdCode, TimeSpan timeout);

    /// <summary>Index des SMS déjà présents sur la puce, pour ne retenir ensuite que ceux arrivés après (vide si la lecture échoue).</summary>
    IReadOnlySet<int> ListSmsIndexes(string comPort);

    /// <summary>
    /// Attend un SMS absent de <paramref name="existing"/> et accepté par <paramref name="accept"/>, dans le délai
    /// donné ; renvoie son texte puis le supprime du modem, ou null si rien de tel n'arrive. Les autres SMS ne sont
    /// pas touchés.
    /// </summary>
    string? WaitForSms(string comPort, IReadOnlySet<int> existing, Func<string, bool> accept, TimeSpan timeout);

    /// <summary>
    /// Attend les SMS absents de <paramref name="existing"/>, les recolle dans l'ordre d'arrivée (un message trop
    /// long arrive parfois coupé en plusieurs SMS, ex. « ...DAT » puis « A est : 105.00... »), et les supprime.
    /// Attend un peu après le premier message reçu, pour laisser le temps aux suivants d'arriver. Null si rien
    /// n'arrive dans le délai donné.
    /// </summary>
    string? WaitForSmsBatch(string comPort, IReadOnlySet<int> existing, TimeSpan timeout);

    /// <summary>
    /// Vérifie, sans rien envoyer à l'opérateur (ni USSD ni SMS, donc gratuit), que le modem répond, que la puce est
    /// prête et qu'elle est inscrite sur le réseau.
    /// </summary>
    ModemCheck CheckLink(string comPort);

    /// <summary>Occupation du stockage SMS actif (ex. 25/25 = plein : le réseau ne peut plus livrer de nouveau SMS), ou null si la question échoue.</summary>
    (int Used, int Total)? GetSmsStorageUsage(string comPort);

    /// <summary>
    /// Ferme explicitement une session USSD restée ouverte (ex. une réponse qui se termine par un menu « 1:... »),
    /// pour qu'une requête suivante ne soit pas refusée par le réseau (« max number of menu retries »). Un échec ici
    /// n'empêche jamais de continuer : au pire, la session expirera toute seule côté réseau.
    /// </summary>
    void CancelUssd(string comPort);
}

/// <summary>Résultat d'une vérification de liaison avec un modem : en ligne ou non, avec un message lisible.</summary>
public record ModemCheck(bool Online, string Message);

/// <summary>
/// Implémentation réelle par port série, avec les commandes AT standard (AT, AT+CUSD pour l'USSD, AT+CMGF/AT+CMGL/
/// AT+CMGD pour lire puis effacer un SMS en mode texte). N'a pas pu être testée sur un modem physique : le débit
/// (115200 bauds, comme sur la clé Vodafone K3772-H), le format exact des réponses et les délais varient selon le modem et devront très probablement être
/// ajustés une fois essayés avec du vrai matériel.
/// </summary>
public class SerialModemPort : IModemPort
{
    private const int BaudRate = 115200;
    private static readonly TimeSpan AtTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SmsPollInterval = TimeSpan.FromSeconds(2);

    public string? SendUssd(string comPort, string ussdCode, TimeSpan timeout)
    {
        try
        {
            using var port = Open(comPort);
            port.WriteLine("AT");
            if (!ReadUntil(port, AtTimeout, "OK", "ERROR", out var atReply) || Contains(atReply, "ERROR"))
            {
                LogDiagnostic(comPort, "AT initial a echoue", atReply);
                return null;
            }
            // Au cas où la réponse arrive par SMS séparé : doit être réglé dans cette même session, juste avant
            // l'envoi, car rouvrir le port ensuite peut remettre le modem sur sa mémoire interne par défaut.
            SelectSimStorage(port);

            port.WriteLine($"AT+CUSD=1,\"{ussdCode}\",15");
            if (!ReadUntil(port, timeout, "+CUSD:", "ERROR", out var reply) || !Contains(reply, "+CUSD:"))
            {
                LogDiagnostic(comPort, $"Pas de +CUSD: recu pour le code {ussdCode}", reply);
                return null;
            }
            var text = ExtractQuoted(reply, "+CUSD:");
            if (text == null) LogDiagnostic(comPort, "+CUSD: recu mais texte entre guillemets introuvable", reply);
            return text == null ? null : DecodeIfUcs2Hex(text);
        }
        catch (Exception ex)
        {
            LogDiagnostic(comPort, "Exception pendant SendUssd", ex.ToString());
            return null;
        }
    }

    /// <summary>
    /// Trace temporaire pour diagnostiquer un échange qui échoue sans raison apparente (ex. requête de solde sans
    /// réponse alors que la liaison de base fonctionne) : écrit ce qui a réellement été capturé sur le port.
    /// </summary>
    private static void LogDiagnostic(string comPort, string label, string content)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GestionStock", "modem-debug.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {comPort} — {label} :\n{content}\n---\n");
        }
        catch (Exception)
        {
            // Diagnostic seulement : ne doit jamais faire échouer l'appel réel.
        }
    }

    public IReadOnlySet<int> ListSmsIndexes(string comPort)
    {
        try
        {
            using var port = Open(comPort);
            port.WriteLine("AT+CMGF=1"); // mode texte (lisible), plutôt que le mode PDU par défaut
            ReadUntil(port, AtTimeout, "OK", "ERROR", out _);
            SelectSimStorage(port);
            port.WriteLine("AT+CMGL=\"ALL\"");
            ReadUntil(port, AtTimeout, "OK", "ERROR", out var reply);
            return ParseSmsList(reply).Select(s => s.Index).ToHashSet();
        }
        catch (Exception)
        {
            return new HashSet<int>();
        }
    }

    public string? WaitForSms(string comPort, IReadOnlySet<int> existing, Func<string, bool> accept, TimeSpan timeout)
    {
        try
        {
            using var port = Open(comPort);
            port.WriteLine("AT+CMGF=1");
            ReadUntil(port, AtTimeout, "OK", "ERROR", out _);
            SelectSimStorage(port);

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                port.WriteLine("AT+CMGL=\"ALL\"");
                if (ReadUntil(port, AtTimeout, "OK", "ERROR", out var reply))
                {
                    var matches = ParseSmsList(reply).Where(s => !existing.Contains(s.Index) && accept(s.Body)).ToList();
                    if (matches.Count > 0)
                    {
                        var sms = matches[^1];
                        port.WriteLine($"AT+CMGD={sms.Index}"); // supprimé pour ne pas être relu au prochain appel
                        ReadUntil(port, AtTimeout, "OK", "ERROR", out _);
                        return sms.Body;
                    }
                }
                Thread.Sleep(SmsPollInterval);
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Délai supplémentaire laissé après la réception d'un SMS, pour recueillir d'éventuels SMS suivants.</summary>
    private static readonly TimeSpan SmsBatchGrace = TimeSpan.FromSeconds(10);

    public string? WaitForSmsBatch(string comPort, IReadOnlySet<int> existing, TimeSpan timeout)
    {
        try
        {
            using var port = Open(comPort);
            port.WriteLine("AT+CMGF=1");
            ReadUntil(port, AtTimeout, "OK", "ERROR", out _);
            SelectSimStorage(port);

            var collected = new List<(int Index, string Body)>();
            var deadline = DateTime.UtcNow + timeout;
            var quietDeadline = DateTime.MaxValue;
            while (DateTime.UtcNow < deadline && DateTime.UtcNow < quietDeadline)
            {
                port.WriteLine("AT+CMGL=\"ALL\"");
                if (ReadUntil(port, AtTimeout, "OK", "ERROR", out var reply))
                {
                    var fresh = ParseSmsList(reply)
                        .Where(s => !existing.Contains(s.Index) && collected.All(c => c.Index != s.Index)).ToList();
                    if (fresh.Count > 0)
                    {
                        collected.AddRange(fresh);
                        quietDeadline = DateTime.UtcNow + SmsBatchGrace;
                    }
                }
                Thread.Sleep(SmsPollInterval);
            }
            if (collected.Count == 0) return null;

            foreach (var sms in collected)
            {
                port.WriteLine($"AT+CMGD={sms.Index}");
                ReadUntil(port, AtTimeout, "OK", "ERROR", out _);
            }
            // Concaténé sans séparateur : un message coupé en plein mot ("...DAT" + "A est...") doit se recoller tel quel.
            return string.Concat(collected.OrderBy(c => c.Index).Select(c => c.Body));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public ModemCheck CheckLink(string comPort)
    {
        try
        {
            using var port = Open(comPort);
            port.WriteLine("AT");
            if (!ReadUntil(port, AtTimeout, "OK", "ERROR", out var atReply) || Contains(atReply, "ERROR"))
                return new ModemCheck(false, "Le modem ne répond pas.");

            port.WriteLine("AT+CPIN?");
            ReadUntil(port, AtTimeout, "OK", "ERROR", out var pinReply);
            if (!Contains(pinReply, "READY"))
                return new ModemCheck(false, "Puce absente, ou bloquée par un code PIN.");

            // CREG = réseau 2G/3G, CEREG = réseau 4G : certaines clés ne s'inscrivent que sur l'un des deux.
            foreach (var command in new[] { "AT+CREG?", "AT+CEREG?" })
            {
                port.WriteLine(command);
                ReadUntil(port, AtTimeout, "OK", "ERROR", out var regReply);
                if (IsRegistered(regReply))
                    return new ModemCheck(true, "En ligne.");
            }
            return new ModemCheck(false, "La puce n'est pas inscrite sur le réseau (pas de signal ?).");
        }
        catch (Exception)
        {
            return new ModemCheck(false, $"Impossible d'ouvrir le port {comPort} (modem débranché ou déjà utilisé).");
        }
    }

    public (int Used, int Total)? GetSmsStorageUsage(string comPort)
    {
        try
        {
            using var port = Open(comPort);
            port.WriteLine("AT+CPMS?");
            ReadUntil(port, AtTimeout, "OK", "ERROR", out var reply);
            return ParseStorageUsage(reply);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void CancelUssd(string comPort)
    {
        try
        {
            using var port = Open(comPort);
            port.WriteLine("AT+CUSD=2");
            ReadUntil(port, AtTimeout, "OK", "ERROR", out _);
        }
        catch (Exception)
        {
            // Au pire, la session expirera toute seule côté réseau.
        }
    }

    /// <summary>Occupation du 1er stockage d'une réponse à « AT+CPMS? » (ex. « +CPMS: "SM",25,25,... » → 25 sur 25).</summary>
    internal static (int Used, int Total)? ParseStorageUsage(string reply)
    {
        var match = Regex.Match(reply, @"\+CPMS:\s*""[^""]*"",(\d+),(\d+)", RegexOptions.IgnoreCase);
        return match.Success ? (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)) : null;
    }

    /// <summary>
    /// Vrai si une réponse à « AT+CREG? » ou « AT+CEREG? » indique une inscription sur le réseau : état 1 (réseau
    /// de l'opérateur) ou 5 (itinérance), ex. « +CREG: 0,1 ».
    /// </summary>
    internal static bool IsRegistered(string reply)
    {
        var match = Regex.Match(reply, @"\+CE?REG:\s*(?:\d+,)?(\d+)", RegexOptions.IgnoreCase);
        return match.Success && match.Groups[1].Value is "1" or "5";
    }

    /// <summary>
    /// Force le stockage des SMS sur la carte SIM plutôt que dans la mémoire interne du modem. Sans ça, un SMS reçu
    /// pendant l'attente peut atterrir dans la mémoire du modem et devenir invisible à la lecture (« SM »), qui ne
    /// regarde que la SIM. À refaire avant chaque envoi qui attend une réponse par SMS : le réglage n'est pas garanti
    /// persistant d'une session à l'autre.
    /// </summary>
    private static void SelectSimStorage(SerialPort port)
    {
        port.WriteLine("AT+CPMS=\"SM\",\"SM\",\"SM\"");
        ReadUntil(port, AtTimeout, "OK", "ERROR", out _);
    }

    private static SerialPort Open(string comPort)
    {
        var port = new SerialPort(comPort, BaudRate)
        {
            // "\r" seul, comme un vrai terminal série (et comme l'application Puces, qui fonctionne sur ce matériel) :
            // "\r\n" laisse passer les commandes de base (AT, CPIN, CREG) mais semble empêcher la réponse USSD d'arriver.
            NewLine = "\r",
            ReadTimeout = 2000,
            WriteTimeout = 5000,
            DtrEnable = true,
            RtsEnable = true,
        };
        port.Open();
        port.DiscardInBuffer();
        return port;
    }

    /// <summary>Lit les lignes du port jusqu'à ce qu'une contienne un des marqueurs, ou que le délai global soit dépassé.</summary>
    private static bool ReadUntil(SerialPort port, TimeSpan timeout, string markerA, string markerB, out string result)
    {
        var deadline = DateTime.UtcNow + timeout;
        var buffer = new StringBuilder();
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var line = port.ReadLine();
                buffer.AppendLine(line);
                if (Contains(line, markerA) || Contains(line, markerB))
                {
                    result = buffer.ToString();
                    return true;
                }
            }
            catch (TimeoutException) { /* on retente jusqu'au délai global */ }
        }
        result = buffer.ToString();
        return false;
    }

    private static bool Contains(string text, string marker) => text.Contains(marker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Le modem renvoie parfois le texte (et le numéro) en UCS2 hexadécimal, ex. "0041004200430044" pour "ABCD".
    /// Seules les chaînes assez longues, de longueur multiple de 4 et uniquement hexadécimales sont décodées, pour ne
    /// pas toucher un texte court qui ressemble par hasard à de l'hexadécimal.
    /// </summary>
    internal static string DecodeIfUcs2Hex(string text)
    {
        if (text.Length < 8 || text.Length % 4 != 0 || !text.All(Uri.IsHexDigit)) return text;
        return Encoding.BigEndianUnicode.GetString(Convert.FromHexString(text));
    }

    /// <summary>Extrait le texte entre le premier couple de guillemets suivant un marqueur (ex. le message d'un « +CUSD: 1,"…",15 »).</summary>
    internal static string? ExtractQuoted(string text, string afterMarker)
    {
        var markerIndex = text.IndexOf(afterMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0) return null;
        // [^"]* et non .*? : certaines réponses USSD contiennent un vrai retour à la ligne dans le texte entre
        // guillemets (ex. un menu "1:Plus de detail..."), et "." ne correspond pas à un retour à la ligne par défaut.
        var match = Regex.Match(text[(markerIndex + afterMarker.Length)..], "\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Le SMS le plus récent d'une réponse à « AT+CMGL="ALL" » (voir <see cref="ParseSmsList"/>).</summary>
    internal static (int Index, string Body)? ParseLastSms(string listing)
        => ParseSmsList(listing) is { Count: > 0 } all ? all[^1] : null;

    /// <summary>
    /// Extrait les SMS d'une réponse à « AT+CMGL="ALL" », du plus ancien au plus récent : une ligne d'en-tête
    /// « +CMGL: index,... » suivie du corps du message sur la ou les lignes suivantes, jusqu'au prochain « +CMGL: »
    /// ou à la fin.
    /// </summary>
    internal static List<(int Index, string Body)> ParseSmsList(string listing)
    {
        var lines = listing.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var all = new List<(int Index, string Body)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var header = Regex.Match(lines[i], @"^\+CMGL:\s*(\d+)");
            if (!header.Success) continue;
            var index = int.Parse(header.Groups[1].Value);
            var bodyLines = new List<string>();
            var j = i + 1;
            while (j < lines.Count && !lines[j].StartsWith("+CMGL:", StringComparison.OrdinalIgnoreCase)
                   && !lines[j].Trim().Equals("OK", StringComparison.OrdinalIgnoreCase))
            {
                bodyLines.Add(lines[j].Trim());
                j++;
            }
            // Décodé ligne par ligne avant d'être recollé : un corps en UCS2 hexadécimal réparti sur plusieurs lignes
            // ne serait plus reconnu comme de l'hexadécimal une fois l'espace de jonction inséré au milieu.
            if (bodyLines.Count > 0) all.Add((index, string.Join(" ", bodyLines.Select(DecodeIfUcs2Hex))));
        }
        return all;
    }
}

/// <summary>
/// Orchestre l'envoi du crédit d'un bon de livraison : chaque ligne de crédit virtuel déclenche un transfert USSD en
/// deux échanges (le code, puis le chiffre de confirmation) vers le port COM configuré pour l'opérateur du produit,
/// avec le numéro du destinataire et le montant de la ligne. Le succès est détecté par un mot-clé dans le message
/// final — celui de la session USSD, ou celui d'un SMS séparé si l'opérateur confirme par SMS (ex. Mobilis). Un
/// produit physique n'a rien à envoyer.
/// <para>
/// Protections contre le double envoi : une seule opération à la fois par modem (verrou par port, de l'envoi du code
/// jusqu'au SMS de confirmation), statut enregistré après chaque ligne, et statut « incertain » dès que la
/// confirmation est partie sans résultat clair : une ligne incertaine n'est jamais renvoyée sans demande explicite.
/// </para>
/// </summary>
public class CreditTransferService
{
    public static readonly TimeSpan UssdTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan SmsTimeout = TimeSpan.FromSeconds(45);

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IModemPort _modem;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _portLocks = new(StringComparer.OrdinalIgnoreCase);
    private int _activeSends;

    public CreditTransferService(IDbContextFactory<AppDbContext> factory, IModemPort modem)
    {
        _factory = factory;
        _modem = modem;
    }

    /// <summary>Vrai tant qu'un envoi de crédit est en cours (pour griser les boutons qui en lanceraient un autre).</summary>
    public bool IsSending => Volatile.Read(ref _activeSends) > 0;

    /// <summary>Déclenché quand <see cref="IsSending"/> change.</summary>
    public event EventHandler? SendingChanged;

    /// <summary>Une tentative d'envoi pour une ligne du bon (pour le récapitulatif affiché à l'utilisateur).</summary>
    public record LineResult(string ProductName, string? RecipientPhone, UssdSendStatus Status, string Message)
    {
        public bool Success => Status == UssdSendStatus.Sent;
    }

    /// <summary>Résultat d'une consultation de solde pour un opérateur (« Tester connexion » et Rapport transactions).</summary>
    public record BalanceResult(string OperatorName, bool Success, string Message);

    /// <summary>État de la liaison avec le modem d'un opérateur (test au démarrage et pastilles de la fenêtre principale).</summary>
    public record ConnectionResult(string OperatorName, string ColorHex, bool Online, string Message);

    /// <summary>
    /// Vérifie la liaison avec le modem de chaque opérateur, sans rien envoyer à l'opérateur. Chaque opérateur a son
    /// propre port COM, donc les vérifications se font en parallèle (chacune attend qu'un envoi en cours sur son
    /// port soit fini).
    /// </summary>
    public async Task<List<ConnectionResult>> CheckConnectionsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var operators = await db.Operators.OrderBy(o => o.Id).ToListAsync();

        var checks = operators.Select(async op =>
        {
            if (string.IsNullOrWhiteSpace(op.ComPort))
                return new ConnectionResult(op.Name, op.ColorHex, false, "Port COM non configuré (page Paramètres).");
            var check = await OnPortAsync(op.ComPort, () => Task.Run(() => _modem.CheckLink(op.ComPort)));
            return new ConnectionResult(op.Name, op.ColorHex, check.Online, check.Message);
        });
        return (await Task.WhenAll(checks)).ToList();
    }

    /// <summary>
    /// Interroge le solde de crédit disponible sur la puce de chaque opérateur configuré, en envoyant son code USSD
    /// de consultation (une seule requête, sans confirmation). Sert à la fois à tester la connexion au modem et à
    /// afficher le solde des puces.
    /// </summary>
    public async Task<List<BalanceResult>> CheckBalancesAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var operators = await db.Operators.ToListAsync();

        var results = new List<BalanceResult>();
        foreach (var op in operators)
        {
            if (string.IsNullOrWhiteSpace(op.ComPort) || string.IsNullOrWhiteSpace(op.BalanceUssdCode))
            {
                results.Add(new BalanceResult(op.Name, false, "Port COM ou code de solde non configuré (page Paramètres)."));
                continue;
            }
            var (success, message) = await OnPortAsync(op.ComPort, async () =>
            {
                var existingSms = op.ConfirmationViaSms ? await Task.Run(() => _modem.ListSmsIndexes(op.ComPort)) : new HashSet<int>();
                var reply = await Task.Run(() => _modem.SendUssd(op.ComPort, op.BalanceUssdCode, UssdTimeout));
                if (reply == null)
                {
                    // SendUssd renvoie null pour toute erreur (port occupé, modem débranché, pas de réponse...) sans
                    // distinguer laquelle. CheckLink ne coûte rien à l'opérateur et donne un message plus précis.
                    var check = await Task.Run(() => _modem.CheckLink(op.ComPort));
                    return (false, check.Online ? "Aucune réponse à la requête de solde (la liaison de base fonctionne)." : check.Message);
                }

                try
                {
                    // Mobilis ne renvoie pas le solde dans la réponse USSD elle-même, mais par SMS séparé (parfois coupé en plusieurs messages).
                    if (op.ConfirmationViaSms)
                    {
                        var sms = await Task.Run(() => _modem.WaitForSmsBatch(op.ComPort, existingSms, SmsTimeout));
                        var text = sms != null ? FormatBalance(op, sms) : reply;
                        var usage = await Task.Run(() => _modem.GetSmsStorageUsage(op.ComPort));
                        return (true, AppendStorageWarning(text, usage));
                    }
                    return (true, FormatBalance(op, reply));
                }
                finally
                {
                    // Une requête de solde n'attend pas de confirmation de notre part : si la réponse se termine par
                    // un menu (ex. Djezzy, « 1:Plus de detail Bonus »), la session USSD reste ouverte côté réseau tant
                    // qu'elle n'est pas fermée explicitement, et la requête suivante peut être refusée (« max number
                    // of menu retries »).
                    await Task.Run(() => _modem.CancelUssd(op.ComPort));
                }
            });
            results.Add(new BalanceResult(op.Name, success, message));
        }
        return results;
    }

    /// <summary>
    /// Envoie le crédit de chaque ligne de crédit virtuel du bon qui n'a pas encore été tentée ou qui a échoué. Les
    /// lignes envoyées ne sont jamais renvoyées ; les lignes incertaines (le crédit est peut-être déjà parti) ne le
    /// sont que si <paramref name="resendUncertain"/> est vrai, après vérification par l'utilisateur.
    /// </summary>
    public async Task<List<LineResult>> SendPendingAsync(int deliveryNoteId, bool resendUncertain = false)
    {
        SetSending(+1);
        try
        {
            await using var db = await _factory.CreateDbContextAsync();
            var note = await db.DeliveryNotes.Include(n => n.Lines).ThenInclude(l => l.Product)
                .FirstOrDefaultAsync(n => n.Id == deliveryNoteId) ?? throw new BusinessException("Ce bon n'existe plus.");
            var operators = await db.Operators.ToListAsync();

            var toSend = note.Lines.Where(l => l.Product?.Kind == ProductKind.VirtualCredit && l.UssdStatus switch
            {
                null or UssdSendStatus.Failed => true,
                UssdSendStatus.Uncertain => resendUncertain,
                _ => false,
            }).ToList();

            // Les lignes d'un même bon partent l'une après l'autre, jamais en même temps.
            var results = new List<LineResult>();
            foreach (var line in toSend)
            {
                var (status, message) = await SendLineAsync(line, operators, db);
                line.UssdStatus = status;
                line.UssdMessage = message;
                await db.SaveChangesAsync(); // après chaque ligne : un plantage ne fait pas oublier ce qui est déjà parti
                results.Add(new LineResult(line.Product?.Name ?? "", line.RecipientPhone, status, message));
            }
            return results;
        }
        finally
        {
            SetSending(-1);
        }
    }

    /// <summary>
    /// Marque comme envoyées les lignes incertaines du bon, une fois que l'utilisateur a vérifié (solde ou historique
    /// de la puce) que le crédit est bien parti. Renvoie le nombre de lignes marquées.
    /// </summary>
    public async Task<int> MarkUncertainAsSentAsync(int deliveryNoteId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var lines = await db.DeliveryLines
            .Where(l => l.DeliveryNoteId == deliveryNoteId && l.UssdStatus == UssdSendStatus.Uncertain).ToListAsync();
        foreach (var line in lines)
        {
            line.UssdStatus = UssdSendStatus.Sent;
            line.UssdMessage = $"Marqué comme envoyé après vérification le {DateTime.Now:dd/MM/yyyy HH:mm}. {line.UssdMessage}";
        }
        await db.SaveChangesAsync();
        return lines.Count;
    }

    private async Task<(UssdSendStatus Status, string Message)> SendLineAsync(DeliveryLine line, List<Operator> operators, AppDbContext db)
    {
        var operatorName = ProductOperators.OperatorNameFor(line.Product?.Name);
        var op = operatorName == null ? null : operators.FirstOrDefault(o => string.Equals(o.Name, operatorName, StringComparison.OrdinalIgnoreCase));

        if (op == null)
            return (UssdSendStatus.Failed, $"Aucun opérateur connu pour « {line.Product?.Name} ».");
        if (string.IsNullOrWhiteSpace(op.ComPort) || string.IsNullOrWhiteSpace(op.UssdTemplate))
            return (UssdSendStatus.Failed, $"Port COM ou requête USSD non configuré pour {op.Name} (page Paramètres).");
        if (string.IsNullOrWhiteSpace(line.RecipientPhone))
            return (UssdSendStatus.Failed, "Aucun numéro de destinataire sur cette ligne.");
        if (string.IsNullOrWhiteSpace(op.SuccessKeyword))
            return (UssdSendStatus.Failed, $"Mot-clé de succès non configuré pour {op.Name} (page Paramètres).");

        var port = op.ComPort;
        var phone = line.RecipientPhone;
        var keyword = op.SuccessKeyword;

        // Tout l'échange (code, confirmation, SMS) se fait sous le verrou du port : rien d'autre ne peut s'intercaler
        // dans la session USSD, ni un autre bon, ni un test de connexion.
        return await OnPortAsync(port, async () =>
        {
            // SMS déjà sur la puce avant l'envoi : ils ne peuvent pas être la confirmation de ce transfert.
            var existingSms = op.ConfirmationViaSms ? await Task.Run(() => _modem.ListSmsIndexes(port)) : new HashSet<int>();

            // 1er échange : le code envoyé (ex. *760*num*montant*2008#) déclenche une invite de confirmation.
            var code = op.UssdTemplate.Replace("{numero}", phone).Replace("{montant}", line.Quantity.ToString("0.##"));
            var prompt = await Task.Run(() => _modem.SendUssd(port, code, UssdTimeout));
            if (prompt == null)
                return (UssdSendStatus.Failed, "Aucune invite de confirmation reçue du modem après l'envoi du code (rien n'a été confirmé).");

            // À partir d'ici le transfert peut avoir lieu : la ligne est enregistrée comme incertaine avant d'envoyer
            // la confirmation, pour qu'un plantage pendant l'échange ne la laisse pas renvoyer automatiquement.
            line.UssdStatus = UssdSendStatus.Uncertain;
            line.UssdMessage = $"Confirmation envoyée, résultat non reçu. Invite : « {prompt} »";
            await db.SaveChangesAsync();

            // 2ème échange : le chiffre de confirmation (ex. "1") déclenche le message final.
            var confirm = string.IsNullOrWhiteSpace(op.ConfirmKeystroke) ? "1" : op.ConfirmKeystroke;
            var result = await Task.Run(() => _modem.SendUssd(port, confirm, UssdTimeout));
            if (result == null)
                return (UssdSendStatus.Uncertain,
                    $"Aucune réponse à la confirmation : le crédit a peut-être été transféré. Invite reçue : « {prompt} ».");

            if (op.ConfirmationViaSms)
            {
                // Seul un nouveau SMS qui cite le numéro de cette ligne compte : un SMS en retard d'une ligne
                // précédente (autre numéro) n'est pas pris pour la confirmation de celle-ci.
                var sms = await Task.Run(() => _modem.WaitForSms(port, existingSms, body => MentionsNumber(body, phone), SmsTimeout));
                if (sms == null)
                    return (UssdSendStatus.Uncertain,
                        $"Aucun SMS de confirmation citant le {phone} reçu : le crédit a peut-être été transféré. Réponse USSD : « {result} ».");
                return (Contains(sms, keyword) ? UssdSendStatus.Sent : UssdSendStatus.Failed, sms);
            }

            return (Contains(result, keyword) ? UssdSendStatus.Sent : UssdSendStatus.Failed, result);
        });
    }

    /// <summary>Exécute une opération sur un modem en attendant que toute autre opération sur le même port soit finie.</summary>
    private async Task<T> OnPortAsync<T>(string comPort, Func<Task<T>> work)
    {
        var gate = _portLocks.GetOrAdd(comPort.Trim(), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            return await work();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Mobilis renvoie en une seule réponse le solde des 4 « distributeurs » du compte (Poste, Assilou, Data, GTS),
    /// sous la forme « Poste est : 10.00 DZD . Assilou est : ... ». On n'affiche que celui utilisé pour les
    /// transferts de cet opérateur : le chiffre juste après {numero} dans sa requête USSD (ex. *630*{numero}*03*...
    /// envoie depuis le distributeur 3, Data). Les opérateurs sans ce découpage affichent la réponse telle quelle.
    /// </summary>
    private static readonly Dictionary<int, string> MobilisDistributeurs = new()
    {
        [1] = "Poste",
        [2] = "Assilou",
        [3] = "Data",
        [4] = "GTS",
    };

    private static string FormatBalance(Operator op, string reply)
    {
        var distributeur = ExtractDistributeurNumber(op.UssdTemplate);
        if (distributeur is { } d && ExtractDistributeurBalance(reply, d) is { } distBalance)
            return $"{distBalance} DZD";

        // Ex. Djezzy : "VOTRE SOLDE EST 6523.35 DA. VOTRE ANCIEN CREDIT EST ... 1:Plus de detail Bonus" → juste "6523.35 DA".
        if (ExtractSoldeEst(reply) is { } solde) return solde;

        // Ex. Ooredoo : "Votre credit Storm-Credit est 10447 Dinar Fidélité:50DA." → juste "10447".
        if (ExtractStormCredit(reply) is { } storm) return storm;

        return reply;
    }

    /// <summary>Extrait le nombre (et son unité) juste après « VOTRE SOLDE EST », sans le reste du message.</summary>
    internal static string? ExtractSoldeEst(string text)
    {
        var match = Regex.Match(text, @"VOTRE\s+SOLDE\s+EST\s*:?\s*([\d]+(?:[.,]\d+)?\s*[A-Za-z]*)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>Extrait seulement le nombre juste après « Votre credit Storm-Credit est » (Ooredoo), sans le reste du message.</summary>
    internal static string? ExtractStormCredit(string text)
    {
        var match = Regex.Match(text, @"VOTRE\s+CREDIT\s+STORM-CREDIT\s+EST\s*:?\s*([\d]+(?:[.,]\d+)?)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Replace(',', '.') : null;
    }

    /// <summary>À partir de ce taux d'occupation, on avertit que la mémoire SMS risque de bloquer l'arrivée de nouveaux messages.</summary>
    private const double StorageWarningThreshold = 0.8;

    /// <summary>
    /// Ajoute un avertissement si la mémoire SMS de la SIM est presque pleine : au-delà, le réseau ne peut plus
    /// livrer de nouveau SMS (ex. le solde ou la confirmation Mobilis), sans message d'erreur visible ailleurs que
    /// sur le modem lui-même.
    /// </summary>
    internal static string AppendStorageWarning(string message, (int Used, int Total)? storage)
    {
        if (storage is not { } s || s.Total == 0 || (double)s.Used / s.Total < StorageWarningThreshold) return message;
        return $"{message} Attention : mémoire SMS de la SIM presque pleine ({s.Used}/{s.Total}) — les nouveaux SMS risquent de ne pas arriver tant qu'elle n'est pas libérée (AT+CMGD=1,4 pour tout effacer).";
    }

    /// <summary>Le chiffre juste après {numero} dans une requête USSD (ex. 3 dans *630*{numero}*03*{montant}*00000#).</summary>
    internal static int? ExtractDistributeurNumber(string? ussdTemplate)
    {
        if (string.IsNullOrWhiteSpace(ussdTemplate)) return null;
        const string marker = "{numero}";
        var index = ussdTemplate.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var match = Regex.Match(ussdTemplate[(index + marker.Length)..], @"^\*(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    /// <summary>Extrait la valeur qui suit le nom du distributeur (ex. « Data est : 155.00 DZD »), ou null si absente.</summary>
    internal static string? ExtractDistributeurBalance(string text, int distributeur)
    {
        if (!MobilisDistributeurs.TryGetValue(distributeur, out var label)) return null;
        var match = Regex.Match(text, $@"{Regex.Escape(label)}\s*est\s*:?\s*([\d]+(?:[.,]\d+)?)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Replace(',', '.') : null;
    }

    private void SetSending(int delta)
    {
        Interlocked.Add(ref _activeSends, delta);
        SendingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Vrai si le texte cite le numéro, quel que soit son format (0778123456, 213778123456, 778 12 34 56…) : on
    /// compare les 9 derniers chiffres, sans le 0 ou l'indicatif du début.
    /// </summary>
    internal static bool MentionsNumber(string text, string phone)
    {
        var phoneDigits = new string(phone.Where(char.IsDigit).ToArray());
        if (phoneDigits.Length == 0) return false;
        var significant = phoneDigits.Length > 9 ? phoneDigits[^9..] : phoneDigits;
        return new string(text.Where(char.IsDigit).ToArray()).Contains(significant);
    }

    private static bool Contains(string message, string keyword) => message.Contains(keyword, StringComparison.OrdinalIgnoreCase);
}
