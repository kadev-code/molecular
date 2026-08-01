namespace Molecular.Core.Models;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>When true, the window close button hides to tray. When false, it exits the app.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>When true, Molecular starts hidden in the system tray.</summary>
    public bool StartInTray { get; set; }

    /// <summary>Preferred WASAPI output device id. Null/empty means follow Windows default.</summary>
    public string? PreferredOutputDeviceId { get; set; }

    /// <summary>If the preferred device is missing, fall back to the Windows default output.</summary>
    public bool PreferSystemDefaultFallback { get; set; } = true;

    /// <summary>Foreground meter poll interval in milliseconds.</summary>
    public int MeterIntervalMs { get; set; } = 100;
}

public sealed record MeterIntervalOption(int IntervalMs, string DisplayName);
