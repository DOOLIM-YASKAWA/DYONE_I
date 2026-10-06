using System;
using System.IO;
using System.Text.Json;

namespace DyOne.Desktop;

/// <summary>
/// 앱 설정. 브리지는 AppUrl과 같은 출처(scheme + host + port)에서 온 요청만 받는다.
/// 환경 변수 DYONE_APP_URL이 있으면 설정 파일보다 우선한다(개발·테스트용).
/// </summary>
public sealed class AppConfig
{
    public Uri AppUrl { get; }

    private AppConfig(Uri appUrl) => AppUrl = appUrl;

    public static AppConfig Load()
    {
        var url = Environment.GetEnvironmentVariable("DYONE_APP_URL");
        if (string.IsNullOrWhiteSpace(url))
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            url = doc.RootElement.GetProperty("AppUrl").GetString();
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"AppUrl이 올바르지 않습니다: {url}");
        // 운영 주소는 HTTPS만 허용한다. HTTP는 로컬 개발 서버(localhost)에서만 쓴다.
        if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
            throw new InvalidOperationException("AppUrl은 https 주소여야 합니다(localhost 개발 서버 제외).");
        return new AppConfig(uri);
    }

    public bool IsTrusted(string? candidate) =>
        Uri.TryCreate(candidate, UriKind.Absolute, out var u)
        && u.Scheme == AppUrl.Scheme
        && string.Equals(u.Host, AppUrl.Host, StringComparison.OrdinalIgnoreCase)
        && u.Port == AppUrl.Port;

    public string Origin => AppUrl.GetLeftPart(UriPartial.Authority);
}
