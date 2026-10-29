using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using DyOne.Desktop.Broker;
using Microsoft.Web.WebView2.Core;

namespace DyOne.Desktop;

public partial class MainWindow : Window
{
    private readonly TrayIcon _tray;
    private AppConfig? _config;
    private BridgeHost? _bridge;
    private bool _exiting;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        SetWindowIcon();
        _tray = new TrayIcon(this);
        Loaded += async (_, _) => await InitWebViewAsync();
        Closing += OnClosing;
    }

    // dy-one.ico는 모든 크기가 PNG 압축이라 WPF 디코더가 읽지 못한다. 정식 아이콘이 나오기 전까지 PNG를 쓴다.
    private void SetWindowIcon()
    {
        try
        {
            Icon = BitmapFrame.Create(new Uri(Path.Combine(AppContext.BaseDirectory, "assets", "icon-192.png")));
        }
        catch (Exception)
        {
            // 아이콘이 없어도 앱은 동작해야 한다.
        }
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            _config = AppConfig.Load();
            var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DY ONE", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await Web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "Microsoft Edge WebView2 런타임이 필요합니다.", "DY ONE", MessageBoxButton.OK, MessageBoxImage.Error);
            ExitApp();
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "DY ONE 시작 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            ExitApp();
            return;
        }

        var core = Web.CoreWebView2;
        var config = _config;
        core.Settings.IsStatusBarEnabled = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif

        // 보안 규칙 1: 앱 안에서는 DY ONE 주소만 연다. 그 밖의 링크는 기본 브라우저로 넘긴다.
        core.NavigationStarting += (_, e) =>
        {
            if (!config.IsTrusted(e.Uri))
            {
                e.Cancel = true;
                ExternalLinks.Open(e.Uri);
            }
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            ExternalLinks.Open(e.Uri);
        };

        var brokerContext = new BrokerContext(config, this);
        _bridge = new BridgeHost(core, this, _tray, config, BrokerRegistry.CreateDefault(brokerContext), brokerContext);
        var script = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "bridge.js")))
            .Replace("__DYONE_ORIGIN__", config.Origin);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(script);

        core.Navigate(config.AppUrl.ToString());
    }

    // 창을 닫으면 종료하지 않고 트레이로 내려가 알림을 계속 받는다.
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        Hide();
        if (!_trayHintShown)
        {
            _tray.ShowToast("DY ONE", "트레이에서 계속 실행 중입니다. 종료는 트레이 아이콘 우클릭 → 종료.");
            _trayHintShown = true;
        }
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>앱 안 화면 경로만 허용한다: '/'로 시작하고 다른 주소(//, 스킴)나 따옴표·공백이 없는 짧은 경로.</summary>
    public static bool IsAppLink(string? link) =>
        link is { Length: > 1 and <= 300 } && Regex.IsMatch(link, @"^/(?!/)[A-Za-z0-9_\-/?=&.%]*$");

    /// <summary>알림을 눌렀을 때: 화면(HashRouter)을 그 경로로 옮긴다(새로 고치지 않고 화면 안에서 이동).</summary>
    public void OpenAppLink(string link)
    {
        if (!IsAppLink(link) || Web.CoreWebView2 is not { } core || _config is null) return;
        if (!_config.IsTrusted(core.Source)) return;
        _ = core.ExecuteScriptAsync($"location.hash = {JsonSerializer.Serialize("#" + link)}");
    }

    public void ExitApp()
    {
        _exiting = true;
        _tray.Dispose();
        Application.Current.Shutdown();
    }
}
