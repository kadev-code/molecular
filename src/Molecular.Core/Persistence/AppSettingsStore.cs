using System.Text.Json;
using Molecular.Core.Models;

namespace Molecular.Core.Persistence;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;

    public AppSettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Molecular",
            "settings.json");
    }

    public string FilePath => _filePath;

    public AppSettings Load()
    {
        if (!File.Exists(_filePath))
            return Normalize(new AppSettings());

        try
        {
            var json = File.ReadAllText(_filePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            return Normalize(settings);
        }
        catch
        {
            return Normalize(new AppSettings());
        }
    }

    public void Save(AppSettings settings)
    {
        var normalized = Normalize(settings);
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryFile = _filePath + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(normalized, JsonOptions));
        File.Move(temporaryFile, _filePath, true);
    }

    public static AppSettings Normalize(AppSettings settings)
    {
        settings.SchemaVersion = Math.Max(1, settings.SchemaVersion);
        settings.MeterIntervalMs = settings.MeterIntervalMs switch
        {
            <= 75 => 50,
            <= 150 => 100,
            <= 350 => 200,
            _ => 500
        };
        if (string.IsNullOrWhiteSpace(settings.PreferredOutputDeviceId))
            settings.PreferredOutputDeviceId = null;
        return settings;
    }

    public static IReadOnlyList<MeterIntervalOption> MeterIntervalOptions { get; } =
    [
        new(50, "20 Hz (50 ms)"),
        new(100, "10 Hz (100 ms) — padrão"),
        new(200, "5 Hz (200 ms)"),
        new(500, "2 Hz (500 ms)")
    ];
}
