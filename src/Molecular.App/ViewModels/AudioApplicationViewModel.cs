using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Molecular.Core.Models;

namespace Molecular.App.ViewModels;

public sealed class AudioApplicationViewModel : ObservableObject
{
    private static readonly Dictionary<string, ApplicationIconSet> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object IconCacheLock = new();

    // Official Spotify icon geometry. Keeping the known mark as WPF vector data
    // avoids routing a large PNG through the compositor for every 32/48-DIP use.
    private const string SpotifyIconGeometry =
        "m122.37,3.31C61.99.91,11.1,47.91,8.71,108.29c-2.4,60.38,44.61,111.26,104.98,113.66,60.38,2.4,111.26-44.6,113.66-104.98C229.74,56.59,182.74,5.7,122.37,3.31Zm46.18,160.28c-1.36,2.4-4.01,3.6-6.59,3.24-.79-.11-1.58-.37-2.32-.79-14.46-8.23-30.22-13.59-46.84-15.93-16.62-2.34-33.25-1.53-49.42,2.4-3.51.85-7.04-1.3-7.89-4.81-.85-3.51,1.3-7.04,4.81-7.89,17.78-4.32,36.06-5.21,54.32-2.64,18.26,2.57,35.58,8.46,51.49,17.51,3.13,1.79,4.23,5.77,2.45,8.91Zm14.38-28.72c-2.23,4.12-7.39,5.66-11.51,3.43-16.92-9.15-35.24-15.16-54.45-17.86-19.21-2.7-38.47-1.97-57.26,2.16-1.02.22-2.03.26-3.01.12-3.41-.48-6.33-3.02-7.11-6.59-1.01-4.58,1.89-9.11,6.47-10.12,20.77-4.57,42.06-5.38,63.28-2.4,21.21,2.98,41.46,9.62,60.16,19.74,4.13,2.23,5.66,7.38,3.43,11.51Zm15.94-32.38c-2.1,4.04-6.47,6.13-10.73,5.53-1.15-.16-2.28-.52-3.37-1.08-19.7-10.25-40.92-17.02-63.07-20.13-22.15-3.11-44.42-2.45-66.18,1.97-5.66,1.15-11.17-2.51-12.32-8.16-1.15-5.66,2.51-11.17,8.16-12.32,24.1-4.89,48.74-5.62,73.25-2.18,24.51,3.44,47.99,10.94,69.81,22.29,5.12,2.66,7.11,8.97,4.45,14.09Z";

    private string _displayName;
    private string _executableName;
    private string? _executablePath;
    private double _volume;
    private double _peak;
    private bool _isMuted;
    private ImageSource? _compactIconSource;
    private ImageSource? _expandedIconSource;
    private string? _iconLoadPath;

    public AudioApplicationViewModel(AudioApplication application)
    {
        Key = application.Key;
        _displayName = application.DisplayName;
        _executableName = application.ExecutableName;
        _executablePath = application.ExecutablePath;
        Update(application);
    }

    public string Key { get; }
    public string DisplayName { get => _displayName; private set { if (SetProperty(ref _displayName, value)) OnPropertyChanged(nameof(Initials)); } }
    public string ExecutableName { get => _executableName; private set => SetProperty(ref _executableName, value); }
    public string? ExecutablePath { get => _executablePath; private set => SetProperty(ref _executablePath, value); }
    public string Initials => string.IsNullOrWhiteSpace(DisplayName) ? "?" : DisplayName[..1].ToUpperInvariant();
    public double Volume { get => _volume; private set => SetProperty(ref _volume, value); }
    public double Peak { get => _peak; private set => SetProperty(ref _peak, value); }
    public bool IsMuted { get => _isMuted; private set => SetProperty(ref _isMuted, value); }
    public ImageSource? CompactIconSource { get => _compactIconSource; private set => SetProperty(ref _compactIconSource, value); }
    public ImageSource? ExpandedIconSource { get => _expandedIconSource; private set => SetProperty(ref _expandedIconSource, value); }
    public ImageSource? IconSource => ExpandedIconSource;
    public bool HasIcon => CompactIconSource is not null || ExpandedIconSource is not null;

    public void Update(AudioApplication application)
    {
        DisplayName = application.DisplayName;
        ExecutableName = application.ExecutableName;
        ExecutablePath = application.ExecutablePath;
        Volume = application.Volume;
        Peak = application.Peak;
        IsMuted = application.IsMuted;
        EnsureIconLoaded();
    }

    private async void EnsureIconLoaded()
    {
        var path = ExecutablePath;
        if (HasIcon
            || string.IsNullOrWhiteSpace(path)
            || string.Equals(_iconLoadPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _iconLoadPath = path;
        var icons = await Task.Run(() => LoadIconSetFromPath(path));
        if (!icons.HasAny || !string.Equals(ExecutablePath, path, StringComparison.OrdinalIgnoreCase)) return;

        CompactIconSource = icons.Compact;
        ExpandedIconSource = icons.Expanded;
        OnPropertyChanged(nameof(IconSource));
        OnPropertyChanged(nameof(HasIcon));
    }

    public static ImageSource? LoadIconFromPath(string? executablePath) =>
        LoadIconSetFromPath(executablePath).Expanded;

    private static ApplicationIconSet LoadIconSetFromPath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) return default;

        lock (IconCacheLock)
        {
            if (IconCache.TryGetValue(executablePath, out var cached)) return cached;
        }

        var vector = LoadKnownVectorIcon(executablePath);
        var compact = vector
            ?? LoadShellIcon(executablePath, ShellImageListLarge)
            ?? LoadHighResolutionIcon(executablePath, 32)
            ?? LoadAssociatedIcon(executablePath);
        var expanded = vector
            ?? LoadShellIcon(executablePath, ShellImageListExtraLarge)
            ?? LoadHighResolutionIcon(executablePath, 48)
            ?? compact;
        compact ??= expanded;

        var icons = new ApplicationIconSet(compact, expanded);
        lock (IconCacheLock) IconCache[executablePath] = icons;
        return icons;
    }

    private static ImageSource? LoadKnownVectorIcon(string executablePath)
    {
        var executableName = Path.GetFileName(executablePath);
        if (!string.Equals(executableName, "spotify.exe", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            var geometry = Geometry.Parse(SpotifyIconGeometry);
            var fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 215, 96));
            fill.Freeze();
            geometry.Freeze();
            var drawing = new GeometryDrawing(fill, null, geometry);
            drawing.Freeze();
            var image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? LoadShellIcon(string executablePath, int imageListSize)
    {
        var fileInfo = new ShellFileInfo();
        IImageList? imageList = null;
        IntPtr iconHandle = IntPtr.Zero;
        try
        {
            var result = SHGetFileInfo(
                executablePath,
                0,
                ref fileInfo,
                (uint)Marshal.SizeOf<ShellFileInfo>(),
                ShellGetFileInfoSysIconIndex);
            if (result == IntPtr.Zero || fileInfo.IconIndex < 0) return null;

            var imageListId = typeof(IImageList).GUID;
            if (SHGetImageList(imageListSize, ref imageListId, out imageList) < 0 || imageList is null)
                return null;
            if (imageList.GetIcon(fileInfo.IconIndex, ImageListDrawTransparent, out iconHandle) < 0
                || iconHandle == IntPtr.Zero)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHIcon(
                iconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
            if (imageList is not null && Marshal.IsComObject(imageList)) Marshal.FinalReleaseComObject(imageList);
        }
    }

    private static BitmapSource? LoadHighResolutionIcon(string executablePath, int size)
    {
        var handles = new IntPtr[1];
        var iconIds = new uint[1];
        try
        {
            if (PrivateExtractIcons(executablePath, 0, size, size, handles, iconIds, 1, 0) == 0 || handles[0] == IntPtr.Zero)
                return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(
                handles[0],
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (handles[0] != IntPtr.Zero) DestroyIcon(handles[0]);
        }
    }

    private static BitmapSource? LoadAssociatedIcon(string executablePath)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(executablePath);
            if (icon is null) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PrivateExtractIcons(
        string fileName,
        int iconIndex,
        int iconWidth,
        int iconHeight,
        IntPtr[] iconHandles,
        uint[] iconIds,
        uint iconCount,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    private const uint ShellGetFileInfoSysIconIndex = 0x000004000;
    private const int ShellImageListLarge = 0;
    private const int ShellImageListExtraLarge = 2;
    private const int ImageListDrawTransparent = 0x00000001;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("shell32.dll", EntryPoint = "SHGetImageList")]
    private static extern int SHGetImageList(
        int imageList,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IImageList? imageListResult);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr image, IntPtr mask, out int index);
        [PreserveSig] int ReplaceIcon(int index, IntPtr icon, out int resultingIndex);
        [PreserveSig] int SetOverlayImage(int imageIndex, int overlayIndex);
        [PreserveSig] int Replace(int index, IntPtr image, IntPtr mask);
        [PreserveSig] int AddMasked(IntPtr image, uint maskColor, out int index);
        [PreserveSig] int Draw(IntPtr drawParameters);
        [PreserveSig] int Remove(int index);
        [PreserveSig] int GetIcon(int index, int flags, out IntPtr icon);
    }

    private readonly record struct ApplicationIconSet(ImageSource? Compact, ImageSource? Expanded)
    {
        public bool HasAny => Compact is not null || Expanded is not null;
    }
}
