namespace Molecular.Core.Runtime;

/// <summary>
/// Guarantees one application process per Windows user session and lets a
/// subsequent launch ask the existing process to restore its main window.
/// </summary>
public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly RegisteredWaitHandle _registeredWait;
    private bool _disposed;

    private SingleInstanceCoordinator(Mutex mutex, EventWaitHandle activationEvent)
    {
        _mutex = mutex;
        _activationEvent = activationEvent;
        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            static (state, timedOut) =>
            {
                if (!timedOut && state is SingleInstanceCoordinator coordinator && !coordinator._disposed)
                    coordinator.ActivationRequested?.Invoke(coordinator, EventArgs.Empty);
            },
            this,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public event EventHandler? ActivationRequested;

    public static bool TryAcquire(string applicationId, out SingleInstanceCoordinator? coordinator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        var safeId = string.Concat(applicationId.Select(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' ? character : '_'));
        var mutexName = $"Local\\{safeId}.Instance";
        var activationName = $"Local\\{safeId}.Activate";

        var activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, activationName);
        var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            activationEvent.Set();
            activationEvent.Dispose();

            coordinator = null;
            return false;
        }

        try
        {
            coordinator = new SingleInstanceCoordinator(mutex, activationEvent);
            return true;
        }
        catch
        {
            activationEvent.Dispose();
            mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registeredWait.Unregister(null);
        _activationEvent.Dispose();
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
