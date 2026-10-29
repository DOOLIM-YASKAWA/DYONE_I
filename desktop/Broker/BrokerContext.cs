using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace DyOne.Desktop.Broker;

/// <summary>
/// Broker 모듈이 함께 쓰는 것: 앱 설정, 창, 웹으로 진행 이벤트 보내기, DY ONE 서버 호출, 오래 걸리는 작업 취소.
/// 서버 호출은 앱 주소(AppUrl)와 같은 출처의 /api/ 로만 간다. 토큰은 웹이 명령마다 넘겨준다(앱은 저장하지 않는다).
/// </summary>
public sealed class BrokerContext(AppConfig config, Window owner)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobs = new(StringComparer.Ordinal);

    public AppConfig Config { get; } = config;
    public Window Owner { get; } = owner;

    /// <summary>웹으로 이벤트 보내기(BridgeHost가 연결). 어느 스레드에서 불러도 된다.</summary>
    public Action<string, object?> Emit { get; set; } = (_, _) => { };

    public static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>DY ONE 서버 요청. path는 반드시 /api/ 로 시작해야 한다.</summary>
    public HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        if (!path.StartsWith("/api/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal))
            throw new BrokerException("허용되지 않은 서버 경로입니다.");
        if (string.IsNullOrWhiteSpace(token)) throw new BrokerException("로그인 정보가 없습니다. 다시 로그인해 주세요.");
        var req = new HttpRequestMessage(method, new Uri(new Uri(Config.Origin), path));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    public static T? Parse<T>(string text) => JsonSerializer.Deserialize<T>(text, Json);

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Json);

    /// <summary>서버 오류 응답(ProblemDetails)에서 사람이 읽을 메시지를 뽑는다.</summary>
    public static string ProblemMessage(string body, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String) return d.GetString()!;
            if (doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) return t.GetString()!;
        }
        catch (JsonException) { }
        return fallback;
    }

    /// <summary>취소할 수 있는 작업 시작. jobId는 웹이 정한다(같은 id가 돌고 있으면 거부).</summary>
    public CancellationTokenSource StartJob(string jobId)
    {
        var cts = new CancellationTokenSource();
        if (!_jobs.TryAdd(jobId, cts)) throw new BrokerException("같은 작업이 이미 진행 중입니다.");
        return cts;
    }

    public void EndJob(string jobId)
    {
        if (_jobs.TryRemove(jobId, out var cts)) cts.Dispose();
    }

    public bool CancelJob(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var cts)) return false;
        cts.Cancel();
        return true;
    }
}
