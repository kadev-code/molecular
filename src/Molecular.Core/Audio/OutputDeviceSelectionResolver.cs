using Molecular.Core.Models;

namespace Molecular.Core.Audio;

public sealed record OutputDeviceSelection(
    AudioOutputDevice? DisplayDevice,
    string? PreferredDeviceId,
    bool IsPreferredUnavailable,
    bool IsUsingFallback);

public static class OutputDeviceSelectionResolver
{
    public static OutputDeviceSelection Resolve(
        IReadOnlyList<AudioOutputDevice> devices,
        string? preferredDeviceId,
        bool allowDefaultFallback)
    {
        if (string.IsNullOrWhiteSpace(preferredDeviceId))
        {
            return new OutputDeviceSelection(
                devices.FirstOrDefault(device => device.IsDefault) ?? devices.FirstOrDefault(),
                PreferredDeviceId: null,
                IsPreferredUnavailable: false,
                IsUsingFallback: false);
        }

        var preferred = devices.FirstOrDefault(device =>
            string.Equals(device.Id, preferredDeviceId, StringComparison.OrdinalIgnoreCase));
        if (preferred is not null)
        {
            return new OutputDeviceSelection(
                preferred,
                preferredDeviceId,
                IsPreferredUnavailable: false,
                IsUsingFallback: false);
        }

        var fallback = allowDefaultFallback
            ? devices.FirstOrDefault(device => device.IsDefault) ?? devices.FirstOrDefault()
            : null;
        return new OutputDeviceSelection(
            fallback,
            preferredDeviceId,
            IsPreferredUnavailable: true,
            IsUsingFallback: fallback is not null);
    }
}
