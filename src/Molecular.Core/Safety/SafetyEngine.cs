using Molecular.Core.Models;

namespace Molecular.Core.Safety;

public sealed class SafetyEngine
{
    public SafetyEngine(SafetyPolicy policy) => Policy = policy;

    public SafetyPolicy Policy { get; }

    public double Clamp(double requestedVolume, double? channelCeiling = null)
    {
        var ceiling = Policy.Enabled
            ? Math.Min(Policy.GlobalCeiling, channelCeiling ?? Policy.GlobalCeiling)
            : 100d;

        return Math.Clamp(requestedVolume, 0, ceiling);
    }

    public double SafeInitialVolume(double? configuredVolume = null, double? channelCeiling = null) =>
        Clamp(configuredVolume ?? Policy.NewSessionVolume, channelCeiling);

    public double StepToward(double current, double requested, TimeSpan elapsed, double? channelCeiling = null)
    {
        var target = Clamp(requested, channelCeiling);
        if (Math.Abs(target - current) < 0.05)
        {
            return target;
        }

        var rate = target > current ? Policy.RisePerSecond : Policy.FallPerSecond;
        var maximumStep = Math.Max(0.1, rate * elapsed.TotalSeconds);
        return target > current
            ? Math.Min(target, current + maximumStep)
            : Math.Max(target, current - maximumStep);
    }
}
