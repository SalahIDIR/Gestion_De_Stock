using System.Collections.Concurrent;
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
    /// Vérifie, sans rien envoyer à l'opérateur (ni USSD ni SMS, donc gratuit), que le modem répond, que la puce est
    /// prête et qu'elle est inscrite sur le réseau.
    /// </summary>
    ModemCheck CheckLink(string comPort);
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
                return null;

            port.WriteLine($"AT+CUSD=1,\"{ussdCode}\",15");
            if (!ReadUntil(port, timeout, "+CUSD:", "ERROR", out var reply) || !Contains(reply, "+CUSD:"))
                return null;
            return ExtractQuoted(reply, "+CUSD:");
        }
        catch (Exception)
        {
            return null;
        }
    }

    public IReadOnlySet<int> ListSmsIndexes(string comPort)
    {
        try
        {
            using var port = Open(comPort);
            port.WriteLine("AT+CMGF=1"); // mode texte (lisible), plutôt que le mode PDU par défaut
            ReadUntil(port, AtTimeout, "OK", "ERROR", out _);
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

    /// <summary>
    /// Vrai si une réponse à « AT+CREG? » ou « AT+CEREG? » indique une inscription sur le réseau : état 1 (réseau
    /// de l'opérateur) ou 5 (itinérance), ex. « +CREG: 0,1 ».
    /// </summary>
    internal static bool IsRegistered(string reply)
    {
        var match = Regex.Match(reply, @"\+CE?REG:\s*(?:\d+,)?(\d+)", RegexOptions.IgnoreCase);
        return match.Success && match.Groups[1].Value is "1" or "5";
    }

    private static SerialPort Open(string comPort)
    {
        var port = new SerialPort(comPort, BaudRate) { NewLine = "\r\n", ReadTimeout = 2000, WriteTimeout = 5000 };
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

    /// <summary>Extrait le texte entre le premier couple de guillemets suivant un marqueur (ex. le message d'un « +CUSD: 1,"…",15 »).</summary>
    internal static string? ExtractQuoted(string text, string afterMarker)
    {
        var markerIndex = text.IndexOf(afterMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0) return null;
        var match = Regex.Match(text[(markerIndex + afterMarker.Length)..], "\"(.*?)\"");
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
            var body = new StringBuilder();
            var j = i + 1;
            while (j < lines.Count && !lines[j].StartsWith("+CMGL:", StringComparison.OrdinalIgnoreCase)
                   && !lines[j].Trim().Equals("OK", StringComparison.OrdinalIgnoreCase))
            {
                if (body.Length > 0) body.Append(' ');
                body.Append(lines[j].Trim());
                j++;
            }
            if (body.Length > 0) all.Add((index, body.ToString()));
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
            var reply = await OnPortAsync(op.ComPort, () => Task.Run(() => _modem.SendUssd(op.ComPort, op.BalanceUssdCode, UssdTimeout)));
            results.Add(new BalanceResult(op.Name, reply != null, reply ?? "Aucune réponse du modem."));
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
