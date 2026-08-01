namespace Molecular.Core.Models;

/// <summary>
/// Lets a manual profile choice temporarily override applications that are
/// already open, while rearming automatic activation after those matches close.
/// </summary>
public sealed class BoundProfileActivationGate
{
    private bool _suppressedUntilNoMatch;

    public void SuppressCurrentMatches() => _suppressedUntilNoMatch = true;

    public bool CanActivateMatch() => !_suppressedUntilNoMatch;

    public void ObserveNoMatch() => _suppressedUntilNoMatch = false;
}
