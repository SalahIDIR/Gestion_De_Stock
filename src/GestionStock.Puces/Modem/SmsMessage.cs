namespace GestionStock.Puces.Modem;

public record SmsMessage(int Index, string Phone, DateTime? Date, string Body, bool IsOutgoing)
{
    public string DirectionLabel => IsOutgoing ? "Envoyé" : "Reçu";
}
