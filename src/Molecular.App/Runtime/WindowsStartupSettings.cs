using System.IO;
using Microsoft.Win32;

namespace Molecular.App.Runtime;

public static class WindowsStartupSettings
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Molecular";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            var value = key?.GetValue(ValueName) as string;
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(executablePath)) return false;
            var matchesCurrentExecutable = string.Equals(
                value.Trim(),
                $"\"{executablePath}\"",
                StringComparison.OrdinalIgnoreCase);
            if (!matchesCurrentExecutable)
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return matchesCurrentExecutable;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
            ?? throw new InvalidOperationException("Não foi possível abrir a chave de inicialização do Windows.");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            throw new InvalidOperationException("Caminho do executável do Molecular indisponível.");

        key.SetValue(ValueName, $"\"{executablePath}\"");
    }
}
