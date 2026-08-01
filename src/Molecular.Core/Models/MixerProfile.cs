namespace Molecular.Core.Models;

public sealed class MixerProfile
{
    public int SchemaVersion { get; set; } = 11;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Principal";
    /// <summary>
    /// When this application key is live (e.g. process:discord), the mixer auto-activates
    /// this profile. Closing that app restores the default profile.
    /// </summary>
    public string? BoundApplicationKey { get; set; }
    public string? BoundApplicationName { get; set; }
    /// <summary>
    /// When enabled, newly detected audio sessions are added as channels on this profile.
    /// </summary>
    public bool AutoDiscoverChannels { get; set; }
    /// <summary>
    /// Applications explicitly removed while auto-discovery is enabled. Keeping
    /// these keys in the profile prevents a removed channel from returning after
    /// the application reconnects or Molecular restarts.
    /// </summary>
    public List<string> SuppressedApplicationKeys { get; set; } = [];
    public SafetyPolicy Safety { get; set; } = new();
    public List<ChannelBinding> Channels { get; set; } = [];
}

public sealed class ChannelBinding
{
    public int Index { get; set; }
    public string? ApplicationKey { get; set; }
    public string? ApplicationName { get; set; }
    public string? ExecutableName { get; set; }
    public string? ExecutablePath { get; set; }
    public double TargetVolume { get; set; } = 20;
    public double Ceiling { get; set; } = 100;
    public bool IsMuted { get; set; }
    public bool IsSolo { get; set; }
    public string ViewMode { get; set; } = "collapsed";
    public int Order { get; set; }
    public string? AccentColor { get; set; }
    public bool IsPinned { get; set; }
    public bool IsHidden { get; set; }
}

public sealed class SafetyPolicy
{
    public bool Enabled { get; set; } = true;
    public double GlobalCeiling { get; set; } = 50;
    public double NewSessionVolume { get; set; } = 20;
    public double RisePerSecond { get; set; } = 8;
    public double FallPerSecond { get; set; } = 100;
}

public sealed class ProfileCatalog
{
    public int CatalogVersion { get; set; } = 1;
    public string ActiveProfileId { get; set; } = string.Empty;
    public string DefaultProfileId { get; set; } = string.Empty;
    public List<MixerProfile> Profiles { get; set; } = [];

    public MixerProfile? ActiveProfileOrNull =>
        Profiles.FirstOrDefault(profile => string.Equals(profile.Id, ActiveProfileId, StringComparison.OrdinalIgnoreCase))
        ?? Profiles.FirstOrDefault(profile => string.Equals(profile.Id, DefaultProfileId, StringComparison.OrdinalIgnoreCase))
        ?? Profiles.FirstOrDefault();

    public MixerProfile ActiveProfile => ActiveProfileOrNull ?? CreateDefaultProfile();

    public MixerProfile DefaultProfile =>
        Profiles.FirstOrDefault(profile => string.Equals(profile.Id, DefaultProfileId, StringComparison.OrdinalIgnoreCase))
        ?? Profiles.FirstOrDefault()
        ?? CreateDefaultProfile();

    public bool HasProfiles => Profiles.Count > 0;

    public static ProfileCatalog CreateDefault()
    {
        var profile = CreateDefaultProfile();
        return new ProfileCatalog
        {
            CatalogVersion = 1,
            ActiveProfileId = profile.Id,
            DefaultProfileId = profile.Id,
            Profiles = [profile]
        };
    }

    public static ProfileCatalog FromLegacyProfile(MixerProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id))
            profile.Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(profile.Name))
            profile.Name = "Principal";
        return new ProfileCatalog
        {
            CatalogVersion = 1,
            ActiveProfileId = profile.Id,
            DefaultProfileId = profile.Id,
            Profiles = [profile]
        };
    }

    public static MixerProfile CreateDefaultProfile() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "Principal",
        SchemaVersion = 11
    };

    public MixerProfile? FindById(string? id) =>
        Profiles.FirstOrDefault(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase));

    public MixerProfile? FindBoundToApplication(string applicationKey) =>
        Profiles.FirstOrDefault(profile =>
            !string.IsNullOrWhiteSpace(profile.BoundApplicationKey)
            && string.Equals(profile.BoundApplicationKey, applicationKey, StringComparison.OrdinalIgnoreCase));
}
