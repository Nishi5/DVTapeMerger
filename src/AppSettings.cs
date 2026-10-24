using System.Text.Json;

namespace DVTapeMerger;

public sealed class AppSettings
{
    public string DvrescuePath { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    public string WorkDir { get; set; } = "";
    public string LastOutput { get; set; } = "";
    public int OutputFormat { get; set; }
    public bool SaveLogs { get; set; } = true;

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DVTapeMerger", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
