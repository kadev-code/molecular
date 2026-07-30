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

    public MixerProfile Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new MixerProfile();
            }

            var profile = JsonSerializer.Deserialize<MixerProfile>(File.ReadAllText(_filePath), JsonOptions);
            return Normalize(profile ?? new MixerProfile());
        }
        catch
        {
            return new MixerProfile();
        }
    }

    public void Save(MixerProfile profile)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryFile = _filePath + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(profile, JsonOptions));
        File.Move(temporaryFile, _filePath, true);
    }

    private static MixerProfile Normalize(MixerProfile profile)
    {
        // A primeira versão gravava 50% como teto oculto em todos os canais.
        // A partir da versão 2, o teto global controla a segurança por padrão e
        // o teto individual só restringe um canal quando for configurado de forma explícita.
        if (profile.SchemaVersion < 2)
        {
            foreach (var channel in profile.Channels.Where(channel => Math.Abs(channel.Ceiling - 50) < 0.01))
            {
                channel.Ceiling = 100;
            }

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
            {
                channel.ViewMode = "collapsed";
            }

            profile.SchemaVersion = 4;
        }

        if (profile.SchemaVersion < 5)
        {
            foreach (var channel in profile.Channels.Where(channel => channel.ApplicationKey is null))
            {
                channel.ViewMode = "collapsed";
            }

            profile.SchemaVersion = 5;
        }

        // A single Core Audio application is one controllable target. Older
        // profiles could bind the same target to multiple channels, causing the
        // refresh loop to send conflicting volume and mute commands.
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

        // Versao 7 removes the fixed eight-slot layout. A channel now represents
        // an actual saved assignment and is created only when the user chooses an
        // application. Existing assignments are preserved; obsolete empty slots
        // from older profiles are discarded.
        if (profile.SchemaVersion < 7)
        {
            profile.Channels = profile.Channels
                .Where(channel => !string.IsNullOrWhiteSpace(channel.ApplicationKey))
                .ToList();
            profile.SchemaVersion = 7;
        }

        NormalizeChannels(profile);
        return profile;
    }

    private static void NormalizeChannels(MixerProfile profile)
    {
        profile.Channels ??= [];

        var assignedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<ChannelBinding>();
        var nextIndex = 1;
        foreach (var channel in profile.Channels
                     .Where(channel => !string.IsNullOrWhiteSpace(channel.ApplicationKey))
                     .OrderBy(channel => channel.Order <= 0 ? int.MaxValue : channel.Order)
                     .ThenBy(channel => channel.Index <= 0 ? int.MaxValue : channel.Index))
        {
            if (!assignedKeys.Add(channel.ApplicationKey!)) continue;

            channel.Index = nextIndex;
            channel.Order = nextIndex;
            channel.AccentColor ??= DefaultAccent(nextIndex);
            channel.ViewMode = string.Equals(channel.ViewMode, "expanded", StringComparison.OrdinalIgnoreCase)
                ? "expanded"
                : "collapsed";
            channel.TargetVolume = Math.Clamp(channel.TargetVolume, 0, 100);
            channel.Ceiling = Math.Clamp(channel.Ceiling, 1, 100);
            normalized.Add(channel);
            nextIndex++;
        }

        profile.Channels = normalized;
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
