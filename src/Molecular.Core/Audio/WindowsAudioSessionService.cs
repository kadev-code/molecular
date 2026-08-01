using System.Collections.Concurrent;
using System.Diagnostics;
using Molecular.Core.Diagnostics;
using Molecular.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Molecular.Core.Audio;

public sealed class WindowsAudioSessionService : IAudioSessionService
{
    private static readonly IReadOnlyDictionary<string, string> FriendlyApplicationNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["msedge"] = "Microsoft Edge",
            ["chrome"] = "Google Chrome",
            ["firefox"] = "Mozilla Firefox",
            ["spotify"] = "Spotify",
            ["discord"] = "Discord",
            ["steam"] = "Steam",
            ["zoom"] = "Zoom",
            ["vlc"] = "VLC Media Player"
        };

    private readonly object _sessionGate = new();
    private readonly Dictionary<string, TrackedSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<SessionEvent> _sessionEvents = new();
    private readonly Dictionary<int, ApplicationIdentity> _identityCache = new();
    private readonly MMDeviceEnumerator _notificationEnumerator;
    private readonly DeviceNotificationClient _notificationClient;
    private volatile string? _selectedDeviceId;
    private volatile bool _allowDefaultFallback = true;
    private MMDevice? _sessionDevice;
    private AudioSessionManager? _sessionManager;
    private int _sessionsNeedRebuild = 1;
    private bool _disposed;

    public WindowsAudioSessionService()
    {
        _notificationEnumerator = new MMDeviceEnumerator();
        _notificationClient = new DeviceNotificationClient(OnOutputDevicesChanged, OnOutputDeviceListChanged);
        _notificationEnumerator.RegisterEndpointNotificationCallback(_notificationClient);
        OperationalLog.Shared.Info("audio", "Serviço Core Audio inicializado");
    }

    public event EventHandler? OutputDevicesChanged;

    public string OutputDeviceName { get; private set; } = "Saída padrão do Windows";

    public IReadOnlyList<AudioOutputDevice> ReadOutputDevices()
    {
        using var deviceEnumerator = new MMDeviceEnumerator();
        using var defaultDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var devices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var result = new List<AudioOutputDevice>(devices.Count);
        for (var index = 0; index < devices.Count; index++)
        {
            var device = devices[index];
            try
            {
                result.Add(new AudioOutputDevice(
                    device.ID,
                    device.FriendlyName,
                    string.Equals(device.ID, defaultDevice.ID, StringComparison.OrdinalIgnoreCase)));
            }
            finally
            {
                device.Dispose();
            }
        }

        return result.OrderByDescending(device => device.IsDefault).ThenBy(device => device.Name).ToArray();
    }

    public void SelectOutputDevice(string? deviceId, bool allowDefaultFallback = true)
    {
        if (string.Equals(_selectedDeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
            && _allowDefaultFallback == allowDefaultFallback)
        {
            return;
        }

        _selectedDeviceId = deviceId;
        _allowDefaultFallback = allowDefaultFallback;
        Interlocked.Exchange(ref _sessionsNeedRebuild, 1);
        OperationalLog.Shared.Info(
            "audio",
            $"Dispositivo preferido alterado: {deviceId ?? "(padrão)"}; fallback={(allowDefaultFallback ? "sim" : "não")}");
    }

    public IReadOnlyList<AudioApplication> ReadApplications()
    {
        lock (_sessionGate)
        {
            ThrowIfDisposed();
            EnsureSessionMonitor();
            DrainSessionEvents();

            // Only the instantaneous peak still needs polling. Session creation,
            // volume, mute, state and disconnection are maintained by callbacks.
            foreach (var session in _sessions.Values.ToArray())
            {
                try
                {
                    session.Peak = session.Control.AudioMeterInformation.MasterPeakValue * 100d;
                }
                catch
                {
                    RemoveSession(session.InstanceId);
                }
            }

            CleanupIdentityCache();
            return BuildApplicationSnapshots();
        }
    }

    public void SetVolume(string applicationKey, double volumePercent) =>
        ApplyChanges([new AudioSessionChange(applicationKey, VolumePercent: volumePercent)]);

    public void SetMute(string applicationKey, bool isMuted) =>
        ApplyChanges([new AudioSessionChange(applicationKey, IsMuted: isMuted)]);

    public void ApplyChanges(IReadOnlyCollection<AudioSessionChange> changes)
    {
        if (changes.Count == 0) return;
        var changesByApplication = changes
            .GroupBy(change => change.ApplicationKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new AudioSessionChange(
                    group.Key,
                    group.LastOrDefault(change => change.VolumePercent.HasValue)?.VolumePercent,
                    group.LastOrDefault(change => change.IsMuted.HasValue)?.IsMuted),
                StringComparer.OrdinalIgnoreCase);

        lock (_sessionGate)
        {
            ThrowIfDisposed();
            EnsureSessionMonitor();
            DrainSessionEvents();

            foreach (var session in _sessions.Values.ToArray())
            {
                if (!changesByApplication.TryGetValue(session.Identity.Key, out var change)) continue;

                try
                {
                    if (change.VolumePercent.HasValue)
                    {
                        session.Volume = Math.Clamp(change.VolumePercent.Value, 0, 100);
                        session.Control.SimpleAudioVolume.Volume = (float)(session.Volume / 100d);
                    }

                    if (change.IsMuted.HasValue)
                    {
                        session.IsMuted = change.IsMuted.Value;
                        session.Control.SimpleAudioVolume.Mute = session.IsMuted;
                    }
                }
                catch
                {
                    RemoveSession(session.InstanceId);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_sessionGate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _notificationEnumerator.UnregisterEndpointNotificationCallback(_notificationClient); }
            catch { }
            _notificationEnumerator.Dispose();
            DisposeSessionMonitor();
        }
    }

    private void EnsureSessionMonitor()
    {
        while (true)
        {
            if (_sessionManager is not null && Interlocked.CompareExchange(ref _sessionsNeedRebuild, 0, 0) == 0)
                return;

            // Clear the flag before rebuilding so a concurrent request during
            // rebuild is preserved and triggers another pass below.
            Interlocked.Exchange(ref _sessionsNeedRebuild, 0);
            DisposeSessionMonitor();
            using var deviceEnumerator = new MMDeviceEnumerator();
            _sessionDevice = GetActiveDevice(deviceEnumerator);
            OutputDeviceName = _sessionDevice.FriendlyName;
            _sessionManager = _sessionDevice.AudioSessionManager;

            // Register before taking the initial snapshot. A callback that races the
            // enumeration is deduplicated by the session instance identifier.
            _sessionManager.OnSessionCreated += OnSessionCreated;
            _sessionManager.RefreshSessions();
            var sessions = _sessionManager.Sessions;
            for (var index = 0; index < sessions.Count; index++)
                AddSession(sessions[index]);

            OperationalLog.Shared.Info(
                "audio",
                $"Monitor reconstruído em '{OutputDeviceName}' com {_sessions.Count} sessão(ões)");

            if (Interlocked.CompareExchange(ref _sessionsNeedRebuild, 0, 0) == 0)
                return;
        }
    }

    private void OnSessionCreated(object sender, IAudioSessionControl newSession)
    {
        if (_disposed) return;
        try
        {
            _sessionEvents.Enqueue(new SessionCreatedEvent(new AudioSessionControl(newSession)));
        }
        catch
        {
            // A session may expire before NAudio can wrap the callback interface.
        }
    }

    private void AddSession(AudioSessionControl control)
    {
        string instanceId;
        try
        {
            instanceId = control.GetSessionInstanceIdentifier;
            if (string.IsNullOrWhiteSpace(instanceId)) instanceId = $"session:{Guid.NewGuid():N}";
            if (_sessions.ContainsKey(instanceId))
            {
                control.Dispose();
                return;
            }

            var processId = unchecked((int)control.GetProcessID);
            var isSystemSounds = control.IsSystemSoundsSession;
            var tracked = new TrackedSession(
                instanceId,
                control,
                ResolveIdentity(processId, control.DisplayName, isSystemSounds),
                processId,
                isSystemSounds,
                control.SimpleAudioVolume.Volume * 100d,
                control.SimpleAudioVolume.Mute);
            tracked.EventHandler = new SessionEventsHandler(sessionEvent =>
            {
                if (!_disposed) _sessionEvents.Enqueue(new TrackedSessionEvent(tracked, sessionEvent));
            });
            control.RegisterEventClient(tracked.EventHandler);
            _sessions.Add(instanceId, tracked);
            OperationalLog.Shared.Info(
                "session",
                $"Sessão criada: {tracked.Identity.Key} ({tracked.Identity.ExecutableName ?? "sem exe"})");
        }
        catch
        {
            control.Dispose();
        }
    }

    private void DrainSessionEvents()
    {
        while (_sessionEvents.TryDequeue(out var queuedEvent))
        {
            switch (queuedEvent)
            {
                case SessionCreatedEvent created:
                    AddSession(created.Control);
                    break;

                case TrackedSessionEvent { Session: var session, Event: var sessionEvent }
                    when _sessions.ContainsKey(session.InstanceId):
                    ApplySessionEvent(session, sessionEvent);
                    break;

                case TrackedSessionEvent:
                    break;
            }
        }
    }

    private void ApplySessionEvent(TrackedSession session, AudioSessionEvent sessionEvent)
    {
        switch (sessionEvent)
        {
            case VolumeChangedEvent volume:
                session.Volume = volume.Volume * 100d;
                session.IsMuted = volume.IsMuted;
                break;

            case DisplayNameChangedEvent displayName when !string.IsNullOrWhiteSpace(displayName.DisplayName):
                session.Identity = ResolveIdentity(session.ProcessId, displayName.DisplayName, session.IsSystemSounds, refresh: true);
                break;

            case RefreshVolumeEvent:
                try
                {
                    session.Volume = session.Control.SimpleAudioVolume.Volume * 100d;
                    session.IsMuted = session.Control.SimpleAudioVolume.Mute;
                }
                catch
                {
                    RemoveSession(session.InstanceId);
                }
                break;

            case StateChangedEvent { State: AudioSessionState.AudioSessionStateExpired }:
            case DisconnectedEvent:
                RemoveSession(session.InstanceId);
                break;
        }
    }

    private void RemoveSession(string instanceId)
    {
        if (!_sessions.Remove(instanceId, out var session)) return;
        OperationalLog.Shared.Info(
            "session",
            $"Sessão removida: {session.Identity.Key} ({session.Identity.ExecutableName ?? "sem exe"})");
        session.Dispose();
    }

    private IReadOnlyList<AudioApplication> BuildApplicationSnapshots() => _sessions.Values
        .GroupBy(item => item.Identity.Key, StringComparer.OrdinalIgnoreCase)
        .Select(group => new AudioApplication(
            group.Key,
            group.First().Identity.Name,
            group.First().Identity.ExecutableName,
            group.First().Identity.ExecutablePath,
            group.Select(item => item.ProcessId).Distinct().ToArray(),
            group.Average(item => item.Volume),
            group.All(item => item.IsMuted),
            group.Max(item => item.Peak),
            group.Any(item => item.IsSystemSounds)))
        .OrderByDescending(item => item.Peak)
        .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    private void CleanupIdentityCache()
    {
        var activeProcessIds = _sessions.Values.Where(session => !session.IsSystemSounds)
            .Select(session => session.ProcessId)
            .ToHashSet();
        foreach (var staleProcessId in _identityCache.Keys.Where(processId => !activeProcessIds.Contains(processId)).ToArray())
            _identityCache.Remove(staleProcessId);
    }

    private void DisposeSessionMonitor()
    {
        if (_sessionManager is not null)
        {
            try { _sessionManager.OnSessionCreated -= OnSessionCreated; }
            catch { }
        }

        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
        while (_sessionEvents.TryDequeue(out var queuedEvent))
        {
            if (queuedEvent is SessionCreatedEvent created) created.Control.Dispose();
        }

        _sessionManager?.Dispose();
        _sessionManager = null;
        _sessionDevice?.Dispose();
        _sessionDevice = null;
    }

    public void RequestSessionRebuild()
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _sessionsNeedRebuild, 1);
        OperationalLog.Shared.Info("audio", "Reconstrução do monitor solicitada");
    }

    private void OnOutputDevicesChanged()
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _sessionsNeedRebuild, 1);
        OperationalLog.Shared.Info("audio", "Topologia de dispositivo alterada");
        OutputDevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnOutputDeviceListChanged()
    {
        if (_disposed) return;
        OutputDevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private MMDevice GetActiveDevice(MMDeviceEnumerator deviceEnumerator)
    {
        if (!string.IsNullOrWhiteSpace(_selectedDeviceId))
        {
            try
            {
                var preferred = deviceEnumerator.GetDevice(_selectedDeviceId);
                if (preferred.State == DeviceState.Active) return preferred;
                preferred.Dispose();
                if (!_allowDefaultFallback)
                    throw new InvalidOperationException("O dispositivo de saída preferido está indisponível.");
            }
            catch when (_allowDefaultFallback)
            {
                // Keep the preferred id. Device notifications will rebuild the
                // monitor and return to it automatically when it reconnects.
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("O dispositivo de saída preferido está indisponível.", exception);
            }
        }

        return deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private ApplicationIdentity ResolveIdentity(
        int processId,
        string? sessionName,
        bool isSystemSounds,
        bool refresh = false)
    {
        if (isSystemSounds || processId == 0)
            return new ApplicationIdentity("system:sounds", "Sons do sistema", "Windows", null);

        if (!refresh && _identityCache.TryGetValue(processId, out var cachedIdentity)) return cachedIdentity;

        try
        {
            using var process = Process.GetProcessById(processId);
            var processName = process.ProcessName;
            var displayName = FriendlyApplicationNames.GetValueOrDefault(processName)
                ?? (string.IsNullOrWhiteSpace(sessionName) ? Humanize(processName) : sessionName.Trim());
            string? executablePath = null;
            try { executablePath = process.MainModule?.FileName; }
            catch { }
            var identity = new ApplicationIdentity(
                $"process:{processName.ToLowerInvariant()}",
                displayName,
                $"{processName}.exe",
                executablePath);
            _identityCache[processId] = identity;
            return identity;
        }
        catch
        {
            var fallback = string.IsNullOrWhiteSpace(sessionName) ? $"Aplicativo {processId}" : sessionName.Trim();
            return new ApplicationIdentity($"pid:{processId}", fallback, $"PID {processId}", null);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowsAudioSessionService));
    }

    private static string Humanize(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "Aplicativo"
            : char.ToUpperInvariant(value[0]) + value[1..];

    private sealed record ApplicationIdentity(
        string Key,
        string Name,
        string ExecutableName,
        string? ExecutablePath);

    private sealed class TrackedSession(
        string instanceId,
        AudioSessionControl control,
        ApplicationIdentity identity,
        int processId,
        bool isSystemSounds,
        double volume,
        bool isMuted) : IDisposable
    {
        public string InstanceId { get; } = instanceId;
        public AudioSessionControl Control { get; } = control;
        public ApplicationIdentity Identity { get; set; } = identity;
        public int ProcessId { get; } = processId;
        public bool IsSystemSounds { get; } = isSystemSounds;
        public double Volume { get; set; } = volume;
        public bool IsMuted { get; set; } = isMuted;
        public double Peak { get; set; }
        public SessionEventsHandler? EventHandler { get; set; }

        public void Dispose()
        {
            if (EventHandler is not null)
            {
                try { Control.UnRegisterEventClient(EventHandler); }
                catch { }
            }
            Control.Dispose();
        }
    }

    private abstract record SessionEvent;
    private sealed record SessionCreatedEvent(AudioSessionControl Control) : SessionEvent;
    private sealed record TrackedSessionEvent(TrackedSession Session, AudioSessionEvent Event) : SessionEvent;

    private abstract record AudioSessionEvent;
    private sealed record VolumeChangedEvent(float Volume, bool IsMuted) : AudioSessionEvent;
    private sealed record DisplayNameChangedEvent(string DisplayName) : AudioSessionEvent;
    private sealed record StateChangedEvent(AudioSessionState State) : AudioSessionEvent;
    private sealed record DisconnectedEvent : AudioSessionEvent;
    private sealed record RefreshVolumeEvent : AudioSessionEvent;

    private sealed class SessionEventsHandler(Action<AudioSessionEvent> enqueue) : IAudioSessionEventsHandler
    {
        public void OnVolumeChanged(float volume, bool isMuted) => enqueue(new VolumeChangedEvent(volume, isMuted));
        public void OnDisplayNameChanged(string displayName) => enqueue(new DisplayNameChangedEvent(displayName));
        public void OnIconPathChanged(string iconPath) { }
        public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex) => enqueue(new RefreshVolumeEvent());
        public void OnGroupingParamChanged(ref Guid groupingId) { }
        public void OnStateChanged(AudioSessionState state) => enqueue(new StateChangedEvent(state));
        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) => enqueue(new DisconnectedEvent());
    }

    private sealed class DeviceNotificationClient(Action sessionTopologyChanged, Action deviceListChanged) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => sessionTopologyChanged();
        public void OnDeviceAdded(string pwstrDeviceId) => sessionTopologyChanged();
        public void OnDeviceRemoved(string deviceId) => sessionTopologyChanged();

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow is DataFlow.Render or DataFlow.All) sessionTopologyChanged();
        }

        // Friendly-name / property churn should refresh the device picker only.
        // Rebuilding every session monitor here caused offline flashes.
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) => deviceListChanged();
    }
}
