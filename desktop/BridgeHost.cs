using System;
using System.Text.Json;
using System.Threading.Tasks;
using DyOne.Desktop.Broker;
using Microsoft.Web.WebView2.Core;

namespace DyOne.Desktop;

/// <summary>
/// React(웹) ↔ .NET 중계. 요청 { id, cmd, args } → 응답 { id, ok, result | error }, 진행 알림은 { event, data }
/// 앱 공통 명령(app.info · notify.toast · shell.openExternal)은 여기서, PC 작업은 Local Broker 모듈에서 처리한다.
/// </summary>
public sealed class BridgeHost
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CoreWebView2 _core;
    private readonly TrayIcon _tray;
    private readonly AppConfig _config;
    private readonly BrokerRegistry _broker;

    public BridgeHost(CoreWebView2 core, MainWindow window, TrayIcon tray, AppConfig config, BrokerRegistry broker, BrokerContext brokerContext)
    {
        _core = core;
        _tray = tray;
        _config = config;
        _broker = broker;
        _core.WebMessageReceived += OnWebMessageReceived;
        // Broker 진행 이벤트 { event, data } → 웹(bridge.js의 on()). WebView2는 UI 스레드에서만 보낼 수 있다.
        brokerContext.Emit = (name, data) => window.Dispatcher.BeginInvoke(() =>
        {
            if (_config.IsTrusted(_core.Source)) _core.PostWebMessageAsJson(JsonSerializer.Serialize(new { @event = name, data }, Json));
        });
    }

    private sealed record BridgeRequest(string Id, string Cmd, JsonElement Args);

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // 보안 규칙 1: DY ONE 주소에서 온 요청만 처리한다.
        if (!_config.IsTrusted(e.Source)) return;

        BridgeRequest? req;
        try { req = JsonSerializer.Deserialize<BridgeRequest>(e.WebMessageAsJson, Json); }
        catch (JsonException) { return; }
        if (req is null || string.IsNullOrEmpty(req.Id) || string.IsNullOrEmpty(req.Cmd)) return;

        try
        {
            Reply(req.Id, ok: true, await HandleAsync(req.Cmd, req.Args), error: null);
        }
        catch (BrokerException ex)
        {
            Reply(req.Id, ok: false, result: null, ex.Message);
        }
        catch (Exception)
        {
            Reply(req.Id, ok: false, result: null, "처리 중 오류가 발생했습니다.");
        }
    }

    private Task<object?> HandleAsync(string cmd, JsonElement args)
    {
        switch (cmd)
        {
            case "app.info":
                return Task.FromResult<object?>(new
                {
                    app = "DY ONE Desktop",
                    version = typeof(BridgeHost).Assembly.GetName().Version?.ToString(3),
                    dotnet = Environment.Version.ToString(),
                    webView2 = CoreWebView2Environment.GetAvailableBrowserVersionString(),
                    appUrl = _config.Origin,
                });
            case "notify.toast":
                _tray.ShowToast(Limit(BrokerRegistry.GetString(args, "title") ?? "DY ONE", 60), Limit(BrokerRegistry.GetString(args, "body") ?? "", 200),
                    BrokerRegistry.GetString(args, "link") is { } link && (MainWindow.IsAppLink(link) || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ? link : null);
                return Task.FromResult<object?>(new { shown = true });
            case "shell.openExternal":
                if (!ExternalLinks.Open(BrokerRegistry.GetString(args, "url")))
                    throw new BrokerException("https 주소만 열 수 있습니다.");
                return Task.FromResult<object?>(new { opened = true });
        }

        // 보안 규칙 2: 등록된 Broker 명령만 허용한다.
        if (_broker.TryGet(cmd, out var handler)) return handler(args);
        throw new BrokerException($"허용되지 않은 명령입니다: {cmd}");
    }

    private void Reply(string id, bool ok, object? result, string? error) =>
        _core.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok, result, error }, Json));

    private static string Limit(string s, int max) => s.Length <= max ? s : s[..max];
}
