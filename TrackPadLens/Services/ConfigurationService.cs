using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TrackPadLens.Models;

namespace TrackPadLens.Services;

/// <summary>
/// Lightweight JSON settings persistence using System.Text.Json with atomic file replacement.
/// </summary>
public sealed class ConfigurationService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string ConfigFilePath { get; }

    public ConfigurationService(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            ConfigFilePath = customPath;
        }
        else
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            ConfigFilePath = Path.Combine(appData, "TrackPadLens", "config.json");
        }
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
            {
                var defaults = new AppSettings();
                defaults.Sanitize();
                return defaults;
            }

            string json = File.ReadAllText(ConfigFilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
            settings.Sanitize();
            return settings;
        }
        catch
        {
            var fallback = new AppSettings();
            fallback.Sanitize();
            return fallback;
        }
    }

    public void Save(AppSettings settings)
    {
        settings.Sanitize();
        string? dir = Path.GetDirectoryName(ConfigFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = ConfigFilePath + ".tmp";
        string json = JsonSerializer.Serialize(settings, SerializerOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, ConfigFilePath, overwrite: true);
    }
}
