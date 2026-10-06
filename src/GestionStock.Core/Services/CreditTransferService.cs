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

    /// <summary>Attend un SMS reçu sur ce port dans le délai donné, renvoie son texte puis le supprime du modem ; null si rien n'arrive.</summary>
    string? WaitForSms(string comPort, TimeSpan timeout);
}

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

    public string? WaitForSms(string comPort, TimeSpan timeout)
    {
        try
        {
            using var port = Open(comPort);
            port.WriteLine("AT+CMGF=1"); // mode texte (lisible), plutôt que le mode PDU par défaut
            ReadUntil(port, AtTimeout, "OK", "ERROR", out _);

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                port.WriteLine("AT+CMGL=\"ALL\"");
                if (ReadUntil(port, AtTimeout, "OK", "ERROR", out var reply))
                {
                    var sms = ParseLastSms(reply);
                    if (sms != null)
                    {
                        port.WriteLine($"AT+CMGD={sms.Value.Index}"); // supprimé pour ne pas être relu au prochain appel
                        ReadUntil(port, AtTimeout, "OK", "ERROR", out _);
                        return sms.Value.Body;
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

    /// <summary>
    /// Extrait le SMS le plus récent d'une réponse à « AT+CMGL="ALL" » : une ligne d'en-tête « +CMGL: index,... »
    /// suivie du corps du message sur la ou les lignes suivantes, jusqu'au prochain « +CMGL: » ou à la fin.
    /// </summary>
    internal static (int Index, string Body)? ParseLastSms(string listing)
    {
        var lines = listing.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        (int Index, string Body)? last = null;
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
            if (body.Length > 0) last = (index, body.ToString());
        }
        return last;
    }
}

/// <summary>
/// Orchestre l'envoi du crédit d'un bon de livraison : chaque ligne de crédit virtuel déclenche un transfert USSD en
/// deux échanges (le code, puis le chiffre de confirmation) vers le port COM configuré pour l'opérateur du produit,
/// avec le numéro du destinataire et le montant de la ligne. Le succès est détecté par un mot-clé dans le message
/// final — celui de la session USSD, ou celui d'un SMS séparé si l'opérateur confirme par SMS (ex. Mobilis). Un
/// produit physique n'a rien à envoyer. Le statut de chaque ligne est enregistré pour permettre de corriger et
/// renvoyer plus tard ce qui a échoué, sans jamais renvoyer ce qui est déjà marqué comme envoyé.
/// </summary>
public class CreditTransferService
{
    public static readonly TimeSpan UssdTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan SmsTimeout = TimeSpan.FromSeconds(45);

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IModemPort _modem;

    public CreditTransferService(IDbContextFactory<AppDbContext> factory, IModemPort modem)
    {
        _factory = factory;
        _modem = modem;
    }

    /// <summary>Une tentative d'envoi pour une ligne du bon (pour le récapitulatif affiché à l'utilisateur).</summary>
    public record LineResult(string ProductName, string? RecipientPhone, bool Success, string Message);

    /// <summary>Résultat d'une consultation de solde pour un opérateur (« Tester connexion » et Rapport transactions).</summary>
    public record BalanceResult(string OperatorName, bool Success, string Message);

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
            var reply = _modem.SendUssd(op.ComPort, op.BalanceUssdCode, UssdTimeout);
            results.Add(new BalanceResult(op.Name, reply != null, reply ?? "Aucune réponse du modem."));
        }
        return results;
    }

    /// <summary>
    /// Envoie le crédit de chaque ligne de crédit virtuel du bon qui n'est pas encore marquée comme envoyée
    /// (première tentative, ou nouvel essai après un échec).
    /// </summary>
    public async Task<List<LineResult>> SendPendingAsync(int deliveryNoteId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var note = await db.DeliveryNotes.Include(n => n.Lines).ThenInclude(l => l.Product)
            .FirstOrDefaultAsync(n => n.Id == deliveryNoteId) ?? throw new BusinessException("Ce bon n'existe plus.");
        var operators = await db.Operators.ToListAsync();

        var results = new List<LineResult>();
        foreach (var line in note.Lines.Where(l => l.Product?.Kind == ProductKind.VirtualCredit && l.UssdStatus != UssdSendStatus.Sent))
        {
            var (success, message) = SendLine(line, operators);
            line.UssdStatus = success ? UssdSendStatus.Sent : UssdSendStatus.Failed;
            line.UssdMessage = message;
            results.Add(new LineResult(line.Product?.Name ?? "", line.RecipientPhone, success, message));
        }

        await db.SaveChangesAsync();
        return results;
    }

    private (bool Success, string Message) SendLine(DeliveryLine line, List<Operator> operators)
    {
        var operatorName = ProductOperators.OperatorNameFor(line.Product?.Name);
        var op = operatorName == null ? null : operators.FirstOrDefault(o => string.Equals(o.Name, operatorName, StringComparison.OrdinalIgnoreCase));

        if (op == null)
            return (false, $"Aucun opérateur connu pour « {line.Product?.Name} ».");
        if (string.IsNullOrWhiteSpace(op.ComPort) || string.IsNullOrWhiteSpace(op.UssdTemplate))
            return (false, $"Port COM ou requête USSD non configuré pour {op.Name} (page Paramètres).");
        if (string.IsNullOrWhiteSpace(line.RecipientPhone))
            return (false, "Aucun numéro de destinataire sur cette ligne.");
        if (string.IsNullOrWhiteSpace(op.SuccessKeyword))
            return (false, $"Mot-clé de succès non configuré pour {op.Name} (page Paramètres).");

        // 1er échange : le code envoyé (ex. *760*num*montant*2008#) déclenche une invite de confirmation.
        var code = op.UssdTemplate.Replace("{numero}", line.RecipientPhone).Replace("{montant}", line.Quantity.ToString("0.##"));
        var prompt = _modem.SendUssd(op.ComPort, code, UssdTimeout);
        if (prompt == null)
            return (false, "Aucune invite de confirmation reçue du modem après l'envoi du code.");

        // 2ème échange : le chiffre de confirmation (ex. "1") déclenche le message final.
        var confirm = string.IsNullOrWhiteSpace(op.ConfirmKeystroke) ? "1" : op.ConfirmKeystroke;
        var result = _modem.SendUssd(op.ComPort, confirm, UssdTimeout);
        if (result == null)
            return (false, $"Aucune réponse à la confirmation. Invite reçue : « {prompt} ».");

        if (op.ConfirmationViaSms)
        {
            var sms = _modem.WaitForSms(op.ComPort, SmsTimeout);
            if (sms == null)
                return (false, $"Aucun SMS de confirmation reçu. Réponse USSD : « {result} ».");
            return (Contains(sms, op.SuccessKeyword), sms);
        }

        return (Contains(result, op.SuccessKeyword), result);
    }

    private static bool Contains(string message, string keyword) => message.Contains(keyword, StringComparison.OrdinalIgnoreCase);
}
