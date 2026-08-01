namespace Molecular.Core.Models;

/// <summary>
/// Owns the invariants for the dynamic list of mixer channels.
/// </summary>
public static class MixerChannelRegistry
{
    public static readonly string[] AccentPalette =
    [
        "#7B5CFF",
        "#25D7E8",
        "#D94DFF",
        "#438CFF",
        "#2ECC9A",
        "#F0A202"
    ];

    public static bool ContainsApplication(MixerProfile profile, string applicationKey) =>
        profile.Channels.Any(channel =>
            string.Equals(channel.ApplicationKey, applicationKey, StringComparison.OrdinalIgnoreCase));

    public static bool IsAutoDiscoverSuppressed(MixerProfile profile, string applicationKey) =>
        profile.SuppressedApplicationKeys.Any(key =>
            string.Equals(key, applicationKey, StringComparison.OrdinalIgnoreCase));

    public static void SuppressAutoDiscover(MixerProfile profile, string applicationKey)
    {
        if (string.IsNullOrWhiteSpace(applicationKey) || IsAutoDiscoverSuppressed(profile, applicationKey)) return;
        profile.SuppressedApplicationKeys.Add(applicationKey.Trim());
    }

    public static void AllowAutoDiscover(MixerProfile profile, string applicationKey) =>
        profile.SuppressedApplicationKeys.RemoveAll(key =>
            string.Equals(key, applicationKey, StringComparison.OrdinalIgnoreCase));

    public static ChannelBinding Add(
        MixerProfile profile,
        string applicationKey,
        string applicationName,
        string executableName,
        string? executablePath,
        double initialVolume)
    {
        if (ContainsApplication(profile, applicationKey))
            throw new InvalidOperationException("O aplicativo ja possui um canal.");

        AllowAutoDiscover(profile, applicationKey);

        var nextIndex = profile.Channels.Count == 0
            ? 1
            : profile.Channels.Max(channel => channel.Index) + 1;
        var nextOrder = profile.Channels.Count == 0
            ? 1
            : profile.Channels.Max(channel => channel.Order) + 1;
        var channel = new ChannelBinding
        {
            Index = nextIndex,
            Order = nextOrder,
            ApplicationKey = applicationKey,
            ApplicationName = applicationName,
            ExecutableName = executableName,
            ExecutablePath = executablePath,
            TargetVolume = Math.Clamp(initialVolume, 0, 100),
            AccentColor = DefaultAccent(nextIndex),
            ViewMode = "expanded"
        };

        profile.Channels.Add(channel);
        return channel;
    }

    public static bool Remove(MixerProfile profile, ChannelBinding channel) =>
        profile.Channels.Remove(channel);

    public static bool Restore(MixerProfile profile, ChannelBinding channel)
    {
        if (channel.ApplicationKey is not null && ContainsApplication(profile, channel.ApplicationKey))
            return false;

        if (profile.Channels.Contains(channel)) return false;
        profile.Channels.Add(channel);
        RenumberOrders(profile);
        return true;
    }

    public static bool Move(MixerProfile profile, ChannelBinding channel, int delta)
    {
        if (delta == 0 || !profile.Channels.Contains(channel)) return false;

        var ordered = Sorted(profile).ToList();
        var index = ordered.FindIndex(item => ReferenceEquals(item, channel));
        if (index < 0) return false;

        var targetIndex = index + delta;
        if (targetIndex < 0 || targetIndex >= ordered.Count) return false;

        // Keep pinned channels grouped at the front.
        if (ordered[index].IsPinned != ordered[targetIndex].IsPinned) return false;

        (ordered[index], ordered[targetIndex]) = (ordered[targetIndex], ordered[index]);
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].Order = i + 1;
        return true;
    }

    public static void SetPinned(MixerProfile profile, ChannelBinding channel, bool pinned)
    {
        if (!profile.Channels.Contains(channel)) return;
        channel.IsPinned = pinned;
        if (pinned) channel.ViewMode = "expanded";
        RenumberOrders(profile);
    }

    public static string CycleAccent(ChannelBinding channel)
    {
        var current = channel.AccentColor ?? AccentPalette[0];
        var index = Array.FindIndex(
            AccentPalette,
            color => string.Equals(color, current, StringComparison.OrdinalIgnoreCase));
        var next = AccentPalette[(index + 1) % AccentPalette.Length];
        channel.AccentColor = next;
        return next;
    }

    public static IEnumerable<ChannelBinding> Sorted(MixerProfile profile) =>
        profile.Channels
            .OrderByDescending(channel => channel.IsPinned)
            .ThenBy(channel => channel.Order <= 0 ? int.MaxValue : channel.Order)
            .ThenBy(channel => channel.Index <= 0 ? int.MaxValue : channel.Index);

    public static void RenumberOrders(MixerProfile profile)
    {
        var ordered = Sorted(profile).ToList();
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].Order = i + 1;
    }

    private static string DefaultAccent(int index) => AccentPalette[(Math.Max(1, index) - 1) % AccentPalette.Length];
}
