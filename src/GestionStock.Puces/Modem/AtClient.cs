using System.IO.Ports;

namespace GestionStock.Puces.Modem;

/// <summary>Dialogue AT avec un modem sur un port COM. Un seul programme peut ouvrir le port à la fois.</summary>
public sealed class AtClient : IDisposable
{
    private readonly SerialPort _port;

    public AtClient(string comPort)
    {
        _port = new SerialPort(comPort, 115200, Parity.None, 8, StopBits.One)
        {
            NewLine = "\r",
            ReadTimeout = 1000,
            WriteTimeout = 3000,
            DtrEnable = true,
            RtsEnable = true,
        };
        _port.Open();
        _port.DiscardInBuffer();
    }

    /// <summary>Envoie une commande et renvoie ses lignes de réponse, jusqu'à OK ou ERROR.</summary>
    public List<string> Command(string command, TimeSpan timeout)
    {
        _port.DiscardInBuffer();
        _port.WriteLine(command);
        var lines = new List<string>();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var line = ReadLine();
            if (string.IsNullOrEmpty(line) || line == command) continue;
            lines.Add(line);
            if (line == "OK" || line.StartsWith("ERROR") || line.StartsWith("+CME ERROR") || line.StartsWith("+CMS ERROR"))
                break;
        }
        return lines;
    }

    /// <summary>Envoie une requête USSD et attend la réponse « +CUSD: » (qui arrive après le OK).</summary>
    public (int Status, string Text)? Ussd(string code, TimeSpan timeout)
    {
        // Si la réponse arrive par SMS séparé (ex. solde Mobilis), il doit atterrir sur la SIM, pas la mémoire de la
        // clé, sinon "Lire les SMS reçus" (qui ne regarde que la SIM) ne le verra jamais. Trop tard pour le faire
        // après coup : il faut que ce soit déjà réglé avant l'arrivée du SMS.
        Command("AT+CPMS=\"SM\",\"SM\",\"SM\"", TimeSpan.FromSeconds(5));
        _port.DiscardInBuffer();
        _port.WriteLine($"AT+CUSD=1,\"{code}\",15");
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var line = ReadLine();
            if (line == null) continue;
            if (line.StartsWith("+CUSD:", StringComparison.OrdinalIgnoreCase)) return AtParser.ParseCusd(line);
            if (line.StartsWith("+CME ERROR") || line.StartsWith("ERROR")) return null;
        }
        return null;
    }

    /// <summary>
    /// Ferme explicitement une session USSD restée ouverte (ex. une réponse qui se termine par un menu « 1:... »),
    /// pour que la requête suivante ne soit pas refusée par le réseau (« max number of menu retries »).
    /// </summary>
    public void CancelUssd() => Command("AT+CUSD=2", TimeSpan.FromSeconds(5));

    /// <summary>Lit tous les SMS stockés sur la carte SIM (et non dans la mémoire interne de la clé).</summary>
    public List<SmsMessage> ReadSms()
    {
        Command("AT+CMGF=1", TimeSpan.FromSeconds(5));
        Command("AT+CPMS=\"SM\",\"SM\",\"SM\"", TimeSpan.FromSeconds(5));
        var lines = Command("AT+CMGL=\"ALL\"", TimeSpan.FromSeconds(20));
        return AtParser.ParseCmgl(lines);
    }

    /// <summary>Occupation du stockage SMS actif (ex. 25/25 = plein, aucun nouveau SMS ne peut plus arriver).</summary>
    public (int Used, int Total)? GetSimStorageUsage()
    {
        var lines = Command("AT+CPMS?", TimeSpan.FromSeconds(5));
        return AtParser.ParseStorageUsage(lines);
    }

    private string? ReadLine()
    {
        try
        {
            return _port.ReadLine().Trim();
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    public void Dispose() => _port.Dispose();
}
