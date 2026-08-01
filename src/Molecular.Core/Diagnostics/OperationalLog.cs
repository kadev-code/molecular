using System.Text;

namespace Molecular.Core.Diagnostics;

/// <summary>
/// Circular operational log under %LOCALAPPDATA%\Molecular\Logs.
/// Does not record media titles, artists, or other personal playback metadata.
/// </summary>
public sealed class OperationalLog
{
    public const long MaxFileBytes = 256 * 1024;
    public const int MaxRotatedFiles = 3;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _activePath;

    public OperationalLog(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Molecular",
            "Logs");
        _activePath = Path.Combine(_directory, "operational.log");
    }

    public static OperationalLog Shared { get; } = new();

    public string DirectoryPath => _directory;
    public string ActiveFilePath => _activePath;

    public void Info(string category, string message) => Write("INFO", category, message);

    public void Warn(string category, string message) => Write("WARN", category, message);

    public void Error(string category, string message) => Write("ERROR", category, message);

    public IReadOnlyList<string> ReadRecentLines(int maxLines = 400)
    {
        lock (_gate)
        {
            try
            {
                if (!Directory.Exists(_directory)) return Array.Empty<string>();

                var lines = new List<string>();
                foreach (var path in EnumerateLogFilesOldestFirst().Reverse())
                {
                    if (!File.Exists(path)) continue;
                    var fileLines = File.ReadAllLines(path);
                    lines.InsertRange(0, fileLines);
                    if (lines.Count >= maxLines) break;
                }

                if (lines.Count <= maxLines) return lines;
                return lines.Skip(lines.Count - maxLines).ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }

    private void Write(string level, string category, string message)
    {
        var safeCategory = Sanitize(category);
        var safeMessage = Sanitize(message);
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {safeCategory}: {safeMessage}{Environment.NewLine}";

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                RotateIfNeeded(Encoding.UTF8.GetByteCount(line));
                File.AppendAllText(_activePath, line, Encoding.UTF8);
            }
            catch
            {
                // Logging must never break the mixer.
            }
        }
    }

    private void RotateIfNeeded(int upcomingBytes)
    {
        if (!File.Exists(_activePath)) return;
        var length = new FileInfo(_activePath).Length;
        if (length + upcomingBytes <= MaxFileBytes) return;

        for (var index = MaxRotatedFiles - 1; index >= 1; index--)
        {
            var source = RotatedPath(index);
            var destination = RotatedPath(index + 1);
            if (!File.Exists(source)) continue;
            if (index + 1 > MaxRotatedFiles)
            {
                File.Delete(source);
                continue;
            }

            File.Copy(source, destination, overwrite: true);
            File.Delete(source);
        }

        if (File.Exists(_activePath))
        {
            File.Copy(_activePath, RotatedPath(1), overwrite: true);
            File.Delete(_activePath);
        }
    }

    private string RotatedPath(int index) => Path.Combine(_directory, $"operational.{index}.log");

    private IEnumerable<string> EnumerateLogFilesOldestFirst()
    {
        for (var index = MaxRotatedFiles; index >= 1; index--)
        {
            var path = RotatedPath(index);
            if (File.Exists(path)) yield return path;
        }

        if (File.Exists(_activePath)) yield return _activePath;
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(vazio)";
        var trimmed = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return trimmed.Length <= 400 ? trimmed : trimmed[..400] + "…";
    }
}
