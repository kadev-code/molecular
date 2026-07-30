using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Molecular.App.Media;
using Molecular.Core.Models;

namespace Molecular.App.ViewModels;

public sealed class ChannelViewModel : ObservableObject
{
    private readonly ChannelBinding _binding;
    private readonly Action<ChannelViewModel> _changed;
    private readonly Action<ChannelViewModel> _viewModeChanged;
    private readonly Action<ChannelViewModel, MediaTransportAction> _mediaAction;
    private readonly Action<ChannelViewModel> _removeRequested;
    private AudioApplicationViewModel? _selectedApplication;
    private string _applicationName;
    private string _executableName;
    private ImageSource? _compactIconSource;
    private ImageSource? _expandedIconSource;
    private double _targetVolume;
    private double _maximumVolume = 100;
    private double _effectiveVolume;
    private double _peak;
    private bool _isMuted;
    private bool _isSolo;
    private bool _isOnline;
    private bool _hasAudioActivity;
    private MediaSessionSnapshot? _mediaSession;
    private ImageSource? _mediaThumbnailSource;
    private Brush _mediaBackdropBrush = CreateDefaultMediaBackdrop();
    private double _mediaBackdropImageOpacity;
    private string? _mediaThumbnailKey;
    private DateTime _lastAudioActivityAt = DateTime.MinValue;

    public ChannelViewModel(
        ChannelBinding binding,
        ObservableCollection<AudioApplicationViewModel> availableApplications,
        Action<ChannelViewModel> changed,
        Action<ChannelViewModel> viewModeChanged,
        Action<ChannelViewModel, MediaTransportAction> mediaAction,
        Action<ChannelViewModel> removeRequested)
    {
        _binding = binding;
        _changed = changed;
        _viewModeChanged = viewModeChanged;
        _mediaAction = mediaAction;
        _removeRequested = removeRequested;
        AvailableApplications = availableApplications;
        AssignmentOptions = new ObservableCollection<AudioApplicationViewModel>();
        _applicationName = binding.ApplicationName ?? "Canal não atribuído";
        _executableName = binding.ExecutableName ?? "Nenhuma fonte atribuída";
        // Executable icon extraction can block the shell during startup. Live sessions
        // hydrate the icon on the first background audio poll instead.
        _compactIconSource = null;
        _expandedIconSource = null;
        _targetVolume = binding.TargetVolume;
        _effectiveVolume = binding.TargetVolume;
        _isMuted = binding.IsMuted;
        _isSolo = binding.IsSolo;
        ToggleMuteCommand = new RelayCommand(() => IsMuted = !IsMuted);
        ToggleSoloCommand = new RelayCommand(() => IsSolo = !IsSolo);
        ToggleExpandedCommand = new RelayCommand(ToggleExpanded);
        ToggleHiddenCommand = new RelayCommand(ToggleHidden);
        ClearCommand = new RelayCommand(ClearAssignment);
        ResetVolumeCommand = new RelayCommand(() => TargetVolume = 100);
        RemoveCommand = new RelayCommand(() => _removeRequested(this));
        PreviousMediaCommand = new RelayCommand(
            () => _mediaAction(this, MediaTransportAction.Previous),
            () => _mediaSession?.CanGoPrevious == true);
        ToggleMediaCommand = new RelayCommand(
            () => _mediaAction(this, MediaTransportAction.TogglePlayPause),
            () => _mediaSession?.CanTogglePlayPause == true);
        NextMediaCommand = new RelayCommand(
            () => _mediaAction(this, MediaTransportAction.Next),
            () => _mediaSession?.CanGoNext == true);
    }

    public int Index => _binding.Index;
    public int Order => _binding.Order;
    internal ChannelBinding Binding => _binding;
    public double Ceiling => _binding.Ceiling;
    public ObservableCollection<AudioApplicationViewModel> AvailableApplications { get; }
    public ObservableCollection<AudioApplicationViewModel> AssignmentOptions { get; }
    public string? ApplicationKey => _binding.ApplicationKey;
    public string ApplicationName { get => _applicationName; private set => SetProperty(ref _applicationName, value); }
    public string ExecutableName { get => _executableName; private set => SetProperty(ref _executableName, value); }
    public string Initials => SelectedApplication?.Initials
        ?? (IsAssigned && !string.IsNullOrWhiteSpace(ApplicationName)
            ? ApplicationName[..1].ToUpperInvariant()
            : "+");
    public ImageSource? CompactIconSource => SelectedApplication?.CompactIconSource ?? _compactIconSource;
    public ImageSource? ExpandedIconSource => SelectedApplication?.ExpandedIconSource ?? _expandedIconSource;
    public ImageSource? IconSource => ExpandedIconSource;
    public bool HasIcon => CompactIconSource is not null || ExpandedIconSource is not null;
    public bool IsAssigned => _binding.ApplicationKey is not null;
    public bool IsDisconnected => IsAssigned && !IsOnline;
    public bool IsExpanded => string.Equals(_binding.ViewMode, "expanded", StringComparison.OrdinalIgnoreCase);
    public bool IsHidden => _binding.IsHidden;
    public string Accent => _binding.AccentColor ?? "#7B5CFF";
    public string StatusText => !IsAssigned
        ? "Selecione um aplicativo"
        : IsOnline
            ? (IsMediaPlaying || HasAudioActivity ? "Reproduzindo áudio" : "Sem atividade")
            : "Aguardando reconexão…";
    public string QuickStatusText => IsHidden ? "OCULTO" : !IsAssigned ? "Clique para atribuir" : IsOnline ? "ATIVO" : "DESCONECTADO";
    public string HideActionText => IsHidden ? "Mostrar canal" : "Ocultar canal";
    public string VolumeDisplayText => IsOnline ? $"{TargetVolume:0}%" : "—";
    public bool HasMediaSession => _mediaSession is not null;
    public ImageSource? MediaThumbnailSource => _mediaThumbnailSource;
    public bool HasMediaThumbnail => MediaThumbnailSource is not null;
    public Brush MediaBackdropBrush => _mediaBackdropBrush;
    public double MediaBackdropImageOpacity => _mediaBackdropImageOpacity;
    public bool IsMediaPlaying => _mediaSession?.IsPlaying == true;
    public string MediaPlayPauseGlyph => IsMediaPlaying ? "\uE769" : "\uE768";
    public double MediaProgress => _mediaSession is { Duration.TotalMilliseconds: > 0 } session
        ? Math.Clamp(session.Position.TotalMilliseconds / session.Duration.TotalMilliseconds * 100, 0, 100)
        : 0;
    public string MediaPositionText => FormatMediaTime(_mediaSession?.Position ?? TimeSpan.Zero);
    public string MediaDurationText => FormatMediaTime(_mediaSession?.Duration ?? TimeSpan.Zero);
    public string MediaHeading => !IsOnline
        ? "APLICATIVO DESCONECTADO"
        : HasMediaSession ? (IsMediaPlaying ? "REPRODUZINDO AGORA" : "MÍDIA PAUSADA")
        : HasAudioActivity ? "ÁUDIO DO APLICATIVO" : "NENHUMA MÍDIA ATIVA";
    public string MediaTitle => !IsOnline
        ? "Aguardando uma nova sessão de áudio."
        : HasMediaSession && !string.IsNullOrWhiteSpace(_mediaSession!.Title)
            ? _mediaSession.Title
        : HasAudioActivity
            ? $"{ApplicationName} está reproduzindo áudio."
            : "O aplicativo não está reproduzindo conteúdo no momento.";
    public string MediaSubtitle => !IsOnline
        ? "O canal será restaurado automaticamente quando o aplicativo voltar."
        : HasMediaSession
            ? (string.IsNullOrWhiteSpace(_mediaSession!.Artist) ? ApplicationName : _mediaSession.Artist)
        : HasAudioActivity
            ? "O aplicativo não disponibilizou uma sessão de mídia ao Windows."
            : "Os controles aparecem quando uma sessão de mídia compatível estiver ativa.";
    public bool HasAudioActivity => _hasAudioActivity;
    public double CardOpacity => IsMuted ? 0.58 : 1;
    public double EffectiveVolume { get => _effectiveVolume; private set => SetProperty(ref _effectiveVolume, value); }
    public double Peak
    {
        get => _peak;
        private set
        {
            if (!SetProperty(ref _peak, Math.Clamp(value, 0, 100))) return;
            OnPropertyChanged(nameof(PeakDb));
            OnPropertyChanged(nameof(PeakDbText));
        }
    }
    public double PeakDb => Peak <= 0.1 ? -60 : Math.Max(-60, 20 * Math.Log10(Peak / 100d));
    public string PeakDbText => Peak <= 0.1 ? "−∞ dB" : $"{PeakDb:0.0} dB";
    public bool IsOnline { get => _isOnline; private set { if (SetProperty(ref _isOnline, value)) NotifyState(); } }

    public AudioApplicationViewModel? SelectedApplication
    {
        get => _selectedApplication;
        set
        {
            if (!SetProperty(ref _selectedApplication, value) || value is null) return;
            _binding.ApplicationKey = value.Key;
            _binding.ApplicationName = value.DisplayName;
            _binding.ExecutableName = value.ExecutableName;
            _binding.ExecutablePath = value.ExecutablePath;
            ApplicationName = value.DisplayName;
            ExecutableName = value.ExecutableName;
            _compactIconSource = value.CompactIconSource;
            _expandedIconSource = value.ExpandedIconSource;
            TargetVolume = value.Volume;
            NotifyIdentity();
            _changed(this);
        }
    }

    public double TargetVolume
    {
        get => _targetVolume;
        set
        {
            var clamped = Math.Clamp(value, 0, MaximumVolume);
            if (!SetProperty(ref _targetVolume, clamped)) return;
            _binding.TargetVolume = clamped;
            OnPropertyChanged(nameof(VolumeDisplayText));
            _changed(this);
        }
    }

    public double MaximumVolume
    {
        get => _maximumVolume;
        private set => SetProperty(ref _maximumVolume, Math.Clamp(value, 1, 100));
    }

    public void SetMaximumVolume(double maximumVolume)
    {
        MaximumVolume = maximumVolume;
        if (TargetVolume > MaximumVolume) TargetVolume = MaximumVolume;
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (!SetProperty(ref _isMuted, value)) return;
            _binding.IsMuted = value;
            OnPropertyChanged(nameof(CardOpacity));
            _changed(this);
        }
    }

    public bool IsSolo
    {
        get => _isSolo;
        set
        {
            if (!SetProperty(ref _isSolo, value)) return;
            _binding.IsSolo = value;
            _changed(this);
        }
    }

    public RelayCommand ToggleMuteCommand { get; }
    public RelayCommand ToggleSoloCommand { get; }
    public RelayCommand ToggleExpandedCommand { get; }
    public RelayCommand ToggleHiddenCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand ResetVolumeCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand PreviousMediaCommand { get; }
    public RelayCommand ToggleMediaCommand { get; }
    public RelayCommand NextMediaCommand { get; }

    public void Sync(AudioApplicationViewModel? application, double effectiveVolume, double peak)
    {
        if (application is null)
        {
            IsOnline = false;
            _lastAudioActivityAt = DateTime.MinValue;
            SetAudioActivity(false);
            SyncMedia(null);
            EffectiveVolume = Math.Min(TargetVolume, 100);
            Peak = 0;
            return;
        }

        var identityChanged = !ReferenceEquals(_selectedApplication, application)
            || !string.Equals(_applicationName, application.DisplayName, StringComparison.Ordinal)
            || !string.Equals(_executableName, application.ExecutableName, StringComparison.Ordinal)
            || !ReferenceEquals(_compactIconSource, application.CompactIconSource)
            || !ReferenceEquals(_expandedIconSource, application.ExpandedIconSource);
        _selectedApplication = application;
        ApplicationName = application.DisplayName;
        ExecutableName = application.ExecutableName;
        _binding.ExecutableName = application.ExecutableName;
        _binding.ExecutablePath = application.ExecutablePath;
        _compactIconSource = application.CompactIconSource;
        _expandedIconSource = application.ExpandedIconSource;
        EffectiveVolume = effectiveVolume;
        Peak = peak;
        IsOnline = true;
        UpdateAudioActivity(application.Peak);
        if (identityChanged) NotifyIdentity();
    }

    public void SyncMedia(MediaSessionSnapshot? mediaSession)
    {
        if (mediaSession is null && _mediaSession is null && _mediaThumbnailKey is null) return;

        var thumbnailKey = mediaSession is null
            ? null
            : $"{mediaSession.SourceAppId}\n{mediaSession.Title}\n{mediaSession.Artist}\n{mediaSession.ThumbnailBytes?.Length ?? 0}";
        if (!string.Equals(_mediaThumbnailKey, thumbnailKey, StringComparison.Ordinal))
        {
            _mediaThumbnailKey = thumbnailKey;
            var thumbnail = LoadThumbnail(mediaSession?.ThumbnailBytes);
            _mediaThumbnailSource = thumbnail?.Image;
            _mediaBackdropBrush = thumbnail?.BackdropBrush ?? CreateDefaultMediaBackdrop();
            _mediaBackdropImageOpacity = thumbnail?.CanFillBackdrop == true ? 0.2 : 0;
            OnPropertyChanged(nameof(MediaThumbnailSource));
            OnPropertyChanged(nameof(HasMediaThumbnail));
            OnPropertyChanged(nameof(MediaBackdropBrush));
            OnPropertyChanged(nameof(MediaBackdropImageOpacity));
        }

        _mediaSession = mediaSession;
        OnPropertyChanged(nameof(HasMediaSession));
        OnPropertyChanged(nameof(IsMediaPlaying));
        OnPropertyChanged(nameof(MediaPlayPauseGlyph));
        OnPropertyChanged(nameof(MediaProgress));
        OnPropertyChanged(nameof(MediaPositionText));
        OnPropertyChanged(nameof(MediaDurationText));
        OnPropertyChanged(nameof(StatusText));
        PreviousMediaCommand.RaiseCanExecuteChanged();
        ToggleMediaCommand.RaiseCanExecuteChanged();
        NextMediaCommand.RaiseCanExecuteChanged();
        NotifyMediaState();
    }

    public void SetExpanded(bool expanded)
    {
        var next = expanded ? "expanded" : "collapsed";
        if (string.Equals(_binding.ViewMode, next, StringComparison.OrdinalIgnoreCase)) return;
        _binding.ViewMode = next;
        OnPropertyChanged(nameof(IsExpanded));
        _viewModeChanged(this);
        _changed(this);
    }

    private void ToggleExpanded() => SetExpanded(!IsExpanded);

    private void ToggleHidden()
    {
        _binding.IsHidden = !_binding.IsHidden;
        if (_binding.IsHidden) SetExpanded(false);
        OnPropertyChanged(nameof(IsHidden));
        OnPropertyChanged(nameof(HideActionText));
        OnPropertyChanged(nameof(QuickStatusText));
        _changed(this);
    }

    public void ClearAssignment()
    {
        _binding.ApplicationKey = null;
        _binding.ApplicationName = null;
        _binding.ExecutableName = null;
        _binding.ExecutablePath = null;
        _selectedApplication = null;
        _binding.IsMuted = false;
        _binding.IsSolo = false;
        _binding.ViewMode = "collapsed";
        _binding.IsHidden = false;
        ApplicationName = "Canal não atribuído";
        ExecutableName = "Nenhuma fonte atribuída";
        _compactIconSource = null;
        _expandedIconSource = null;
        SyncMedia(null);
        _isMuted = false;
        _isSolo = false;
        IsOnline = false;
        Peak = 0;
        NotifyIdentity();
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(IsHidden));
        OnPropertyChanged(nameof(HideActionText));
        OnPropertyChanged(nameof(IsMuted));
        OnPropertyChanged(nameof(IsSolo));
        OnPropertyChanged(nameof(CardOpacity));
        _changed(this);
    }

    private void NotifyIdentity()
    {
        OnPropertyChanged(nameof(SelectedApplication));
        OnPropertyChanged(nameof(ApplicationKey));
        OnPropertyChanged(nameof(Initials));
        OnPropertyChanged(nameof(CompactIconSource));
        OnPropertyChanged(nameof(ExpandedIconSource));
        OnPropertyChanged(nameof(IconSource));
        OnPropertyChanged(nameof(HasIcon));
        NotifyState();
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsAssigned));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(QuickStatusText));
        OnPropertyChanged(nameof(VolumeDisplayText));
        NotifyMediaState();
    }

    private void SetAudioActivity(bool value)
    {
        if (_hasAudioActivity == value) return;
        _hasAudioActivity = value;
        OnPropertyChanged(nameof(HasAudioActivity));
        NotifyMediaState();
    }

    private void UpdateAudioActivity(double rawPeak)
    {
        var now = DateTime.UtcNow;
        if (rawPeak >= 0.25) _lastAudioActivityAt = now;
        SetAudioActivity(now - _lastAudioActivityAt < TimeSpan.FromSeconds(1.8));
        OnPropertyChanged(nameof(StatusText));
    }

    private static MediaThumbnail? LoadThumbnail(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;

        try
        {
            int sourceWidth;
            int sourceHeight;
            using (var metadataStream = new MemoryStream(bytes, writable: false))
            {
                var decoder = BitmapDecoder.Create(
                    metadataStream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                sourceWidth = decoder.Frames[0].PixelWidth;
                sourceHeight = decoder.Frames[0].PixelHeight;
            }

            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (sourceWidth > 1024) image.DecodePixelWidth = 1024;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            // A small source is kept as a foreground cover only. Stretching it over
            // the entire media surface exposes its pixels even with high-quality
            // interpolation, so the card uses its sampled colour palette instead.
            var canFillBackdrop = sourceWidth >= 512 && sourceHeight >= 256;
            return new MediaThumbnail(image, CreateMediaBackdrop(image), canFillBackdrop);
        }
        catch
        {
            return null;
        }
    }

    private static Brush CreateDefaultMediaBackdrop()
    {
        var brush = new LinearGradientBrush(
            Color.FromRgb(24, 29, 42),
            Color.FromRgb(12, 17, 26),
            0);
        brush.Freeze();
        return brush;
    }

    private static Brush CreateMediaBackdrop(BitmapSource image)
    {
        try
        {
            const int sampleSize = 32;
            var scale = Math.Min(1d, sampleSize / (double)Math.Max(image.PixelWidth, image.PixelHeight));
            BitmapSource sample = scale < 1
                ? new TransformedBitmap(image, new ScaleTransform(scale, scale))
                : image;
            var converted = new FormatConvertedBitmap(sample, PixelFormats.Bgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);

            long red = 0;
            long green = 0;
            long blue = 0;
            long weightTotal = 0;
            for (var index = 0; index < pixels.Length; index += 4)
            {
                var alpha = pixels[index + 3];
                if (alpha < 24) continue;
                var weight = Math.Max(1, alpha / 32);
                blue += pixels[index] * weight;
                green += pixels[index + 1] * weight;
                red += pixels[index + 2] * weight;
                weightTotal += weight;
            }

            if (weightTotal == 0) return CreateDefaultMediaBackdrop();

            static byte Tone(long total, long weight) =>
                (byte)Math.Clamp(10 + (total / (double)weight * 0.34), 10, 72);

            var sampled = Color.FromRgb(
                Tone(red, weightTotal),
                Tone(green, weightTotal),
                Tone(blue, weightTotal));
            var brush = new LinearGradientBrush(sampled, Color.FromRgb(12, 17, 26), 0);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return CreateDefaultMediaBackdrop();
        }
    }

    private sealed record MediaThumbnail(BitmapImage Image, Brush BackdropBrush, bool CanFillBackdrop);

    private static string FormatMediaTime(TimeSpan value) =>
        value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private void NotifyMediaState()
    {
        OnPropertyChanged(nameof(MediaHeading));
        OnPropertyChanged(nameof(MediaTitle));
        OnPropertyChanged(nameof(MediaSubtitle));
        OnPropertyChanged(nameof(StatusText));
    }
}
