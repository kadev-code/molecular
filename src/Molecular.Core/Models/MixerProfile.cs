namespace Molecular.Core.Models;

public sealed class MixerProfile
{
    public int SchemaVersion { get; set; } = 7;
    public string Name { get; set; } = "Principal";
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
