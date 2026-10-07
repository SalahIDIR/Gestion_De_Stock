using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GestionStock.Puces.Modem;

public static class AtParser
{
    private static readonly Regex CusdRegex = new(@"\+CUSD:\s*(\d)(?:,""([^""]*)"")?", RegexOptions.IgnoreCase);
    private static readonly Regex CmglHeader = new(@"^\+CMGL:\s*(\d+),""([^""]*)"",""([^""]*)"",[^,]*,""([^""]*)""", RegexOptions.IgnoreCase);
    private static readonly Regex CpmsRegex = new(@"\+CPMS:\s*""[^""]*"",(\d+),(\d+)", RegexOptions.IgnoreCase);

    /// <summary>Occupation du stockage SMS actif à partir d'une réponse à « AT+CPMS? » (ex. 25 messages utilisés sur 25).</summary>
    public static (int Used, int Total)? ParseStorageUsage(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var match = CpmsRegex.Match(line);
            if (match.Success) return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
        }
        return null;
    }

    /// <summary>Statut 1 = session ouverte (une réponse est attendue), 2 = session fermée.</summary>
    public static (int Status, string Text)? ParseCusd(string line)
    {
        var match = CusdRegex.Match(line);
        if (!match.Success) return null;
        var text = match.Groups[2].Success ? DecodeIfUcs2Hex(match.Groups[2].Value) : "";
        return (int.Parse(match.Groups[1].Value), text);
    }

    public static List<SmsMessage> ParseCmgl(IReadOnlyList<string> lines)
    {
        var messages = new List<SmsMessage>();
        Match? header = null;
        var body = new List<string>();

        void Flush()
        {
            if (header == null) return;
            var status = header.Groups[2].Value;
            messages.Add(new SmsMessage(
                Index: int.Parse(header.Groups[1].Value),
                Phone: DecodeIfUcs2Hex(header.Groups[3].Value),
                Date: ParseDate(header.Groups[4].Value),
                Body: string.Join("\n", body.Select(DecodeIfUcs2Hex)),
                IsOutgoing: status.Contains("SENT", StringComparison.OrdinalIgnoreCase)));
            header = null;
            body.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line == "OK" || line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) break;

            var match = CmglHeader.Match(line);
            if (match.Success)
            {
                Flush();
                header = match;
            }
            else if (header != null && line.Length > 0)
            {
                body.Add(line);
            }
        }
        Flush();
        return messages;
    }

    /// <summary>
    /// Le modem renvoie parfois le texte (et le numéro) en UCS2 hexadécimal, ex. "0041004200430044" pour "ABCD".
    /// Seules les chaînes assez longues, de longueur multiple de 4 et uniquement hexadécimales sont décodées, pour ne
    /// pas toucher un numéro ou un texte court qui ressemble par hasard à de l'hexadécimal.
    /// </summary>
    public static string DecodeIfUcs2Hex(string text)
    {
        if (text.Length < 8 || text.Length % 4 != 0 || !text.All(Uri.IsHexDigit)) return text;
        return Encoding.BigEndianUnicode.GetString(Convert.FromHexString(text));
    }

    private static DateTime? ParseDate(string scts)
    {
        var parts = scts.Split(',', 2);
        if (parts.Length < 2) return null;
        var timePart = Regex.Match(parts[1], @"^\d{2}:\d{2}:\d{2}").Value;
        var value = $"{parts[0]},{timePart}";
        return DateTime.TryParseExact(value, "yy/MM/dd,HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }
}
