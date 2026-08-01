using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using Molecular.App.Media;
using Molecular.App.Runtime;
using Molecular.Core.Audio;
using Molecular.Core.Diagnostics;
using Molecular.Core.Models;
using Molecular.Core.Persistence;
using Molecular.Core.Safety;

namespace Molecular.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IAudioSessionService _audio;
    private readonly ProfileStore _store;
    private readonly AppSettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly ProfileCatalog _catalog;
    private MixerProfile _profile;
    private SafetyEngine _safety;
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
    private bool _startWithWindows;
    private int _audioRetryAttempt;
    private DateTime _nextAudioRetryAt = DateTime.MinValue;
    private bool _isReconnectingAudio;
    private double _dpiScalePercent = 100;
    private bool _backgroundMode;
    private string? _autoActivatedProfileId;
    private readonly BoundProfileActivationGate _boundProfileActivationGate = new();
    private ProfileListItem? _selectedProfileItem;
    private ProfileBindOption? _selectedBindOption;
    private bool _isProfileFlyoutOpen;
    private string _channelFilter = string.Empty;

    public MainViewModel(IAudioSessionService audio, ProfileStore store, AppSettingsStore? settingsStore = null)
    {
        _audio = audio;
        _audio.OutputDevicesChanged += OnOutputDevicesChanged;
        _store = store;
        _settingsStore = settingsStore ?? new AppSettingsStore();
        _settings = _settingsStore.Load();
        var load = store.LoadDetailed();
        _catalog = load.Catalog;
        _profile = _catalog.ActiveProfileOrNull ?? CreateProfilelessWorkspace();
        if (!string.IsNullOrWhiteSpace(load.Notice))
            _statusMessage = load.Notice!;
        _startWithWindows = WindowsStartupSettings.IsEnabled();
        _safety = new SafetyEngine(_profile.Safety);
        OperationalLog.Shared.Info("app", "MainViewModel inicializado");
        if (load.RecoveredFromBackup)
            OperationalLog.Shared.Warn("profile", "Perfil restaurado a partir do backup");
        else if (load.ResetToDefault)
            OperationalLog.Shared.Error("profile", "Perfil corrompido; mixer reiniciado vazio");
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
        DisplayedExpandedChannels = new ObservableCollection<ChannelViewModel>();
        AddableApplications = new ObservableCollection<AudioApplicationViewModel>();
        AvailableProfiles = new ObservableCollection<ProfileListItem>();
        BindOptions = new ObservableCollection<ProfileBindOption>();

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
        ExportDiagnosticsCommand = new RelayCommand(ExportDiagnostics);
        ExportProfileCommand = new RelayCommand(ExportActiveProfile);
        ImportProfileCommand = new RelayCommand(ImportProfile);
        ToggleProfileFlyoutCommand = new RelayCommand(() => IsProfileFlyoutOpen = !IsProfileFlyoutOpen);
        CreateProfileCommand = new RelayCommand(CreateProfile);
        DuplicateProfileCommand = new RelayCommand(DuplicateActiveProfile);
        DeleteProfileCommand = new RelayCommand(DeleteActiveProfile, () => !IsProfileless);
        SetDefaultProfileCommand = new RelayCommand(SetActiveAsDefault, () => !IsProfileless && !IsActiveProfileDefault);

        RebuildChannelsFromActiveProfile();
        RefreshProfileList();
        RefreshBindOptions();
        RefreshQuickPage();
        RefreshAssignmentOptions();
        RefreshChannelVolumeLimits();
        DeleteProfileCommand.RaiseCanExecuteChanged();
        SetDefaultProfileCommand.RaiseCanExecuteChanged();
        _audio.SelectOutputDevice(
            _settings.PreferredOutputDeviceId,
            _settings.PreferSystemDefaultFallback);
        Save();
    }

    public event EventHandler? MeterIntervalChanged;

    public ObservableCollection<AudioApplicationViewModel> Applications { get; }
    public ObservableCollection<AudioOutputDevice> OutputDevices { get; }
    public ObservableCollection<ChannelViewModel> AllChannels { get; }
    public ObservableCollection<ChannelViewModel> QuickChannels { get; }
    public ObservableCollection<ChannelViewModel> VisibleQuickChannels { get; }
    public ObservableCollection<ChannelViewModel> ExpandedChannels { get; }
    public ObservableCollection<ChannelViewModel> DisplayedExpandedChannels { get; }
    public ObservableCollection<AudioApplicationViewModel> AddableApplications { get; }
    public ObservableCollection<ProfileListItem> AvailableProfiles { get; }
    public ObservableCollection<ProfileBindOption> BindOptions { get; }
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
    public RelayCommand ExportDiagnosticsCommand { get; }
    public RelayCommand ExportProfileCommand { get; }
    public RelayCommand ImportProfileCommand { get; }
    public RelayCommand ToggleProfileFlyoutCommand { get; }
    public RelayCommand CreateProfileCommand { get; }
    public RelayCommand DuplicateProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public RelayCommand SetDefaultProfileCommand { get; }

    public string ProfileName
    {
        get => IsProfileless ? "Sem perfil" : _profile.Name;
        set
        {
            if (IsProfileless) return;
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0 || string.Equals(_profile.Name, trimmed, StringComparison.Ordinal)) return;
            _profile.Name = trimmed;
            OnPropertyChanged();
            RefreshProfileList();
            Save();
        }
    }

    public bool IsProfileFlyoutOpen
    {
        get => _isProfileFlyoutOpen;
        set => SetProperty(ref _isProfileFlyoutOpen, value);
    }

    public bool IsProfileless => _catalog.FindById(_profile.Id) is null;

    public bool CanManageActiveProfile => !IsProfileless;

    public bool IsActiveProfileDefault =>
        !IsProfileless
        && string.Equals(_profile.Id, _catalog.DefaultProfileId, StringComparison.OrdinalIgnoreCase);

    public string ProfileBindSummary => IsProfileless
        ? "Operando sem perfil"
        : string.IsNullOrWhiteSpace(_profile.BoundApplicationKey)
            ? "Sem app atrelado"
            : $"Atrelado a {_profile.BoundApplicationName ?? _profile.BoundApplicationKey}";

    public bool AutoDiscoverChannels
    {
        get => !IsProfileless && _profile.AutoDiscoverChannels;
        set
        {
            if (IsProfileless) return;
            if (_profile.AutoDiscoverChannels == value) return;
            _profile.AutoDiscoverChannels = value;
            OnPropertyChanged();
            if (value)
            {
                DiscoverMissingLiveChannels();
                StatusMessage = $"Busca automática ativa em '{_profile.Name}'";
            }
            else
            {
                StatusMessage = $"Busca automática desativada em '{_profile.Name}'";
            }

            Save();
        }
    }

    public string ChannelFilter
    {
        get => _channelFilter;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(_channelFilter, next, StringComparison.Ordinal)) return;
            _channelFilter = next;
            OnPropertyChanged();
            _quickPageIndex = 0;
            RefreshQuickPage();
            RefreshExpandedFilter();
        }
    }

    public bool HasChannelFilter => !string.IsNullOrWhiteSpace(_channelFilter);

    public ProfileListItem? SelectedProfileItem
    {
        get => _selectedProfileItem;
        set
        {
            if (ReferenceEquals(_selectedProfileItem, value)) return;
            _selectedProfileItem = value;
            OnPropertyChanged();
            if (value is null) return;
            if (string.IsNullOrWhiteSpace(value.Id))
            {
                if (!IsProfileless)
                    EnterProfilelessMode("Operando sem perfil");
                return;
            }

            if (string.Equals(value.Id, _profile.Id, StringComparison.OrdinalIgnoreCase))
                return;
            ActivateProfile(value.Id, autoActivated: false, $"Perfil '{value.Name}' ativado");
        }
    }

    public ProfileBindOption? SelectedBindOption
    {
        get => _selectedBindOption;
        set
        {
            if (ReferenceEquals(_selectedBindOption, value)) return;
            _selectedBindOption = value;
            OnPropertyChanged();
            if (value is null || IsProfileless) return;
            _profile.BoundApplicationKey = value.ApplicationKey;
            _profile.BoundApplicationName = value.DisplayName;
            OnPropertyChanged(nameof(ProfileBindSummary));
            StatusMessage = value.ApplicationKey is null
                ? "Perfil sem aplicativo atrelado"
                : $"Perfil atrelado a {value.DisplayName}";
            Save();
        }
    }

    public string ExpandedCountText => $"{ExpandedChannels.Count} {(ExpandedChannels.Count == 1 ? "canal ativo" : "canais ativos")}";
    public string QuickPageText => QuickPageCount > 1 ? $"{_quickPageIndex + 1} / {QuickPageCount}" : string.Empty;
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows == value) return;
            try
            {
                WindowsStartupSettings.SetEnabled(value);
                _startWithWindows = value;
                OnPropertyChanged();
                StatusMessage = value
                    ? "Molecular iniciará com o Windows"
                    : "Inicialização com o Windows desativada";
            }
            catch (Exception exception)
            {
                StatusMessage = $"Não foi possível alterar a inicialização: {exception.Message}";
                OnPropertyChanged();
            }
        }
    }

    public bool CloseToTray
    {
        get => _settings.CloseToTray;
        set
        {
            if (_settings.CloseToTray == value) return;
            _settings.CloseToTray = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CloseButtonToolTip));
            PersistSettings();
            StatusMessage = value
                ? "Fechar oculta o Molecular na bandeja"
                : "Fechar encerra o Molecular";
        }
    }

    public bool StartInTray
    {
        get => _settings.StartInTray;
        set
        {
            if (_settings.StartInTray == value) return;
            _settings.StartInTray = value;
            OnPropertyChanged();
            PersistSettings();
            StatusMessage = value
                ? "Molecular abrirá na bandeja"
                : "Molecular abrirá com a janela visível";
        }
    }

    public bool PreferSystemDefaultFallback
    {
        get => _settings.PreferSystemDefaultFallback;
        set
        {
            if (_settings.PreferSystemDefaultFallback == value) return;
            _settings.PreferSystemDefaultFallback = value;
            OnPropertyChanged();
            PersistSettings();
            _audio.SelectOutputDevice(_settings.PreferredOutputDeviceId, value);
            _audio.RequestSessionRebuild();
            _liveOnPreviousTick.Clear();
            Interlocked.Exchange(ref _outputDeviceRefreshRequested, 1);
        }
    }

    public IReadOnlyList<MeterIntervalOption> MeterIntervalOptions => AppSettingsStore.MeterIntervalOptions;

    public MeterIntervalOption SelectedMeterInterval
    {
        get => MeterIntervalOptions.FirstOrDefault(option => option.IntervalMs == _settings.MeterIntervalMs)
            ?? MeterIntervalOptions[1];
        set
        {
            if (value is null || _settings.MeterIntervalMs == value.IntervalMs) return;
            _settings.MeterIntervalMs = value.IntervalMs;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MeterPerformanceSummary));
            PersistSettings();
            MeterIntervalChanged?.Invoke(this, EventArgs.Empty);
            StatusMessage = $"Medidores em {value.DisplayName}";
        }
    }

    public int MeterIntervalMs => _settings.MeterIntervalMs;

    public string MeterPerformanceSummary =>
        $"Medidores a cada {_settings.MeterIntervalMs} ms · bandeja em 1 Hz";

    public string CloseButtonToolTip => CloseToTray ? "Ocultar na bandeja" : "Sair do Molecular";

    public bool ShouldStartInTray => _settings.StartInTray;
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
            _audio.SelectOutputDevice(value.Id, _settings.PreferSystemDefaultFallback);
            if (!string.Equals(_settings.PreferredOutputDeviceId, value.Id, StringComparison.OrdinalIgnoreCase))
            {
                _settings.PreferredOutputDeviceId = value.Id;
                PersistSettings();
            }

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

    public void UpdateDpiScale(double dpiScaleX) =>
        _dpiScalePercent = Math.Round(Math.Max(0.5, dpiScaleX) * 100, 1);

    public void SetBackgroundMode(bool background) => _backgroundMode = background;

    public async Task TickAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _isTicking, 1) == 1) return;

        try
        {
            var nowUtc = DateTime.UtcNow;
            if (_isReconnectingAudio && nowUtc < _nextAudioRetryAt)
            {
                StatusMessage = "Reconectando";
                return;
            }

            var tickNumber = _tickCount++;
            var includeDevices = Interlocked.Exchange(ref _outputDeviceRefreshRequested, 0) == 1;
            // Skip GSMTC while hidden to tray — volume control still runs at 1 Hz.
            var includeMedia = !_backgroundMode && tickNumber % 5 == 0;
            if (includeDevices)
            {
                var outputDevices = await Task.Run(_audio.ReadOutputDevices);
                if (_disposed) return;
                SynchronizeOutputDevices(outputDevices);
            }

            var snapshots = await Task.Run(_audio.ReadApplications);
            var mediaSessions = includeMedia
                ? await _media.ReadSessionsAsync()
                : null;
            if (_disposed) return;

            var now = DateTime.UtcNow;
            var elapsed = now - _lastTick;
            _lastTick = now;
            SynchronizeApplications(snapshots);
            EvaluateBoundProfileActivation(snapshots);
            var changes = new List<AudioSessionChange>();

            foreach (var snapshot in snapshots.Where(item => !_liveOnPreviousTick.Contains(item.Key)))
            {
                var binding = _profile.Channels.FirstOrDefault(channel =>
                    string.Equals(channel.ApplicationKey, snapshot.Key, StringComparison.OrdinalIgnoreCase));
                var safeVolume = _safety.SafeInitialVolume(binding?.TargetVolume);
                if (snapshot.Volume > safeVolume || binding is not null)
                    changes.Add(new AudioSessionChange(snapshot.Key, VolumePercent: safeVolume));
            }

            if (!IsProfileless && _profile.AutoDiscoverChannels)
            {
                var missing = false;
                foreach (var snapshot in snapshots)
                {
                    if (snapshot.IsSystemSounds) continue;
                    if (MixerChannelRegistry.IsAutoDiscoverSuppressed(_profile, snapshot.Key)) continue;
                    if (MixerChannelRegistry.ContainsApplication(_profile, snapshot.Key)) continue;
                    missing = true;
                    break;
                }

                if (missing)
                    DiscoverMissingLiveChannels(snapshots);
            }

            var liveKeys = snapshots.Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _liveOnPreviousTick.Clear();
            foreach (var key in liveKeys) _liveOnPreviousTick.Add(key);

            var pendingMuteRestores = _pendingMuteRestores.ToArray();
            foreach (var restore in pendingMuteRestores)
                changes.Add(new AudioSessionChange(restore.Key, IsMuted: restore.Value));

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

            MarkAudioHealthy(snapshots.Count);
            if (changes.Count > 0)
                await Task.Run(() => _audio.ApplyChanges(changes));

            // A transient Core Audio failure must not discard a requested
            // restore. Remove only entries that were successfully submitted and
            // were not replaced while the asynchronous apply was running.
            foreach (var restore in pendingMuteRestores)
            {
                if (_pendingMuteRestores.TryGetValue(restore.Key, out var current)
                    && current == restore.Value)
                {
                    _pendingMuteRestores.Remove(restore.Key);
                }
            }
        }
        catch (Exception exception)
        {
            ScheduleAudioRetry(exception.Message);
        }
        finally
        {
            Volatile.Write(ref _isTicking, 0);
        }
    }

    public void NotifyPowerResumed()
    {
        if (_disposed) return;
        _isReconnectingAudio = true;
        _audioRetryAttempt = 0;
        _nextAudioRetryAt = DateTime.UtcNow;
        _audio.RequestSessionRebuild();
        Interlocked.Exchange(ref _outputDeviceRefreshRequested, 1);
        StatusMessage = "Reconectando";
        OperationalLog.Shared.Info("power", "Retomada de energia — reconectando áudio");
    }

    private void MarkAudioHealthy(int sessionCount)
    {
        var wasReconnecting = _isReconnectingAudio;
        _isReconnectingAudio = false;
        _audioRetryAttempt = 0;
        _nextAudioRetryAt = DateTime.MinValue;
        StatusMessage = sessionCount == 0
            ? "Aguardando sessões de áudio"
            : "Sistema de áudio ativo";
        if (wasReconnecting)
            OperationalLog.Shared.Info("audio", $"Áudio restaurado ({sessionCount} app(s) detectados)");
    }

    private void ScheduleAudioRetry(string detail)
    {
        _isReconnectingAudio = true;
        _audioRetryAttempt = Math.Min(_audioRetryAttempt + 1, 6);
        var delaySeconds = Math.Min(30, Math.Pow(2, _audioRetryAttempt - 1));
        _nextAudioRetryAt = DateTime.UtcNow.AddSeconds(delaySeconds);
        _audio.RequestSessionRebuild();
        Interlocked.Exchange(ref _outputDeviceRefreshRequested, 1);
        StatusMessage = _audioRetryAttempt >= 3
            ? "Dispositivo indisponível"
            : "Reconectando";
        if (!string.IsNullOrWhiteSpace(detail) && _audioRetryAttempt == 1)
            StatusMessage = $"Reconectando — {detail}";
        OperationalLog.Shared.Error("audio", $"Falha de áudio (tentativa {_audioRetryAttempt}): {detail}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveTimer.Stop();
        _undoTimer.Stop();
        RestoreOsMutesBeforeExit();
        SaveNow();
        foreach (var channel in AllChannels) channel.PropertyChanged -= OnChannelPropertyChanged;
        _audio.OutputDevicesChanged -= OnOutputDevicesChanged;
        _audio.Dispose();
        OperationalLog.Shared.Info("app", "MainViewModel encerrado");
    }

    private void RestoreOsMutesBeforeExit()
    {
        try
        {
            var changes = new List<AudioSessionChange>();
            var assignedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var channel in AllChannels)
            {
                if (channel.ApplicationKey is null) continue;
                assignedKeys.Add(channel.ApplicationKey);
                // Drop temporary solo/global mute; keep only the channel's own mute.
                changes.Add(new AudioSessionChange(channel.ApplicationKey, IsMuted: channel.IsMuted));
            }

            if (IsGlobalMuted)
            {
                foreach (var application in Applications)
                {
                    if (assignedKeys.Contains(application.Key)) continue;
                    var previous = _muteStatesBeforeGlobalMute.GetValueOrDefault(application.Key);
                    changes.Add(new AudioSessionChange(application.Key, IsMuted: previous));
                }
            }

            if (changes.Count > 0)
                _audio.ApplyChanges(changes);
        }
        catch
        {
            // Shutdown must continue even if Core Audio rejects a final unmute.
        }
    }

    private void OnOutputDevicesChanged(object? sender, EventArgs eventArgs) =>
        Interlocked.Exchange(ref _outputDeviceRefreshRequested, 1);

    private void SynchronizeOutputDevices(IReadOnlyList<AudioOutputDevice> devices)
    {
        if (!OutputDevices.SequenceEqual(devices))
        {
            OutputDevices.Clear();
            foreach (var device in devices) OutputDevices.Add(device);
        }

        var selection = OutputDeviceSelectionResolver.Resolve(
            OutputDevices,
            _settings.PreferredOutputDeviceId,
            _settings.PreferSystemDefaultFallback);

        // This reflects the device used by the monitor in the header without
        // treating an automatic fallback as a new user preference.
        SetProperty(ref _selectedOutputDevice, selection.DisplayDevice, nameof(SelectedOutputDevice));
        _audio.SelectOutputDevice(selection.PreferredDeviceId, _settings.PreferSystemDefaultFallback);

        if (selection.IsUsingFallback)
            StatusMessage = $"Dispositivo preferido indisponível — usando {selection.DisplayDevice!.Name}";
        else if (selection.IsPreferredUnavailable)
            StatusMessage = "Dispositivo preferido indisponível";
    }

    private void PersistSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception exception)
        {
            StatusMessage = $"Não foi possível salvar as configurações: {exception.Message}";
            OperationalLog.Shared.Error("settings", $"Falha ao salvar: {exception.Message}");
        }
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
            RefreshBindOptions();
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
        var expandedMembershipChanged = false;

        if (channel.IsAssigned)
        {
            quickMembershipChanged = InsertOrdered(QuickChannels, channel);
            if (channel.IsExpanded)
            {
                var wasExpanded = ExpandedChannels.Contains(channel);
                var reordered = InsertOrdered(ExpandedChannels, channel);
                expandedMembershipChanged = !wasExpanded || reordered;
            }
            else
            {
                expandedMembershipChanged = ExpandedChannels.Remove(channel);
            }
        }
        else
        {
            quickMembershipChanged = QuickChannels.Remove(channel);
            expandedMembershipChanged = ExpandedChannels.Remove(channel);
        }

        // Rebuilding VisibleQuickChannels recreates every quick-card control. Doing
        // that for each TargetVolume update destroys the Slider while its thumb is
        // being dragged, which makes it jump to zero or stop responding.
        if (quickMembershipChanged) RefreshQuickPage();
        if (expandedMembershipChanged) RefreshExpandedFilter();
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
        foreach (var channel in ExpandedChannels.Where(item => !item.IsPinned).ToArray())
            channel.SetExpanded(false);
    }

    private int QuickPageCount => Math.Max(1, (FilteredQuickChannels.Count + _quickPageSize - 1) / _quickPageSize);

    private IReadOnlyList<ChannelViewModel> FilteredQuickChannels => QuickChannels
        .Where(MatchesChannelFilter)
        .Where(channel => ShowHiddenQuickChannels ? channel.IsHidden : !channel.IsHidden)
        .Where(channel => ShowHiddenQuickChannels || ShowInactiveQuickChannels || channel.IsOnline || channel.IsPinned)
        .ToArray();

    private bool MatchesChannelFilter(ChannelViewModel channel)
    {
        if (string.IsNullOrWhiteSpace(_channelFilter)) return true;
        return channel.ApplicationName.Contains(_channelFilter, StringComparison.CurrentCultureIgnoreCase)
            || channel.ExecutableName.Contains(_channelFilter, StringComparison.CurrentCultureIgnoreCase);
    }

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
        OnPropertyChanged(nameof(HasChannelFilter));
        PreviousQuickPageCommand.RaiseCanExecuteChanged();
        NextQuickPageCommand.RaiseCanExecuteChanged();
        ToggleInactiveQuickChannelsCommand.RaiseCanExecuteChanged();
        ToggleHiddenQuickChannelsCommand.RaiseCanExecuteChanged();
    }

    private void RefreshExpandedFilter()
    {
        if (string.IsNullOrWhiteSpace(_channelFilter))
        {
            if (DisplayedExpandedChannels.Count == ExpandedChannels.Count
                && DisplayedExpandedChannels.SequenceEqual(ExpandedChannels))
            {
                return;
            }

            DisplayedExpandedChannels.Clear();
            foreach (var channel in ExpandedChannels)
                DisplayedExpandedChannels.Add(channel);
            return;
        }

        var filtered = ExpandedChannels.Where(MatchesChannelFilter).ToArray();
        if (DisplayedExpandedChannels.Count == filtered.Length
            && DisplayedExpandedChannels.SequenceEqual(filtered))
        {
            return;
        }

        DisplayedExpandedChannels.Clear();
        foreach (var channel in filtered)
            DisplayedExpandedChannels.Add(channel);
    }

    private void OnChannelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(ChannelViewModel.IsOnline) or nameof(ChannelViewModel.IsHidden) or nameof(ChannelViewModel.IsPinned))
            RefreshQuickPage();
    }

    private static int CompareChannelOrder(ChannelViewModel left, ChannelViewModel right)
    {
        var pin = right.IsPinned.CompareTo(left.IsPinned);
        if (pin != 0) return pin;
        var order = left.Order.CompareTo(right.Order);
        return order != 0 ? order : left.Index.CompareTo(right.Index);
    }

    private static bool InsertOrdered(ObservableCollection<ChannelViewModel> target, ChannelViewModel channel)
    {
        var currentIndex = target.IndexOf(channel);
        var insertionIndex = 0;
        while (insertionIndex < target.Count)
        {
            if (ReferenceEquals(target[insertionIndex], channel))
            {
                insertionIndex++;
                continue;
            }

            if (CompareChannelOrder(target[insertionIndex], channel) > 0) break;
            insertionIndex++;
        }

        if (currentIndex >= 0)
        {
            // Account for the hole left by removing the existing item.
            var adjusted = insertionIndex > currentIndex ? insertionIndex - 1 : insertionIndex;
            if (adjusted == currentIndex) return false;
            target.Move(currentIndex, adjusted);
            return true;
        }

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
        var channel = new ChannelViewModel(
            binding,
            Applications,
            OnChannelChanged,
            OnViewModeChanged,
            OnMediaTransport,
            RemoveChannel,
            MoveChannel,
            OnChannelPinChanged);
        channel.PropertyChanged += OnChannelPropertyChanged;
        InsertOrdered(AllChannels, channel);
        channel.SetMaximumVolume(_safety.Clamp(100));
        return channel;
    }

    private void MoveChannel(ChannelViewModel channel, int delta)
    {
        if (!MixerChannelRegistry.Move(_profile, channel.Binding, delta))
        {
            StatusMessage = "Não é possível mover este canal nessa direção";
            return;
        }

        ResortChannelCollections();
        Save();
        StatusMessage = $"Ordem atualizada: {channel.ApplicationName}";
    }

    private void OnChannelPinChanged(ChannelViewModel channel)
    {
        MixerChannelRegistry.RenumberOrders(_profile);
        if (channel.IsPinned) channel.SetExpanded(true);
        ResortChannelCollections();
        StatusMessage = channel.IsPinned
            ? $"{channel.ApplicationName} fixado"
            : $"{channel.ApplicationName} desafixado";
    }

    private void ResortChannelCollections()
    {
        ReorderCollection(AllChannels);
        ReorderCollection(QuickChannels);
        ReorderCollection(ExpandedChannels);
        RefreshQuickPage();
        RefreshExpandedFilter();
        OnPropertyChanged(nameof(ExpandedCountText));
    }

    private static void ReorderCollection(ObservableCollection<ChannelViewModel> collection)
    {
        var ordered = collection
            .OrderByDescending(channel => channel.IsPinned)
            .ThenBy(channel => channel.Order)
            .ThenBy(channel => channel.Index)
            .ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var currentIndex = collection.IndexOf(ordered[index]);
            if (currentIndex != index) collection.Move(currentIndex, index);
        }
    }

    private void AutoAddDiscoveredSession(AudioApplication snapshot)
    {
        if (snapshot.IsSystemSounds) return;
        if (MixerChannelRegistry.IsAutoDiscoverSuppressed(_profile, snapshot.Key)) return;
        if (MixerChannelRegistry.ContainsApplication(_profile, snapshot.Key)) return;

        var application = Applications.FirstOrDefault(item =>
            string.Equals(item.Key, snapshot.Key, StringComparison.OrdinalIgnoreCase));
        if (application is null) return;

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
        RefreshAddableApplications();
        RefreshAssignmentOptions();
        Save();
        StatusMessage = $"{application.DisplayName} adicionado automaticamente";
        OperationalLog.Shared.Info("profile", $"Auto-discover adicionou {application.DisplayName} no perfil {_profile.Name}");
    }

    private void DiscoverMissingLiveChannels(IReadOnlyList<AudioApplication>? snapshots = null)
    {
        if (!_profile.AutoDiscoverChannels) return;

        if (snapshots is not null)
        {
            foreach (var snapshot in snapshots)
                AutoAddDiscoveredSession(snapshot);
            return;
        }

        foreach (var application in Applications.ToArray())
        {
            AutoAddDiscoveredSession(new AudioApplication(
                application.Key,
                application.DisplayName,
                application.ExecutableName,
                application.ExecutablePath,
                Array.Empty<int>(),
                application.Volume,
                application.IsMuted,
                application.Peak,
                IsSystemSounds: false));
        }
    }

    private void RemoveChannel(ChannelViewModel channel)
    {
        if (!AllChannels.Contains(channel) || !MixerChannelRegistry.Remove(_profile, channel.Binding)) return;

        if (!string.IsNullOrWhiteSpace(channel.ApplicationKey))
            MixerChannelRegistry.SuppressAutoDiscover(_profile, channel.ApplicationKey);

        _undoTimer.Stop();
        _pendingRemoval = new RemovedChannelState(channel.Binding);
        channel.PropertyChanged -= OnChannelPropertyChanged;
        AllChannels.Remove(channel);
        QuickChannels.Remove(channel);
        ExpandedChannels.Remove(channel);
        VisibleQuickChannels.Remove(channel);
        RefreshQuickPage();
        RefreshExpandedFilter();
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

        if (!string.IsNullOrWhiteSpace(removal.Binding.ApplicationKey))
            MixerChannelRegistry.AllowAutoDiscover(_profile, removal.Binding.ApplicationKey);

        var channel = CreateChannelViewModel(removal.Binding);
        SynchronizeViewCollections(channel);
        RefreshQuickPage();
        RefreshExpandedFilter();
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
        MixerChannelRegistry.AllowAutoDiscover(_profile, application.Key);
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
        try
        {
            if (IsProfileless)
            {
                _catalog.ActiveProfileId = string.Empty;
                if (string.IsNullOrWhiteSpace(_catalog.DefaultProfileId)
                    || _catalog.FindById(_catalog.DefaultProfileId) is null)
                {
                    _catalog.DefaultProfileId = string.Empty;
                }
            }
            else
            {
                _catalog.ActiveProfileId = _profile.Id;
            }

            _store.SaveCatalog(_catalog);
        }
        catch (Exception exception)
        {
            StatusMessage = $"Não foi possível salvar o perfil: {exception.Message}";
            OperationalLog.Shared.Error("profile", $"Falha ao salvar: {exception.Message}");
        }
    }

    private void ExportDiagnostics()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Exportar diagnóstico do Molecular",
                Filter = "Texto (*.txt)|*.txt",
                FileName = $"molecular-diagnostico-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                AddExtension = true,
                DefaultExt = ".txt"
            };
            if (dialog.ShowDialog() != true) return;

            var snapshot = new DiagnosticsSnapshot(
                DiagnosticsExporter.ResolveProductVersion(),
                DiagnosticsExporter.ResolveFileVersion(),
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                HashMachineName(),
                _dpiScalePercent,
                SelectedOutputDevice?.Name ?? _audio.OutputDeviceName,
                Applications.Count,
                AllChannels.Count,
                ExpandedChannels.Count,
                StatusMessage,
                StartWithWindows,
                OperationalLog.Shared.ReadRecentLines());

            DiagnosticsExporter.WriteReport(dialog.FileName, snapshot);
            OperationalLog.Shared.Info("diagnostics", $"Diagnóstico exportado para {dialog.FileName}");
            StatusMessage = "Diagnóstico exportado";
        }
        catch (Exception exception)
        {
            OperationalLog.Shared.Error("diagnostics", $"Falha ao exportar: {exception.Message}");
            StatusMessage = $"Não foi possível exportar o diagnóstico: {exception.Message}";
        }
    }

    private void ExportActiveProfile()
    {
        try
        {
            var safeName = string.Concat(_profile.Name.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)));
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "perfil";
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Exportar perfil do Molecular",
                Filter = "Perfil Molecular (*.molecular-profile.json)|*.molecular-profile.json|JSON (*.json)|*.json",
                FileName = $"{safeName}-{DateTime.Now:yyyyMMdd}.molecular-profile.json",
                AddExtension = true,
                DefaultExt = "molecular-profile.json"
            };
            if (dialog.ShowDialog() != true) return;

            ProfileTransfer.ExportToFile(_profile, dialog.FileName);
            OperationalLog.Shared.Info("profile", $"Perfil exportado: {_profile.Name}");
            StatusMessage = $"Perfil '{_profile.Name}' exportado";
        }
        catch (Exception exception)
        {
            OperationalLog.Shared.Error("profile", $"Falha ao exportar perfil: {exception.Message}");
            StatusMessage = $"Não foi possível exportar o perfil: {exception.Message}";
        }
    }

    private void ImportProfile()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Importar perfil do Molecular",
                Filter = "Perfil Molecular (*.molecular-profile.json;*.json)|*.molecular-profile.json;*.json|JSON (*.json)|*.json"
            };
            if (dialog.ShowDialog() != true) return;

            var imported = ProfileTransfer.ImportFromFile(dialog.FileName);
            imported.Name = NextAvailableProfileName(imported.Name);
            _catalog.Profiles.Add(imported);
            ActivateProfile(imported.Id, autoActivated: false, $"Perfil '{imported.Name}' importado");
            IsProfileFlyoutOpen = true;
        }
        catch (Exception exception)
        {
            OperationalLog.Shared.Error("profile", $"Falha ao importar perfil: {exception.Message}");
            StatusMessage = $"Não foi possível importar o perfil: {exception.Message}";
        }
    }

    private static string HashMachineName()
    {
        try
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName));
            return Convert.ToHexString(bytes.AsSpan(0, 8));
        }
        catch
        {
            return "indisponivel";
        }
    }

    private void EvaluateBoundProfileActivation(IReadOnlyList<AudioApplication> snapshots)
    {
        if (_catalog.Profiles.Count == 0) return;

        var liveKeys = snapshots.Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        MixerProfile? matched = null;
        foreach (var profile in _catalog.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.BoundApplicationKey)) continue;
            if (!liveKeys.Contains(profile.BoundApplicationKey)) continue;
            matched = profile;
            break;
        }

        if (matched is not null)
        {
            if (!_boundProfileActivationGate.CanActivateMatch()) return;
            if (!string.Equals(_profile.Id, matched.Id, StringComparison.OrdinalIgnoreCase))
            {
                ActivateProfile(
                    matched.Id,
                    autoActivated: true,
                    $"Perfil '{matched.Name}' ativado ({matched.BoundApplicationName ?? matched.BoundApplicationKey})");
            }
            return;
        }

        // A manual profile choice suppresses the currently-open bound app, not
        // every future activation. Once no bound app is live, automatic matching
        // is armed again for the next launch.
        _boundProfileActivationGate.ObserveNoMatch();

        // Bound app closed: restore default profile when one exists.
        if (IsProfileless) return;
        if (string.IsNullOrWhiteSpace(_catalog.DefaultProfileId)
            || _catalog.FindById(_catalog.DefaultProfileId) is null)
        {
            return;
        }

        if (!string.Equals(_profile.Id, _catalog.DefaultProfileId, StringComparison.OrdinalIgnoreCase)
            && (_autoActivatedProfileId is not null
                || _catalog.Profiles.Any(profile =>
                    !string.IsNullOrWhiteSpace(profile.BoundApplicationKey)
                    && string.Equals(profile.Id, _profile.Id, StringComparison.OrdinalIgnoreCase))))
        {
            _autoActivatedProfileId = null;
            ActivateProfile(
                _catalog.DefaultProfileId,
                autoActivated: false,
                "App atrelado fechou — perfil padrão restaurado");
        }
    }

    private void ActivateProfile(string profileId, bool autoActivated, string status)
    {
        var next = _catalog.FindById(profileId);
        if (next is null) return;
        if (!IsProfileless && string.Equals(next.Id, _profile.Id, StringComparison.OrdinalIgnoreCase))
            return;

        _catalog.ActiveProfileId = next.Id;
        _profile = next;
        _safety = new SafetyEngine(_profile.Safety);
        _autoActivatedProfileId = autoActivated ? next.Id : null;
        if (!autoActivated) _boundProfileActivationGate.SuppressCurrentMatches();
        _liveOnPreviousTick.Clear();
        foreach (var application in Applications)
            _liveOnPreviousTick.Add(application.Key);
        if (IsGlobalMuted) IsGlobalMuted = false;
        RebuildChannelsFromActiveProfile();
        if (_profile.AutoDiscoverChannels)
            DiscoverMissingLiveChannels();
        RefreshProfileUi(status);
        OperationalLog.Shared.Info("profile", $"{status} (id={next.Id})");
        Save();
    }

    private void EnterProfilelessMode(string status)
    {
        _boundProfileActivationGate.SuppressCurrentMatches();
        _autoActivatedProfileId = null;
        _catalog.ActiveProfileId = string.Empty;
        _profile = CreateProfilelessWorkspace();
        _safety = new SafetyEngine(_profile.Safety);
        _liveOnPreviousTick.Clear();
        foreach (var application in Applications)
            _liveOnPreviousTick.Add(application.Key);
        if (IsGlobalMuted) IsGlobalMuted = false;
        RebuildChannelsFromActiveProfile();
        RefreshProfileUi(status);
        OperationalLog.Shared.Info("profile", status);
        Save();
    }

    private void RefreshProfileUi(string status)
    {
        RefreshProfileList();
        RefreshBindOptions();
        RefreshQuickPage();
        RefreshExpandedFilter();
        RefreshAssignmentOptions();
        RefreshChannelVolumeLimits();
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(IsProfileless));
        OnPropertyChanged(nameof(CanManageActiveProfile));
        OnPropertyChanged(nameof(IsActiveProfileDefault));
        OnPropertyChanged(nameof(ProfileBindSummary));
        OnPropertyChanged(nameof(AutoDiscoverChannels));
        OnPropertyChanged(nameof(IsSafetyEnabled));
        OnPropertyChanged(nameof(GlobalCeiling));
        OnPropertyChanged(nameof(NewSessionVolume));
        OnPropertyChanged(nameof(SafetySummary));
        OnPropertyChanged(nameof(ExpandedCountText));
        DeleteProfileCommand.RaiseCanExecuteChanged();
        SetDefaultProfileCommand.RaiseCanExecuteChanged();
        StatusMessage = status;
    }

    private static MixerProfile CreateProfilelessWorkspace() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "Sem perfil",
        SchemaVersion = 11
    };

    private void RebuildChannelsFromActiveProfile()
    {
        foreach (var channel in AllChannels.ToArray())
            channel.PropertyChanged -= OnChannelPropertyChanged;
        AllChannels.Clear();
        QuickChannels.Clear();
        VisibleQuickChannels.Clear();
        ExpandedChannels.Clear();

        foreach (var binding in MixerChannelRegistry.Sorted(_profile))
        {
            var channel = CreateChannelViewModel(binding);
            SynchronizeViewCollections(channel);
        }

        RefreshExpandedFilter();
    }

    private void RefreshProfileList()
    {
        AvailableProfiles.Clear();
        AvailableProfiles.Add(new ProfileListItem(string.Empty, "Sem perfil", false));
        foreach (var profile in _catalog.Profiles.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            AvailableProfiles.Add(new ProfileListItem(
                profile.Id,
                profile.Name,
                string.Equals(profile.Id, _catalog.DefaultProfileId, StringComparison.OrdinalIgnoreCase)));
        }

        _selectedProfileItem = IsProfileless
            ? AvailableProfiles[0]
            : AvailableProfiles.FirstOrDefault(item =>
                string.Equals(item.Id, _profile.Id, StringComparison.OrdinalIgnoreCase))
              ?? AvailableProfiles[0];
        OnPropertyChanged(nameof(SelectedProfileItem));
        DeleteProfileCommand.RaiseCanExecuteChanged();
        SetDefaultProfileCommand.RaiseCanExecuteChanged();
    }

    private void RefreshBindOptions()
    {
        BindOptions.Clear();
        BindOptions.Add(new ProfileBindOption(null, "(Nenhum aplicativo)"));
        foreach (var application in Applications.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            BindOptions.Add(new ProfileBindOption(application.Key, application.DisplayName));

        if (!string.IsNullOrWhiteSpace(_profile.BoundApplicationKey)
            && BindOptions.All(option => !string.Equals(option.ApplicationKey, _profile.BoundApplicationKey, StringComparison.OrdinalIgnoreCase)))
        {
            BindOptions.Add(new ProfileBindOption(
                _profile.BoundApplicationKey,
                _profile.BoundApplicationName ?? _profile.BoundApplicationKey));
        }

        _selectedBindOption = BindOptions.FirstOrDefault(option =>
            string.Equals(option.ApplicationKey, _profile.BoundApplicationKey, StringComparison.OrdinalIgnoreCase))
            ?? BindOptions[0];
        OnPropertyChanged(nameof(SelectedBindOption));
    }

    private void CreateProfile()
    {
        var profile = new MixerProfile
        {
            Name = NextAvailableProfileName("Novo perfil"),
            Safety = CloneSafety(_profile.Safety)
        };
        _catalog.Profiles.Add(profile);
        if (string.IsNullOrWhiteSpace(_catalog.DefaultProfileId)
            || _catalog.FindById(_catalog.DefaultProfileId) is null)
        {
            _catalog.DefaultProfileId = profile.Id;
        }

        ActivateProfile(profile.Id, autoActivated: false, $"Perfil '{profile.Name}' criado");
        IsProfileFlyoutOpen = true;
    }

    private void DuplicateActiveProfile()
    {
        var copy = new MixerProfile
        {
            Name = NextAvailableProfileName(IsProfileless ? "Novo perfil" : $"{_profile.Name} (cópia)"),
            BoundApplicationKey = null,
            BoundApplicationName = null,
            AutoDiscoverChannels = !IsProfileless && _profile.AutoDiscoverChannels,
            SuppressedApplicationKeys = IsProfileless
                ? []
                : _profile.SuppressedApplicationKeys.ToList(),
            Safety = CloneSafety(_profile.Safety),
            Channels = _profile.Channels.Select(CloneChannel).ToList()
        };
        _catalog.Profiles.Add(copy);
        if (string.IsNullOrWhiteSpace(_catalog.DefaultProfileId)
            || _catalog.FindById(_catalog.DefaultProfileId) is null)
        {
            _catalog.DefaultProfileId = copy.Id;
        }

        ActivateProfile(copy.Id, autoActivated: false, $"Perfil '{copy.Name}' duplicado");
    }

    private void DeleteActiveProfile()
    {
        if (IsProfileless)
        {
            StatusMessage = "Já está sem perfil";
            return;
        }

        var removedName = _profile.Name;
        var removedId = _profile.Id;
        var wasDefault = string.Equals(removedId, _catalog.DefaultProfileId, StringComparison.OrdinalIgnoreCase);
        _catalog.Profiles.RemoveAll(profile => string.Equals(profile.Id, removedId, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(_autoActivatedProfileId, removedId, StringComparison.OrdinalIgnoreCase))
            _autoActivatedProfileId = null;

        if (_catalog.Profiles.Count == 0)
        {
            _catalog.DefaultProfileId = string.Empty;
            EnterProfilelessMode($"Perfil '{removedName}' excluído — operando sem perfil");
            return;
        }

        if (wasDefault || _catalog.FindById(_catalog.DefaultProfileId) is null)
            _catalog.DefaultProfileId = _catalog.Profiles[0].Id;

        ActivateProfile(_catalog.DefaultProfileId, autoActivated: false, $"Perfil '{removedName}' excluído");
    }

    private void SetActiveAsDefault()
    {
        if (IsProfileless) return;
        _catalog.DefaultProfileId = _profile.Id;
        RefreshProfileList();
        OnPropertyChanged(nameof(IsActiveProfileDefault));
        DeleteProfileCommand.RaiseCanExecuteChanged();
        SetDefaultProfileCommand.RaiseCanExecuteChanged();
        StatusMessage = $"'{_profile.Name}' definido como perfil padrão";
        Save();
    }

    private string NextAvailableProfileName(string baseName)
    {
        if (_catalog.Profiles.All(profile => !string.Equals(profile.Name, baseName, StringComparison.OrdinalIgnoreCase)))
            return baseName;
        for (var index = 2; index < 100; index++)
        {
            var candidate = $"{baseName} {index}";
            if (_catalog.Profiles.All(profile => !string.Equals(profile.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }

        return $"{baseName} {Guid.NewGuid():N}"[..18];
    }

    private static SafetyPolicy CloneSafety(SafetyPolicy source) => new()
    {
        Enabled = source.Enabled,
        GlobalCeiling = source.GlobalCeiling,
        NewSessionVolume = source.NewSessionVolume,
        RisePerSecond = source.RisePerSecond,
        FallPerSecond = source.FallPerSecond
    };

    private static ChannelBinding CloneChannel(ChannelBinding source) => new()
    {
        Index = source.Index,
        ApplicationKey = source.ApplicationKey,
        ApplicationName = source.ApplicationName,
        ExecutableName = source.ExecutableName,
        ExecutablePath = source.ExecutablePath,
        TargetVolume = source.TargetVolume,
        Ceiling = source.Ceiling,
        IsMuted = source.IsMuted,
        IsSolo = source.IsSolo,
        ViewMode = source.ViewMode,
        Order = source.Order,
        AccentColor = source.AccentColor,
        IsPinned = source.IsPinned,
        IsHidden = source.IsHidden
    };

    private sealed record RemovedChannelState(ChannelBinding Binding);
}

public sealed record ProfileListItem(string Id, string Name, bool IsDefault)
{
    public string DisplayName => IsDefault ? $"{Name} (padrão)" : Name;
}

public sealed record ProfileBindOption(string? ApplicationKey, string DisplayName);
