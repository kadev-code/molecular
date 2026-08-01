using System.Text.Json;
using Molecular.Core.Models;

namespace Molecular.Core.Persistence;

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;

    public ProfileStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Molecular",
            "profile.json");
    }

    public string FilePath => _filePath;
    public string BackupPath => _filePath + ".bak";

    public MixerProfile Load() => LoadDetailed().Catalog.ActiveProfile;

    public ProfileLoadResult LoadDetailed()
    {
        if (!File.Exists(_filePath))
            return new ProfileLoadResult(ProfileCatalog.CreateDefault(), false, false, null);

        try
        {
            return new ProfileLoadResult(NormalizeCatalog(ReadCatalog(_filePath)), false, false, null);
        }
        catch (Exception)
        {
            QuarantineCorruptFile(_filePath);
        }

        if (File.Exists(BackupPath))
        {
            try
            {
                var recovered = NormalizeCatalog(ReadCatalog(BackupPath));
                SaveCatalog(recovered);
                return new ProfileLoadResult(
                    recovered,
                    RecoveredFromBackup: true,
                    ResetToDefault: false,
                    Notice: "Perfil restaurado a partir do backup.");
            }
            catch
            {
                QuarantineCorruptFile(BackupPath);
            }
        }

        return new ProfileLoadResult(
            ProfileCatalog.CreateDefault(),
            RecoveredFromBackup: false,
            ResetToDefault: true,
            Notice: "Perfil corrompido. Um mixer vazio foi criado.");
    }

    public void Save(MixerProfile profile)
    {
        var catalog = TryReadCatalog(_filePath, out var existing)
            ? existing
            : ProfileCatalog.FromLegacyProfile(profile);

        EnsureProfileIdentity(profile);
        var index = catalog.Profiles.FindIndex(item =>
            string.Equals(item.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) catalog.Profiles[index] = profile;
        else catalog.Profiles.Add(profile);

        if (string.IsNullOrWhiteSpace(catalog.DefaultProfileId))
            catalog.DefaultProfileId = profile.Id;
        if (string.IsNullOrWhiteSpace(catalog.ActiveProfileId))
            catalog.ActiveProfileId = profile.Id;

        SaveCatalog(catalog);
    }

    public void SaveCatalog(ProfileCatalog catalog)
    {
        var normalized = NormalizeCatalog(catalog);
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(_filePath) && TryReadCatalog(_filePath, out _))
            File.Copy(_filePath, BackupPath, overwrite: true);

        var temporaryFile = _filePath + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(normalized, JsonOptions));
        File.Move(temporaryFile, _filePath, true);
    }

    private static ProfileCatalog ReadCatalog(string path)
    {
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("Profiles", out _))
        {
            var catalog = JsonSerializer.Deserialize<ProfileCatalog>(json, JsonOptions);
            return catalog ?? throw new InvalidDataException("O catálogo de perfis está vazio.");
        }

        var legacy = JsonSerializer.Deserialize<MixerProfile>(json, JsonOptions);
        if (legacy is null) throw new InvalidDataException("O perfil está vazio.");
        return ProfileCatalog.FromLegacyProfile(legacy);
    }

    private static bool TryReadCatalog(string path, out ProfileCatalog catalog)
    {
        try
        {
            catalog = ReadCatalog(path);
            return true;
        }
        catch
        {
            catalog = null!;
            return false;
        }
    }

    private static void QuarantineCorruptFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var quarantinePath = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(path, quarantinePath, overwrite: true);
        }
        catch
        {
            // Quarantine is best-effort; loading must continue.
        }
    }

    private static ProfileCatalog NormalizeCatalog(ProfileCatalog catalog)
    {
        catalog.Profiles ??= [];
        if (catalog.Profiles.Count == 0)
        {
            catalog.ActiveProfileId = string.Empty;
            catalog.DefaultProfileId = string.Empty;
            catalog.CatalogVersion = Math.Max(1, catalog.CatalogVersion);
            return catalog;
        }

        foreach (var profile in catalog.Profiles)
            Normalize(profile);

        if (string.IsNullOrWhiteSpace(catalog.DefaultProfileId)
            || catalog.FindById(catalog.DefaultProfileId) is null)
        {
            catalog.DefaultProfileId = catalog.Profiles[0].Id;
        }

        if (string.IsNullOrWhiteSpace(catalog.ActiveProfileId)
            || catalog.FindById(catalog.ActiveProfileId) is null)
        {
            catalog.ActiveProfileId = catalog.DefaultProfileId;
        }

        catalog.CatalogVersion = Math.Max(1, catalog.CatalogVersion);
        return catalog;
    }

    private static void EnsureProfileIdentity(MixerProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id))
            profile.Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(profile.Name))
            profile.Name = "Principal";
    }

    private static MixerProfile Normalize(MixerProfile profile)
    {
        EnsureProfileIdentity(profile);
        profile.Channels ??= [];

        // A primeira versão gravava 50% como teto oculto em todos os canais.
        // A partir da versão 2, o teto global controla a segurança por padrão e
        // o teto individual só restringe um canal quando for configurado de forma explícita.
        if (profile.SchemaVersion < 2)
        {
            foreach (var channel in profile.Channels.Where(channel => Math.Abs(channel.Ceiling - 50) < 0.01))
                channel.Ceiling = 100;
            profile.SchemaVersion = 2;
        }

        if (profile.SchemaVersion < 3)
        {
            foreach (var channel in profile.Channels)
            {
                channel.Order = channel.Index;
                channel.ViewMode = channel.Index <= 3 ? "expanded" : "collapsed";
                channel.AccentColor ??= DefaultAccent(channel.Index);
            }

            profile.SchemaVersion = 3;
        }

        if (profile.SchemaVersion < 4)
        {
            foreach (var channel in profile.Channels.Where(channel => channel.ApplicationKey is null))
                channel.ViewMode = "collapsed";
            profile.SchemaVersion = 4;
        }

        if (profile.SchemaVersion < 5)
        {
            foreach (var channel in profile.Channels.Where(channel => channel.ApplicationKey is null))
                channel.ViewMode = "collapsed";
            profile.SchemaVersion = 5;
        }

        if (profile.SchemaVersion < 6)
        {
            var assignedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var channel in profile.Channels
                         .OrderBy(channel => channel.Order)
                         .ThenBy(channel => channel.Index))
            {
                if (string.IsNullOrWhiteSpace(channel.ApplicationKey)
                    || assignedKeys.Add(channel.ApplicationKey))
                {
                    continue;
                }

                ClearAssignment(channel);
            }

            profile.SchemaVersion = 6;
        }

        if (profile.SchemaVersion < 7)
        {
            profile.Channels = profile.Channels
                .Where(channel => !string.IsNullOrWhiteSpace(channel.ApplicationKey))
                .ToList();
            profile.SchemaVersion = 7;
        }

        if (profile.SchemaVersion < 8)
        {
            foreach (var channel in profile.Channels)
                channel.Ceiling = 100;
            profile.SchemaVersion = 8;
        }

        if (profile.SchemaVersion < 9)
        {
            EnsureProfileIdentity(profile);
            profile.SchemaVersion = 9;
        }

        if (profile.SchemaVersion < 10)
        {
            // Auto-discover is opt-in per profile (off by default).
            profile.AutoDiscoverChannels = false;
            profile.SchemaVersion = 10;
        }

        if (profile.SchemaVersion < 11)
        {
            profile.SuppressedApplicationKeys = [];
            profile.SchemaVersion = 11;
        }

        profile.SuppressedApplicationKeys ??= [];
        profile.SuppressedApplicationKeys = profile.SuppressedApplicationKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        NormalizeChannels(profile);
        return profile;
    }

    private static void NormalizeChannels(MixerProfile profile)
    {
        profile.Channels ??= [];

        var assignedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<ChannelBinding>();
        foreach (var channel in MixerChannelRegistry.Sorted(profile)
                     .Where(channel => !string.IsNullOrWhiteSpace(channel.ApplicationKey)))
        {
            if (!assignedKeys.Add(channel.ApplicationKey!)) continue;

            channel.AccentColor ??= DefaultAccent(normalized.Count + 1);
            channel.ViewMode = string.Equals(channel.ViewMode, "expanded", StringComparison.OrdinalIgnoreCase)
                || channel.IsPinned
                ? "expanded"
                : "collapsed";
            channel.TargetVolume = Math.Clamp(channel.TargetVolume, 0, 100);
            channel.Ceiling = Math.Clamp(channel.Ceiling, 1, 100);
            normalized.Add(channel);
        }

        profile.Channels = normalized;
        var nextIndex = 1;
        foreach (var channel in MixerChannelRegistry.Sorted(profile).ToList())
        {
            channel.Index = nextIndex;
            channel.Order = nextIndex;
            nextIndex++;
        }
    }

    private static void ClearAssignment(ChannelBinding channel)
    {
        channel.ApplicationKey = null;
        channel.ApplicationName = null;
        channel.ExecutableName = null;
        channel.ExecutablePath = null;
        channel.IsMuted = false;
        channel.IsSolo = false;
        channel.ViewMode = "collapsed";
    }

    private static string DefaultAccent(int index) => index switch
    {
        1 or 5 => "#7B5CFF",
        2 or 6 => "#25D7E8",
        3 or 7 => "#D94DFF",
        _ => "#438CFF"
    };
}
