using System.Drawing;
using Forms = System.Windows.Forms;

namespace Molecular.App;

internal sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;

    public TrayIconService(Action open, Action muteAll, Action restoreAudio, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir Molecular", null, (_, _) => open());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Silenciar tudo", null, (_, _) => muteAll());
        menu.Items.Add("Restaurar áudio", null, (_, _) => restoreAudio());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => exit());

        var executablePath = Environment.ProcessPath;
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Molecular — Personal Audio Mixer",
            Icon = executablePath is null ? SystemIcons.Application : Icon.ExtractAssociatedIcon(executablePath),
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => open();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
    }
}
