using System.IO;
using System.Text.Json;

namespace ImageClipSaver;

/// <summary>
/// Small JSON settings file in %LocalAppData%\ImageClipSaver.
/// </summary>
internal sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ImageClipSaver",
        "settings.json");

    // Where the app kept its settings before it was renamed from ImageClipToFile
    private static readonly string LegacyFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ImageClipToFile",
        "settings.json");

    public string SaveDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "ImageClips");

    public bool Capture { get; set; } = true;
    public bool ReviewBeforeSaving { get; set; }
    public bool AlwaysOnTop { get; set; }

    // Window placement; null means "let Windows decide"
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 760;
    public double Height { get; set; } = 560;

    public static AppSettings Load()
    {
        try
        {
            var path = File.Exists(FilePath) ? FilePath : LegacyFilePath;
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings: fall back to defaults
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth bothering the user about
        }
    }
}
