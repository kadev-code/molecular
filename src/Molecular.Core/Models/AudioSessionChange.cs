namespace Molecular.Core.Models;

public sealed record AudioSessionChange(
    string ApplicationKey,
    double? VolumePercent = null,
    bool? IsMuted = null);
