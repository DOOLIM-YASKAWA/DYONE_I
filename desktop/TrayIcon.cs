using System;
using System.Drawing;
using System.IO;
using Forms = System.Windows.Forms;

namespace DyOne.Desktop;

// 트레이 상주 + Windows 알림. 웹 화면이 서버 알림(SignalR)을 받으면 브리지(notify.toast)로 ShowToast를 호출한다.
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(MainWindow window)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("열기", null, (_, _) => window.ShowFromTray());
        menu.Items.Add("종료", null, (_, _) => window.ExitApp());

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "DY ONE",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => window.ShowFromTray();
        _icon.BalloonTipClicked += (_, _) => window.ShowFromTray();
    }

    private static Icon LoadIcon()
    {
        try
        {
            return new Icon(Path.Combine(AppContext.BaseDirectory, "assets", "dy-one.ico"), 16, 16);
        }
        catch (Exception)
        {
            return SystemIcons.Application;
        }
    }

    public void ShowToast(string title, string body) =>
        _icon.ShowBalloonTip(5000, title, body, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
