using System.Reflection;
using System.Text;

namespace Molecular.Core.Diagnostics;

public sealed record DiagnosticsSnapshot(
    string ProductVersion,
    string FileVersion,
    string FrameworkDescription,
    string OsDescription,
    string MachineNameHash,
    double DpiScalePercent,
    string OutputDeviceName,
    int DetectedApplicationCount,
    int ChannelCount,
    int ExpandedChannelCount,
    string StatusMessage,
    bool StartWithWindows,
    IReadOnlyList<string> RecentLogLines);

public static class DiagnosticsExporter
{
    public static string BuildReport(DiagnosticsSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Molecular — diagnóstico operacional");
        builder.AppendLine($"Gerado em: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine();
        builder.AppendLine("== Ambiente ==");
        builder.AppendLine($"Produto: {snapshot.ProductVersion}");
        builder.AppendLine($"Arquivo: {snapshot.FileVersion}");
        builder.AppendLine($"Runtime: {snapshot.FrameworkDescription}");
        builder.AppendLine($"Sistema: {snapshot.OsDescription}");
        builder.AppendLine($"Host (hash): {snapshot.MachineNameHash}");
        builder.AppendLine($"Escala DPI: {snapshot.DpiScalePercent:0.#}%");
        builder.AppendLine();
        builder.AppendLine("== Mixer ==");
        builder.AppendLine($"Dispositivo: {snapshot.OutputDeviceName}");
        builder.AppendLine($"Aplicativos detectados: {snapshot.DetectedApplicationCount}");
        builder.AppendLine($"Canais: {snapshot.ChannelCount}");
        builder.AppendLine($"Canais expandidos: {snapshot.ExpandedChannelCount}");
        builder.AppendLine($"Status: {snapshot.StatusMessage}");
        builder.AppendLine($"Iniciar com Windows: {(snapshot.StartWithWindows ? "sim" : "não")}");
        builder.AppendLine();
        builder.AppendLine("== Log recente ==");
        builder.AppendLine("(sem títulos de mídia / artistas)");
        if (snapshot.RecentLogLines.Count == 0)
            builder.AppendLine("(vazio)");
        else
            foreach (var line in snapshot.RecentLogLines)
                builder.AppendLine(line);

        builder.AppendLine();
        builder.AppendLine("== Privacidade ==");
        builder.AppendLine("Este arquivo não inclui títulos de músicas, artistas ou miniaturas.");
        return builder.ToString();
    }

    public static void WriteReport(string filePath, DiagnosticsSnapshot snapshot)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(filePath, BuildReport(snapshot), Encoding.UTF8);
    }

    public static string ResolveProductVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "desconhecida";
    }

    public static string ResolveFileVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
            ?? assembly.GetName().Version?.ToString()
            ?? "desconhecida";
    }
}
