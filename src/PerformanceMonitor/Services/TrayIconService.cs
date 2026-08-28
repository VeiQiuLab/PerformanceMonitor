using System.Drawing;
using System.Windows.Forms;

namespace PerformanceMonitor.Services;

internal sealed class TrayIconService : IDisposable
{
    private readonly Icon _icon;
    private readonly NotifyIcon _notifyIcon;
    private bool _hideNoticeShown;

    public TrayIconService(Action showWindow, Action showSettings, Action exit)
    {
        _icon = LoadApplicationIcon();
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("Open", null, (_, _) => showWindow());
        menu.Items.Add("Settings", null, (_, _) => showSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _notifyIcon = new NotifyIcon
        {
            Text = "Performance Monitor",
            Icon = _icon,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => showWindow();
    }

    public void ShowHiddenNotice()
    {
        if (_hideNoticeShown)
        {
            return;
        }

        _hideNoticeShown = true;
        _notifyIcon.BalloonTipTitle = "Performance Monitor";
        _notifyIcon.BalloonTipText = "Performance Monitor is still running in the system tray.";
        _notifyIcon.ShowBalloonTip(2500);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _icon.Dispose();
    }

    private static Icon LoadApplicationIcon()
    {
        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var extracted = Icon.ExtractAssociatedIcon(executablePath);
            if (extracted is not null)
            {
                return extracted;
            }
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}
