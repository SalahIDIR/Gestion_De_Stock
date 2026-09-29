using System.IO.Ports;
using System.Text;
using GestionStock.Core.Data;
using GestionStock.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Core.Services;

/// <summary>Résultat d'une tentative d'envoi USSD à un modem.</summary>
public record UssdSendResult(bool Success, string Message);

/// <summary>
/// Dialogue avec un modem GSM par port série pour envoyer une requête USSD (transfert de crédit).
/// Abstraction pour permettre de tester <see cref="CreditTransferService"/> sans matériel réel.
/// </summary>
public interface IModemPort
{
    UssdSendResult SendUssd(string comPort, string ussdCode, TimeSpan timeout);
}

/// <summary>
/// Implémentation réelle par port série, avec les commandes AT standard (AT, puis AT+CUSD).
/// N'a pas pu être testée sur un modem physique : le débit (9600 bauds), le format exact des réponses et le délai
/// avant la confirmation « +CUSD: » de l'opérateur varient selon le modem et devront très probablement être ajustés
/// une fois essayés avec du vrai matériel.
/// </summary>
public class SerialModemPort : IModemPort
{
    private const int BaudRate = 9600;

    public UssdSendResult SendUssd(string comPort, string ussdCode, TimeSpan timeout)
    {
        try
        {
            using var port = new SerialPort(comPort, BaudRate) { NewLine = "\r\n", ReadTimeout = 5000, WriteTimeout = 5000 };
            port.Open();
            port.DiscardInBuffer();

            port.WriteLine("AT");
            if (!ReadUntil(port, TimeSpan.FromSeconds(5), "OK", "ERROR", out var atReply) || atReply.Contains("ERROR"))
                return new UssdSendResult(false, "Le modem ne répond pas (AT).");

            port.WriteLine($"AT+CUSD=1,\"{ussdCode}\",15");
            if (!ReadUntil(port, timeout, "+CUSD:", "ERROR", out var reply))
                return new UssdSendResult(false, "Pas de confirmation reçue de l'opérateur (délai dépassé).");
            if (reply.Contains("ERROR") && !reply.Contains("+CUSD:"))
                return new UssdSendResult(false, $"Le modem a refusé la requête : {reply.Trim()}");

            return new UssdSendResult(true, reply.Trim());
        }
        catch (Exception ex)
        {
            return new UssdSendResult(false, $"Erreur de communication avec le port {comPort} : {ex.Message}");
        }
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
                if (line.Contains(markerA, StringComparison.OrdinalIgnoreCase) || line.Contains(markerB, StringComparison.OrdinalIgnoreCase))
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
}

/// <summary>
/// Orchestre l'envoi du crédit d'un bon de livraison : chaque ligne de crédit virtuel déclenche un envoi USSD vers le
/// port COM configuré pour l'opérateur du produit, avec le numéro du destinataire et le montant de la ligne (un
/// produit physique n'a rien à envoyer). Le statut de chaque ligne est enregistré pour permettre de corriger et
/// renvoyer plus tard ce qui a échoué, sans jamais renvoyer ce qui est déjà marqué comme envoyé.
/// </summary>
public class CreditTransferService
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IModemPort _modem;

    public CreditTransferService(IDbContextFactory<AppDbContext> factory, IModemPort modem)
    {
        _factory = factory;
        _modem = modem;
    }

    /// <summary>Une tentative d'envoi pour une ligne du bon (pour le récapitulatif affiché à l'utilisateur).</summary>
    public record LineResult(string ProductName, string? RecipientPhone, bool Success, string Message);

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

        var code = op.UssdTemplate.Replace("{numero}", line.RecipientPhone).Replace("{montant}", line.Quantity.ToString("0.##"));
        var result = _modem.SendUssd(op.ComPort, code, DefaultTimeout);
        return (result.Success, result.Message);
    }
}
