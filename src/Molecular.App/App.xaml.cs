using System.IO;
using System.Windows;
using System.Windows.Threading;
using Molecular.Core.Runtime;

namespace Molecular.App;

public partial class App : System.Windows.Application
{
    private const string ApplicationId = "Molecular.PersonalAudioMixer";
    private SingleInstanceCoordinator? _singleInstance;
    private TrayIconService? _trayIcon;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        try
        {
            if (!SingleInstanceCoordinator.TryAcquire(ApplicationId, out _singleInstance))
            {
                Shutdown(0);
                return;
            }

            var window = new MainWindow();
            MainWindow = window;
            _singleInstance!.ActivationRequested += (_, _) => Dispatcher.BeginInvoke(window.ShowFromTray);
            _trayIcon = new TrayIconService(
                window.ShowFromTray,
                () => window.SetGlobalMute(true),
                () => window.SetGlobalMute(false),
                window.ExitApplication);
            window.Show();
        }
        catch (Exception exception)
        {
            WriteCrashLog(exception);
            MessageBox.Show(
                $"O Molecular não conseguiu iniciar.\n\n{exception.Message}\n\nUm diagnóstico foi salvo em Molecular\\Logs.",
                "Molecular — erro de inicialização",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void Application_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        MessageBox.Show(
            $"O Molecular encontrou um erro inesperado.\n\n{e.Exception.Message}\n\nO diagnóstico foi salvo em Molecular\\Logs.",
            "Molecular — erro inesperado",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Molecular",
                "Logs");
            Directory.CreateDirectory(logDirectory);
            var logFile = Path.Combine(logDirectory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(logFile, exception.ToString());
        }
        catch
        {
            // Diagnostic logging must never replace the original error.
        }
    }
}
