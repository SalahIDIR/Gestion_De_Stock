using System.IO;
using System.Text.Json;

namespace GestionStock.Puces.Settings;

public record OperatorPorts(string? Mobilis, string? Djezzy, string? Ooredoo);

public static class PortSettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GestionStockPuces", "ports.json");

    public static OperatorPorts Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new OperatorPorts(null, null, null);
            return JsonSerializer.Deserialize<OperatorPorts>(File.ReadAllText(FilePath))
                   ?? new OperatorPorts(null, null, null);
        }
        catch (JsonException)
        {
            return new OperatorPorts(null, null, null);
        }
    }

    public static void Save(OperatorPorts ports)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(ports));
    }
}
