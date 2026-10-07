using System.IO;
using System.Text.Json;

namespace GestionStock.Puces.Archive;

/// <summary>Une copie locale d'un SMS lu une fois sur la SIM, pour le garder même si on vide la SIM plus tard.</summary>
public record ArchivedSms(string Operator, string Phone, DateTime? Date, string Body, bool IsOutgoing);

/// <summary>
/// Copie locale, sur ce PC, de tous les SMS déjà lus sur les SIM des opérateurs. La SIM a une capacité limitée (25
/// messages sur cette carte) : au-delà, il faut la vider, ce qui efface les messages qui s'y trouvent. Cette archive
/// permet de les retrouver quand même, puisqu'ils ont déjà été copiés ici au moment de leur lecture.
/// </summary>
public static class SmsArchiveStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GestionStockPuces", "sms-archive.json");

    public static List<ArchivedSms> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<ArchivedSms>();
            return JsonSerializer.Deserialize<List<ArchivedSms>>(File.ReadAllText(FilePath)) ?? new List<ArchivedSms>();
        }
        catch (JsonException)
        {
            return new List<ArchivedSms>();
        }
    }

    /// <summary>Ajoute les messages absents de l'archive (comparés par opérateur, numéro, date et texte), et renvoie l'archive complète à jour.</summary>
    public static List<ArchivedSms> Append(IEnumerable<ArchivedSms> freshlyRead)
    {
        var all = Load();
        var known = new HashSet<ArchivedSms>(all);
        foreach (var sms in freshlyRead)
            if (known.Add(sms)) all.Add(sms);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(all));
        return all;
    }
}
