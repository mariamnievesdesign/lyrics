using System.IO;
using System.Text.Json;

namespace Letras;

/// <summary>Recuerda posición, tamaño, sincronía, tamaño de letra y transparencia entre sesiones.</summary>
public sealed class Settings
{
    // Posición y tamaño en píxeles reales de pantalla.
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? W { get; set; }
    public int? H { get; set; }

    public double OffsetSeconds { get; set; }
    public double FontSize { get; set; } = 22;
    public int GlassLevel { get; set; } = 1;

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Letras", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
