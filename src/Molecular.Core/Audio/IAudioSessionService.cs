using Molecular.Core.Models;

namespace Molecular.Core.Audio;

public interface IAudioSessionService : IDisposable
{
    event EventHandler? OutputDevicesChanged;
    string OutputDeviceName { get; }
    IReadOnlyList<AudioOutputDevice> ReadOutputDevices();
    void SelectOutputDevice(string? deviceId);
    IReadOnlyList<AudioApplication> ReadApplications();
    void ApplyChanges(IReadOnlyCollection<AudioSessionChange> changes);
    void SetVolume(string applicationKey, double volumePercent);
    void SetMute(string applicationKey, bool isMuted);
}
