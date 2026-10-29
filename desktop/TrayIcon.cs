using System;
using System.Drawing;
using System.IO;
using Forms = System.Windows.Forms;

namespace DyOne.Desktop;

// 트레이 상주 + Windows 알림. 웹 화면이 서버 알림(SignalR)을 받으면 브리지(notify.toast)로 ShowToast를 호출한다.
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    // 지금 떠 있는 알림의 화면 경로(예: /works/281?doc=73) 또는 그룹웨어 문서 주소. 알림을 누르면 그 문서로 간다.
    private string? _toastLink;

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
        _icon.BalloonTipClicked += (_, _) =>
        {
            window.ShowFromTray();
            // 앱 안 경로는 그 화면으로, 그룹웨어 문서 주소(https)는 기본 브라우저로 연다
            if (_toastLink is { } link)
            {
                if (link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ExternalLinks.Open(link);
                else window.OpenAppLink(link);
            }
            _toastLink = null;
        };
        _icon.BalloonTipClosed += (_, _) => _toastLink = null;
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

    /// <param name="link">알림을 눌렀을 때 열 DY ONE 화면 경로(없으면 창만 연다)</param>
    public void ShowToast(string title, string body, string? link = null)
    {
        _toastLink = link;
        _icon.ShowBalloonTip(5000, title, body, Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
