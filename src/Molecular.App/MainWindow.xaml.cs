using System.Diagnostics.CodeAnalysis;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Molecular.App.ViewModels;
using Molecular.Core.Audio;
using Molecular.Core.Persistence;

namespace Molecular.App;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The ViewModel is disposed deterministically by the Window.Closed handler.")]
public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private static readonly TimeSpan ForegroundPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan BackgroundPollInterval = TimeSpan.FromSeconds(1);
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;
    private HwndSource? _windowSource;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel(new WindowsAudioSessionService(), new ProfileStore());
        DataContext = _viewModel;

        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => AppFrame.CornerRadius = WindowState == WindowState.Maximized
            ? new CornerRadius(0)
            : new CornerRadius(16);
        SizeChanged += (_, _) =>
        {
            _viewModel.UpdateViewportWidth(ActualWidth);
            _viewModel.UpdateDpiScale(VisualTreeHelper.GetDpi(this).DpiScaleX);
        };
        Closing += OnClosing;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(50, _viewModel.MeterIntervalMs))
        };
        _refreshTimer.Tick += async (_, _) => await _viewModel.TickAsync();
        _viewModel.MeterIntervalChanged += (_, _) =>
            _refreshTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, _viewModel.MeterIntervalMs));
        Loaded += async (_, _) =>
        {
            _viewModel.UpdateDpiScale(VisualTreeHelper.GetDpi(this).DpiScaleX);
            await _viewModel.TickAsync();
            _refreshTimer.Start();
            if (_viewModel.ShouldStartInTray)
                HideToTray();
        };
        Closed += (_, _) =>
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _refreshTimer.Stop();
            _windowSource?.RemoveHook(WindowMessageHook);
            _viewModel.Dispose();
        };
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Dispatcher.BeginInvoke(() => _viewModel.NotifyPowerResumed());
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return IntPtr.Zero;

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var workArea = monitorInfo.WorkArea;
        var monitorArea = monitorInfo.MonitorArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        minMaxInfo.MinTrackSize.X = (int)Math.Ceiling(MinWidth * dpi.DpiScaleX);
        minMaxInfo.MinTrackSize.Y = (int)Math.Ceiling(MinHeight * dpi.DpiScaleY);
        minMaxInfo.MaxPosition.X = Math.Abs(workArea.Left - monitorArea.Left);
        minMaxInfo.MaxPosition.Y = Math.Abs(workArea.Top - monitorArea.Top);
        minMaxInfo.MaxSize.X = Math.Abs(workArea.Right - workArea.Left);
        minMaxInfo.MaxSize.Y = Math.Abs(workArea.Bottom - workArea.Top);
        Marshal.StructureToPtr(minMaxInfo, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            var pointer = e.GetPosition(this);
            var screenPointer = PointToScreen(pointer);
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is not null)
                screenPointer = source.CompositionTarget.TransformFromDevice.Transform(screenPointer);

            var horizontalRatio = ActualWidth <= 0 ? 0.5 : pointer.X / ActualWidth;
            var restoredWidth = RestoreBounds.Width;
            WindowState = WindowState.Normal;
            Left = screenPointer.X - (restoredWidth * horizontalRatio);
            Top = Math.Max(0, screenPointer.Y - 24);
        }

        DragMove();
    }

    private void AudioDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsFlyout.Visibility = Visibility.Collapsed;
        OutputDeviceComboBox.Focus();
        OutputDeviceComboBox.IsDropDownOpen = true;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsFlyout.Visibility = SettingsFlyout.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CloseToTray) HideToTray();
        else ExitApplication();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (_viewModel.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _allowClose = true;
    }

    private void HideToTray()
    {
        SetBackgroundPolling(true);
        Hide();
        ShowInTaskbar = false;
    }

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        SetBackgroundPolling(false);
    }

    private void SetBackgroundPolling(bool background)
    {
        _viewModel.SetBackgroundMode(background);
        _refreshTimer.Interval = background
            ? BackgroundPollInterval
            : TimeSpan.FromMilliseconds(Math.Max(50, _viewModel.MeterIntervalMs));
    }

    public void SetGlobalMute(bool muted) => _viewModel.SetGlobalMute(muted);

    public void ExitApplication()
    {
        _allowClose = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void VolumeSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsEnabled) return;

        var step = e.Delta > 0 ? 1d : -1d;
        var maximum = slider.DataContext is ChannelViewModel channel
            ? Math.Min(slider.Maximum, channel.MaximumVolume)
            : slider.Maximum;
        slider.Value = Math.Clamp(Math.Round(slider.Value) + step, slider.Minimum, maximum);
        e.Handled = true;
    }

    private void ChannelVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is Slider { DataContext: ChannelViewModel channel } slider
            && slider.Value > channel.MaximumVolume)
        {
            slider.Value = channel.MaximumVolume;
        }
    }

    private void AddApplicationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AudioApplicationViewModel application })
            _viewModel.AssignApplicationCommand.Execute(application);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRectangle MonitorArea;
        public NativeRectangle WorkArea;
        public uint Flags;
    }
}
