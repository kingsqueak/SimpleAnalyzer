using System.Text.Json;

namespace SimpleAnalyzer;

/// <summary>The few things kept between runs, in Application Support (macOS),
/// ~/.local/share (Linux) or %LOCALAPPDATA% (Windows).</summary>
public sealed class Settings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 40001;
    public int TimeBaseIndex { get; set; } = 2;
    /// <summary>For a server whose IQ is mirrored (SimpleHPSDR's engine at the time of writing).</summary>
    public bool SwapIq { get; set; }

    private static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimpleAnalyzer");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
