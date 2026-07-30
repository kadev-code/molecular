using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using Molecular.App.Media;
using Molecular.Core.Audio;
using Molecular.Core.Models;
using Molecular.Core.Persistence;
using Molecular.Core.Safety;

namespace Molecular.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IAudioSessionService _audio;
    private readonly ProfileStore _store;
    private readonly MixerProfile _profile;
    private readonly SafetyEngine _safety;
    private readonly WindowsMediaSessionService _media = new();
    private readonly HashSet<string> _liveOnPreviousTick = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _muteStatesBeforeGlobalMute = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _pendingMuteRestores = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _undoTimer;
    private DateTime _lastTick = DateTime.UtcNow;
    private string _statusMessage = "Inicializando áudio do Windows";
    private AudioOutputDevice? _selectedOutputDevice;
    private bool _isGlobalMuted;
    private bool _disposed;
    private int _tickCount;
    private int _isTicking;
    private int _outputDeviceRefreshRequested = 1;
    private int _quickPageIndex;
    private int _quickPageSize = 4;
    private bool _showInactiveQuickChannels;
    private bool _showHiddenQuickChannels;
    private bool _isAddChannelPickerOpen;
    private bool _isResolvingAssignments;
    private bool _isUndoVisible;
    private string _undoMessage = string.Empty;
    private RemovedChannelState? _pendingRemoval;

    public MainViewModel(IAudioSessionService audio, ProfileStore store)
    {
        _audio = audio;
        _audio.OutputDevicesChanged += OnOutputDevicesChanged;
        _store = store;
        _profile = store.Load();
        _safety = new SafetyEngine(_profile.Safety);
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(550)
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };
        _undoTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(8)
        };
        _undoTimer.Tick += (_, _) => DismissUndo();
        Applications = new ObservableCollection<AudioApplicationViewModel>();
        OutputDevices = new ObservableCollection<AudioOutputDevice>();
        AllChannels = new ObservableCollection<ChannelViewModel>();
        QuickChannels = new ObservableCollection<ChannelViewModel>();
        VisibleQuickChannels = new ObservableCollection<ChannelViewModel>();
        ExpandedChannels = new ObservableCollection<ChannelViewModel>();
        AddableApplications = new ObservableCollection<AudioApplicationViewModel>();

        CollapseAllCommand = new RelayCommand(CollapseAll);
        ToggleGlobalMuteCommand = new RelayCommand(ToggleGlobalMute);
        AddChannelCommand = new RelayCommand(OpenAddChannelPicker);
        CloseAddChannelPickerCommand = new RelayCommand(() => IsAddChannelPickerOpen = false);
        AssignApplicationCommand = new RelayCommand<AudioApplicationViewModel>(AddApplication);
        PreviousQuickPageCommand = new RelayCommand(ShowPreviousQuickPage, () => _quickPageIndex > 0);
        NextQuickPageCommand = new RelayCommand(ShowNextQuickPage, () => _quickPageIndex + 1 < QuickPageCount);
        ToggleInactiveQuickChannelsCommand = new RelayCommand(
            ToggleInactiveQuickChannels,
            () => ShowInactiveQuickChannels || InactiveQuickChannelCount > 0);
        ToggleHiddenQuickChannelsCommand = new RelayCommand(
            ToggleHiddenQuickChannels,
            () => ShowHiddenQuickChannels || HiddenQuickChannelCount > 0);
        UndoRemoveCommand = new RelayCommand(UndoRemove, () => _pendingRemoval is not null);

        foreach (var binding in _profile.Channels.OrderBy(channel => channel.Order).ThenBy(channel => channel.Index))
        {
            var channel = CreateChannelViewModel(binding);
            SynchronizeViewCollections(channel);
        }

        RefreshQuickPage();
        RefreshAssignmentOptions();
        RefreshChannelVolumeLimits();
        // Persist schema migrations (including duplicate-assignment cleanup)
        // shortly after startup instead of waiting for the application to close.
        Save();
    }

    public ObservableCollection<AudioApplicationViewModel> Applications { get; }
    public ObservableCollection<AudioOutputDevice> OutputDevices { get; }
    public ObservableCollection<ChannelViewModel> AllChannels { get; }
    public ObservableCollection<ChannelViewModel> QuickChannels { get; }
    public ObservableCollection<ChannelViewModel> VisibleQuickChannels { get; }
    public ObservableCollection<ChannelViewModel> ExpandedChannels { get; }
    public ObservableCollection<AudioApplicationViewModel> AddableApplications { get; }
    public RelayCommand CollapseAllCommand { get; }
    public RelayCommand ToggleGlobalMuteCommand { get; }
    public RelayCommand AddChannelCommand { get; }
    public RelayCommand CloseAddChannelPickerCommand { get; }
    public RelayCommand<AudioApplicationViewModel> AssignApplicationCommand { get; }
    public RelayCommand PreviousQuickPageCommand { get; }
    public RelayCommand NextQuickPageCommand { get; }
    public RelayCommand ToggleInactiveQuickChannelsCommand { get; }
    public RelayCommand ToggleHiddenQuickChannelsCommand { get; }
    public RelayCommand UndoRemoveCommand { get; }
    public string ProfileName => _profile.Name;
    public string ExpandedCountText => $"{ExpandedChannels.Count} {(ExpandedChannels.Count == 1 ? "canal ativo" : "canais ativos")}";
    public string QuickPageText => QuickPageCount > 1 ? $"{_quickPageIndex + 1} / {QuickPageCount}" : string.Empty;
    public bool ShowInactiveQuickChannels => _showInactiveQuickChannels;
    public bool ShowHiddenQuickChannels => _showHiddenQuickChannels;
    public bool IsAddChannelPickerOpen { get => _isAddChannelPickerOpen; set => SetProperty(ref _isAddChannelPickerOpen, value); }
    public int InactiveQuickChannelCount => QuickChannels.Count(channel => !channel.IsHidden && !channel.IsOnline);
    public int HiddenQuickChannelCount => QuickChannels.Count(channel => channel.IsHidden);
    public string QuickInactiveButtonText => ShowInactiveQuickChannels
        ? "OCULTAR INATIVOS"
        : $"INATIVOS ({InactiveQuickChannelCount})";
    public string QuickHiddenButtonText => ShowHiddenQuickChannels
        ? "OCULTAR LISTA"
        : $"OCULTOS ({HiddenQuickChannelCount})";
    public string AddChannelPickerSummary => AddableApplications.Count == 0
        ? "Nenhum aplicativo novo foi detectado. Inicie a reproducao de audio e tente novamente."
        : "Escolha o aplicativo que deseja adicionar ao mixer.";
    public string DetectedApplicationsText => $"{Applications.Count} {(Applications.Count == 1 ? "aplicativo de áudio detectado" : "aplicativos de áudio detectados")}";
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool IsUndoVisible { get => _isUndoVisible; private set => SetProperty(ref _isUndoVisible, value); }
    public string UndoMessage { get => _undoMessage; private set => SetProperty(ref _undoMessage, value); }
    public string GlobalMuteButtonText => IsGlobalMuted ? "RESTAURAR ÁUDIO" : "SILENCIAR TUDO";
    public bool IsGlobalMuted { get => _isGlobalMuted; private set { if (SetProperty(ref _isGlobalMuted, value)) OnPropertyChanged(nameof(GlobalMuteButtonText)); } }

    public AudioOutputDevice? SelectedOutputDevice
    {
        get => _selectedOutputDevice;
        set
        {
            if (!SetProperty(ref _selectedOutputDevice, value) || value is null) return;
            _audio.SelectOutputDevice(value.Id);
            _liveOnPreviousTick.Clear();
            StatusMessage = $"Monitorando {value.Name}";
        }
    }

    public bool IsSafetyEnabled
    {
        get => _profile.Safety.Enabled;
        set
        {
            if (_profile.Safety.Enabled == value) return;
            _profile.Safety.Enabled = value;
            RefreshChannelVolumeLimits();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SafetySummary));
            Save();
        }
    }

    public double GlobalCeiling
    {
        get => _profile.Safety.GlobalCeiling;
        set
        {
            var clamped = Math.Clamp(value, 1, 100);
            if (Math.Abs(_profile.Safety.GlobalCeiling - clamped) < 0.01) return;
            _profile.Safety.GlobalCeiling = clamped;
            RefreshChannelVolumeLimits();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SafetySummary));
            Save();
        }
    }

    public double NewSessionVolume
    {
        get => _profile.Safety.NewSessionVolume;
        set
        {
            var clamped = Math.Clamp(value, 0, GlobalCeiling);
            if (Math.Abs(_profile.Safety.NewSessionVolume - clamped) < 0.01) return;
            _profile.Safety.NewSessionVolume = clamped;
            OnPropertyChanged();
            Save();
        }
    }

    public string SafetySummary => IsSafetyEnabled ? $"ATIVA · TETO {GlobalCeiling:0}%" : "DESATIVADA";

    public void UpdateViewportWidth(double windowWidth)
    {
        var nextPageSize = windowWidth < 1180 ? 2 : windowWidth < 1380 ? 3 : 4;
        if (_quickPageSize == nextPageSize) return;
        _quickPageSize = nextPageSize;
        _quickPageIndex = 0;
        RefreshQuickPage();
    }

    public async Task TickAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _isTicking, 1) == 1) return;

        try
        {
            var tickNumber = _tickCount++;
            var includeDevices = Interlocked.Exchange(ref _outputDeviceRefreshRequested, 0) == 1;
            var includeMedia = tickNumber % 5 == 0;
            var poll = await Task.Run(() => new AudioPoll(
                _audio.ReadApplications(),
                includeDevices ? _audio.ReadOutputDevices() : null));
            var mediaSessions = includeMedia
                ? await _media.ReadSessionsAsync()
                : null;
            if (_disposed) return;

            if (poll.OutputDevices is not null) SynchronizeOutputDevices(poll.OutputDevices);
            var now = DateTime.UtcNow;
            var elapsed = now - _lastTick;
            _lastTick = now;
            var snapshots = poll.Applications;
            SynchronizeApplications(snapshots);
            var changes = new List<AudioSessionChange>();

            foreach (var snapshot in snapshots.Where(item => !_liveOnPreviousTick.Contains(item.Key)))
            {
                var binding = _profile.Channels.FirstOrDefault(channel =>
                    string.Equals(channel.ApplicationKey, snapshot.Key, StringComparison.OrdinalIgnoreCase));
                var safeVolume = _safety.SafeInitialVolume(binding?.TargetVolume);
                if (snapshot.Volume > safeVolume || binding is not null)
                    changes.Add(new AudioSessionChange(snapshot.Key, VolumePercent: safeVolume));
            }

            _liveOnPreviousTick.Clear();
            foreach (var key in snapshots.Select(item => item.Key)) _liveOnPreviousTick.Add(key);

            foreach (var restore in _pendingMuteRestores)
                changes.Add(new AudioSessionChange(restore.Key, IsMuted: restore.Value));
            _pendingMuteRestores.Clear();

            var anySolo = AllChannels.Any(channel => channel.IsSolo && channel.ApplicationKey is not null);
            foreach (var channel in AllChannels)
            {
                var application = Applications.FirstOrDefault(item =>
                    string.Equals(item.Key, channel.ApplicationKey, StringComparison.OrdinalIgnoreCase));
                if (application is null || channel.ApplicationKey is null)
                {
                    channel.Sync(null, 0, 0);
                    continue;
                }

                var requested = _safety.Clamp(channel.TargetVolume, 100);
                var next = application.Volume > requested
                    ? requested
                    : _safety.StepToward(application.Volume, requested, elapsed, 100);

                // Core Audio may report a zero-volume session as muted. Treat zero as
                // an intentional silent state so the 100 ms loop does not repeatedly
                // toggle the Windows mute flag. Raising the fader above zero naturally
                // clears this implicit mute again.
                var shouldMute = requested <= 0.01
                    || IsGlobalMuted
                    || channel.IsMuted
                    || (anySolo && !channel.IsSolo);
                double? volumeChange = Math.Abs(application.Volume - next) >= 0.2 ? next : null;
                bool? muteChange = application.IsMuted != shouldMute ? shouldMute : null;
                if (volumeChange.HasValue || muteChange.HasValue)
                    changes.Add(new AudioSessionChange(channel.ApplicationKey, volumeChange, muteChange));
                // IAudioMeterInformation exposes the session signal before the
                // Molecular fader. Display the effective output level so a loud
                // source at 2% no longer appears to be clipping near 0 dBFS.
                var effectivePeak = shouldMute ? 0 : application.Peak * (next / 100d);
                channel.Sync(application, next, effectivePeak);
            }

            if (mediaSessions is not null)
            {
                foreach (var channel in AllChannels.Where(channel => channel.IsOnline))
                {
                    channel.SyncMedia(WindowsMediaSessionService.FindForApplication(
                        mediaSessions,
                        channel.ExecutableName,
                        channel.ApplicationName));
                }
            }

            if (IsGlobalMuted)
            {
                foreach (var application in Applications.Where(application =>
                             !AllChannels.Any(channel => string.Equals(channel.ApplicationKey, application.Key, StringComparison.OrdinalIgnoreCase))))
                {
                    if (!application.IsMuted) changes.Add(new AudioSessionChange(application.Key, IsMuted: true));
                }
            }

            StatusMessage = snapshots.Count == 0
                ? "Aguardando sessões de áudio"
                : "Sistema de áudio ativo";

            if (changes.Count > 0)
                await Task.Run(() => _audio.ApplyChanges(changes));
        }
        catch (Exception exception)
        {
            StatusMessage = $"Serviço de áudio indisponível: {exception.Message}";
        }
        finally
        {
            Volatile.Write(ref _isTicking, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveTimer.Stop();
        _undoTimer.Stop();
        SaveNow();
        foreach (var channel in AllChannels) channel.PropertyChanged -= OnChannelPropertyChanged;
        _audio.OutputDevicesChanged -= OnOutputDevicesChanged;
        _audio.Dispose();
    }

    private void OnOutputDevicesChanged(object? sender, EventArgs eventArgs) =>
        Interlocked.Exchange(ref _outputDeviceRefreshRequested, 1);

    private void SynchronizeOutputDevices(IReadOnlyList<AudioOutputDevice> devices)
    {
        if (OutputDevices.SequenceEqual(devices)) return;

        var selectedId = SelectedOutputDevice?.Id;
        OutputDevices.Clear();
        foreach (var device in devices) OutputDevices.Add(device);
        SelectedOutputDevice = OutputDevices.FirstOrDefault(device => device.Id == selectedId)
            ?? OutputDevices.FirstOrDefault(device => device.IsDefault)
            ?? OutputDevices.FirstOrDefault();
    }

    private void SynchronizeApplications(IReadOnlyList<AudioApplication> snapshots)
    {
        var liveKeys = snapshots.Select(snapshot => snapshot.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var collectionChanged = false;
        foreach (var stale in Applications.Where(item => !liveKeys.Contains(item.Key)).ToArray())
        {
            Applications.Remove(stale);
            collectionChanged = true;
        }

        foreach (var snapshot in snapshots)
        {
            var existing = Applications.FirstOrDefault(item => string.Equals(item.Key, snapshot.Key, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                Applications.Add(new AudioApplicationViewModel(snapshot));
                collectionChanged = true;
            }
            else existing.Update(snapshot);
        }

        if (collectionChanged)
        {
            OnPropertyChanged(nameof(DetectedApplicationsText));
            RefreshAddableApplications();
            RefreshAssignmentOptions();
        }
    }

    private void OnViewModeChanged(ChannelViewModel channel)
    {
        SynchronizeViewCollections(channel);
        OnPropertyChanged(nameof(ExpandedCountText));
    }

    private void SynchronizeViewCollections(ChannelViewModel channel)
    {
        var quickMembershipChanged = false;

        if (channel.IsAssigned)
        {
            quickMembershipChanged = InsertOrdered(QuickChannels, channel);
            if (channel.IsExpanded) InsertOrdered(ExpandedChannels, channel);
            else ExpandedChannels.Remove(channel);
        }
        else
        {
            quickMembershipChanged = QuickChannels.Remove(channel);
            ExpandedChannels.Remove(channel);
        }

        // Rebuilding VisibleQuickChannels recreates every quick-card control. Doing
        // that for each TargetVolume update destroys the Slider while its thumb is
        // being dragged, which makes it jump to zero or stop responding.
        if (quickMembershipChanged) RefreshQuickPage();
    }

    private void OnChannelChanged(ChannelViewModel channel)
    {
        if (!_isResolvingAssignments && channel.ApplicationKey is not null)
        {
            _isResolvingAssignments = true;
            try
            {
                foreach (var duplicate in AllChannels.Where(other =>
                             !ReferenceEquals(other, channel)
                             && string.Equals(other.ApplicationKey, channel.ApplicationKey, StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    duplicate.ClearAssignment();
                }
            }
            finally
            {
                _isResolvingAssignments = false;
            }
        }

        SynchronizeViewCollections(channel);

        OnPropertyChanged(nameof(ExpandedCountText));
        RefreshAddableApplications();
        RefreshAssignmentOptions();
        Save();
    }

    private async void OnMediaTransport(ChannelViewModel channel, MediaTransportAction action)
    {
        var succeeded = await _media.ExecuteAsync(channel.ExecutableName, channel.ApplicationName, action);
        if (!succeeded)
        {
            StatusMessage = $"{channel.ApplicationName} não disponibilizou esse controle de mídia";
            return;
        }

        await Task.Delay(120);
        await TickAsync();
    }

    private void CollapseAll()
    {
        foreach (var channel in ExpandedChannels.ToArray()) channel.SetExpanded(false);
    }

    private int QuickPageCount => Math.Max(1, (FilteredQuickChannels.Count + _quickPageSize - 1) / _quickPageSize);

    private IReadOnlyList<ChannelViewModel> FilteredQuickChannels => QuickChannels
        .Where(channel => ShowHiddenQuickChannels ? channel.IsHidden : !channel.IsHidden)
        .Where(channel => ShowHiddenQuickChannels || ShowInactiveQuickChannels || channel.IsOnline)
        .ToArray();

    private void ShowPreviousQuickPage()
    {
        if (_quickPageIndex == 0) return;
        _quickPageIndex--;
        RefreshQuickPage();
    }

    private void ShowNextQuickPage()
    {
        if (_quickPageIndex + 1 >= QuickPageCount) return;
        _quickPageIndex++;
        RefreshQuickPage();
    }

    private void ToggleInactiveQuickChannels()
    {
        _showInactiveQuickChannels = !_showInactiveQuickChannels;
        _quickPageIndex = 0;
        OnPropertyChanged(nameof(ShowInactiveQuickChannels));
        RefreshQuickPage();
    }

    private void ToggleHiddenQuickChannels()
    {
        _showHiddenQuickChannels = !_showHiddenQuickChannels;
        _quickPageIndex = 0;
        OnPropertyChanged(nameof(ShowHiddenQuickChannels));
        RefreshQuickPage();
    }

    private void RefreshQuickPage()
    {
        var filteredChannels = FilteredQuickChannels;
        _quickPageIndex = Math.Clamp(_quickPageIndex, 0, QuickPageCount - 1);
        VisibleQuickChannels.Clear();
        foreach (var channel in filteredChannels.Skip(_quickPageIndex * _quickPageSize).Take(_quickPageSize))
            VisibleQuickChannels.Add(channel);

        OnPropertyChanged(nameof(QuickPageText));
        OnPropertyChanged(nameof(InactiveQuickChannelCount));
        OnPropertyChanged(nameof(QuickInactiveButtonText));
        OnPropertyChanged(nameof(HiddenQuickChannelCount));
        OnPropertyChanged(nameof(QuickHiddenButtonText));
        PreviousQuickPageCommand.RaiseCanExecuteChanged();
        NextQuickPageCommand.RaiseCanExecuteChanged();
        ToggleInactiveQuickChannelsCommand.RaiseCanExecuteChanged();
        ToggleHiddenQuickChannelsCommand.RaiseCanExecuteChanged();
    }

    private void OnChannelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(ChannelViewModel.IsOnline) or nameof(ChannelViewModel.IsHidden))
            RefreshQuickPage();
    }

    private static bool InsertOrdered(ObservableCollection<ChannelViewModel> target, ChannelViewModel channel)
    {
        if (target.Contains(channel)) return false;
        var insertionIndex = target.TakeWhile(existing => existing.Order <= channel.Order).Count();
        target.Insert(insertionIndex, channel);
        return true;
    }

    private void ToggleGlobalMute()
    {
        if (!IsGlobalMuted)
        {
            _muteStatesBeforeGlobalMute.Clear();
            foreach (var application in Applications)
            {
                _muteStatesBeforeGlobalMute[application.Key] = application.IsMuted;
            }
            IsGlobalMuted = true;
        }
        else
        {
            IsGlobalMuted = false;
            foreach (var application in Applications)
            {
                _pendingMuteRestores[application.Key] = _muteStatesBeforeGlobalMute.GetValueOrDefault(application.Key);
            }
            _muteStatesBeforeGlobalMute.Clear();
        }

        StatusMessage = IsGlobalMuted ? "Todos os canais estão silenciados" : "Áudio restaurado";
    }

    public void SetGlobalMute(bool muted)
    {
        if (IsGlobalMuted == muted) return;
        ToggleGlobalMute();
    }

    private ChannelViewModel CreateChannelViewModel(ChannelBinding binding)
    {
        var channel = new ChannelViewModel(binding, Applications, OnChannelChanged, OnViewModeChanged, OnMediaTransport, RemoveChannel);
        channel.PropertyChanged += OnChannelPropertyChanged;
        InsertOrdered(AllChannels, channel);
        channel.SetMaximumVolume(_safety.Clamp(100));
        return channel;
    }

    private void RemoveChannel(ChannelViewModel channel)
    {
        if (!AllChannels.Contains(channel) || !MixerChannelRegistry.Remove(_profile, channel.Binding)) return;

        _undoTimer.Stop();
        _pendingRemoval = new RemovedChannelState(channel.Binding);
        channel.PropertyChanged -= OnChannelPropertyChanged;
        AllChannels.Remove(channel);
        QuickChannels.Remove(channel);
        ExpandedChannels.Remove(channel);
        VisibleQuickChannels.Remove(channel);
        RefreshQuickPage();
        OnPropertyChanged(nameof(ExpandedCountText));
        RefreshAddableApplications();
        RefreshAssignmentOptions();
        Save();

        UndoMessage = $"Canal {channel.ApplicationName} removido";
        IsUndoVisible = true;
        UndoRemoveCommand.RaiseCanExecuteChanged();
        _undoTimer.Start();
    }

    private void UndoRemove()
    {
        var removal = _pendingRemoval;
        if (removal is null || !MixerChannelRegistry.Restore(_profile, removal.Binding))
        {
            DismissUndo();
            return;
        }

        var channel = CreateChannelViewModel(removal.Binding);
        SynchronizeViewCollections(channel);
        RefreshQuickPage();
        OnPropertyChanged(nameof(ExpandedCountText));
        RefreshAddableApplications();
        RefreshAssignmentOptions();
        Save();
        StatusMessage = $"Canal {channel.ApplicationName} restaurado";
        DismissUndo();
    }

    private void DismissUndo()
    {
        _undoTimer.Stop();
        _pendingRemoval = null;
        IsUndoVisible = false;
        UndoRemoveCommand.RaiseCanExecuteChanged();
    }

    private void RefreshChannelVolumeLimits()
    {
        foreach (var channel in AllChannels)
            channel.SetMaximumVolume(_safety.Clamp(100));
    }

    private void OpenAddChannelPicker()
    {
        RefreshAddableApplications();
        IsAddChannelPickerOpen = true;
    }

    private void RefreshAddableApplications()
    {
        var assignedKeys = AllChannels.Where(channel => channel.ApplicationKey is not null)
            .Select(channel => channel.ApplicationKey!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var available = Applications.Where(application => !assignedKeys.Contains(application.Key)).ToArray();
        if (AddableApplications.SequenceEqual(available)) return;

        AddableApplications.Clear();
        foreach (var application in available) AddableApplications.Add(application);
        OnPropertyChanged(nameof(AddChannelPickerSummary));
    }

    private void RefreshAssignmentOptions()
    {
        var assignedKeys = AllChannels
            .Where(channel => channel.ApplicationKey is not null)
            .Select(channel => channel.ApplicationKey!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var channel in AllChannels)
        {
            var options = Applications.Where(application =>
                    string.Equals(application.Key, channel.ApplicationKey, StringComparison.OrdinalIgnoreCase)
                    || !assignedKeys.Contains(application.Key))
                .ToArray();
            if (channel.AssignmentOptions.SequenceEqual(options)) continue;

            channel.AssignmentOptions.Clear();
            foreach (var option in options) channel.AssignmentOptions.Add(option);
        }
    }

    private void AddApplication(AudioApplicationViewModel application)
    {
        if (!AddableApplications.Contains(application)
            || MixerChannelRegistry.ContainsApplication(_profile, application.Key))
        {
            StatusMessage = "Esse aplicativo já possui um canal";
            RefreshAddableApplications();
            return;
        }

        var initialVolume = _safety.SafeInitialVolume();
        var binding = MixerChannelRegistry.Add(
            _profile,
            application.Key,
            application.DisplayName,
            application.ExecutableName,
            application.ExecutablePath,
            initialVolume);
        var channel = CreateChannelViewModel(binding);
        channel.Sync(application, initialVolume, application.Peak * initialVolume / 100d);
        SynchronizeViewCollections(channel);
        OnPropertyChanged(nameof(ExpandedCountText));
        IsAddChannelPickerOpen = false;
        RefreshAddableApplications();
        RefreshAssignmentOptions();
        Save();

        StatusMessage = $"{application.DisplayName} adicionado ao mixer";
    }

    private void Save()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        try { _store.Save(_profile); }
        catch (Exception exception) { StatusMessage = $"Não foi possível salvar o perfil: {exception.Message}"; }
    }

    private sealed record AudioPoll(
        IReadOnlyList<AudioApplication> Applications,
        IReadOnlyList<AudioOutputDevice>? OutputDevices);

    private sealed record RemovedChannelState(ChannelBinding Binding);
}
