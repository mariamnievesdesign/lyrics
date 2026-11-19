using System.IO;
using System.Text.Json;

namespace Letras;

/// <summary>Recuerda posición, tamaño, ajuste de sincronía y tamaño de letra entre sesiones.</summary>
public sealed class Settings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 420;
    public double Height { get; set; } = 260;
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
