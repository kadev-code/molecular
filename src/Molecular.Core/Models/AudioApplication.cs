namespace Molecular.Core.Models;

public sealed record AudioApplication(
    string Key,
    string DisplayName,
    string ExecutableName,
    string? ExecutablePath,
    IReadOnlyList<int> ProcessIds,
    double Volume,
    bool IsMuted,
    double Peak,
    bool IsSystemSounds);
