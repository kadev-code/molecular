namespace Molecular.Core.Models;

/// <summary>
/// Owns the invariants for the dynamic list of mixer channels.
/// </summary>
public static class MixerChannelRegistry
{
    public static bool ContainsApplication(MixerProfile profile, string applicationKey) =>
        profile.Channels.Any(channel =>
            string.Equals(channel.ApplicationKey, applicationKey, StringComparison.OrdinalIgnoreCase));

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
        profile.Channels.Sort(static (left, right) =>
        {
            var order = left.Order.CompareTo(right.Order);
            return order != 0 ? order : left.Index.CompareTo(right.Index);
        });
        return true;
    }

    private static string DefaultAccent(int index) => (index % 4) switch
    {
        1 => "#7B5CFF",
        2 => "#25D7E8",
        3 => "#D94DFF",
        _ => "#438CFF"
    };
}
