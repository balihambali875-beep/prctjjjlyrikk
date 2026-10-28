using System;
using System.IO;
using System.Text.Json;

namespace LyricPop;

/// <summary>Pengaturan tersimpan di %APPDATA%\LyricPop\settings.json.</summary>
public sealed class AppSettings
{
    public int FontSize { get; set; } = 18;
    public int Opacity { get; set; } = 85;
    public string Theme { get; set; } = "artwork"; // dark | light | artwork
    public double OffsetSec { get; set; } = 0;
    public bool AlwaysOnTop { get; set; } = true;
    public double? Left { get; set; }
    public double? Top { get; set; }

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LyricPop");

    private static string FilePath => Path.Combine(DataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception) { /* pakai default */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { /* abaikan */ }
    }
}
