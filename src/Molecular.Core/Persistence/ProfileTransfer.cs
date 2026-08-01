using System.Text.Json;
using Molecular.Core.Models;

namespace Molecular.Core.Persistence;

/// <summary>
/// Import/export of a single mixer profile without machine-specific paths or catalog state.
/// </summary>
public static class ProfileTransfer
{
    public const int TransferSchemaVersion = 1;
    public const string FileExtension = ".molecular-profile.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string ExportJson(MixerProfile profile) =>
        JsonSerializer.Serialize(ToTransferDocument(profile), JsonOptions);

    public static void ExportToFile(MixerProfile profile, string path) =>
        File.WriteAllText(path, ExportJson(profile));

    public static MixerProfile ImportFromFile(string path)
    {
        var json = File.ReadAllText(path);
        return ImportJson(json);
    }

    public static MixerProfile ImportJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        MixerProfile profile;
        if (document.RootElement.TryGetProperty("Profile", out var profileElement))
        {
            profile = JsonSerializer.Deserialize<MixerProfile>(profileElement.GetRawText(), JsonOptions)
                ?? throw new InvalidDataException("O arquivo de perfil está vazio.");
        }
        else
        {
            profile = JsonSerializer.Deserialize<MixerProfile>(json, JsonOptions)
                ?? throw new InvalidDataException("O arquivo de perfil está vazio.");
        }

        return SanitizeImported(profile);
    }

    public static ProfileTransferDocument ToTransferDocument(MixerProfile profile) => new()
    {
        TransferSchemaVersion = TransferSchemaVersion,
        ExportedAtUtc = DateTime.UtcNow,
        Profile = SanitizeForExport(profile)
    };

    public static MixerProfile SanitizeForExport(MixerProfile source)
    {
        var clone = new MixerProfile
        {
            SchemaVersion = Math.Max(11, source.SchemaVersion),
            Id = source.Id,
            Name = string.IsNullOrWhiteSpace(source.Name) ? "Perfil" : source.Name.Trim(),
            BoundApplicationKey = NullIfWhiteSpace(source.BoundApplicationKey),
            BoundApplicationName = NullIfWhiteSpace(source.BoundApplicationName),
            AutoDiscoverChannels = source.AutoDiscoverChannels,
            SuppressedApplicationKeys = (source.SuppressedApplicationKeys ?? [])
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Safety = CloneSafety(source.Safety),
            Channels = []
        };

        foreach (var channel in source.Channels
                     .Where(item => !string.IsNullOrWhiteSpace(item.ApplicationKey))
                     .OrderByDescending(item => item.IsPinned)
                     .ThenBy(item => item.Order)
                     .ThenBy(item => item.Index))
        {
            clone.Channels.Add(new ChannelBinding
            {
                Index = channel.Index,
                Order = channel.Order,
                ApplicationKey = channel.ApplicationKey,
                ApplicationName = channel.ApplicationName,
                ExecutableName = channel.ExecutableName,
                // Omit full paths — they are machine-specific and not needed to rematch sessions.
                ExecutablePath = null,
                TargetVolume = Math.Clamp(channel.TargetVolume, 0, 100),
                Ceiling = Math.Clamp(channel.Ceiling, 1, 100),
                IsMuted = channel.IsMuted,
                IsSolo = channel.IsSolo,
                ViewMode = string.Equals(channel.ViewMode, "expanded", StringComparison.OrdinalIgnoreCase)
                    ? "expanded"
                    : "collapsed",
                AccentColor = channel.AccentColor,
                IsPinned = channel.IsPinned,
                IsHidden = channel.IsHidden
            });
        }

        return clone;
    }

    private static MixerProfile SanitizeImported(MixerProfile profile)
    {
        var sanitized = SanitizeForExport(profile);
        sanitized.Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(sanitized.Name))
            sanitized.Name = "Perfil importado";
        sanitized.SchemaVersion = Math.Max(11, sanitized.SchemaVersion);
        return sanitized;
    }

    private static SafetyPolicy CloneSafety(SafetyPolicy source) => new()
    {
        Enabled = source.Enabled,
        GlobalCeiling = source.GlobalCeiling,
        NewSessionVolume = source.NewSessionVolume,
        RisePerSecond = source.RisePerSecond,
        FallPerSecond = source.FallPerSecond
    };

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class ProfileTransferDocument
{
    public int TransferSchemaVersion { get; set; } = ProfileTransfer.TransferSchemaVersion;
    public DateTime ExportedAtUtc { get; set; }
    public MixerProfile Profile { get; set; } = ProfileCatalog.CreateDefaultProfile();
}
