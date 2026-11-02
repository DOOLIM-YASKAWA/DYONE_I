using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DyOne.Api.Platform;

/// <summary>대화 한 줄(role = system · user · assistant)</summary>
public record LlmMessage(string Role, string Content);

/// <summary>호출 옵션. Json = JSON 객체로만 답하게(response_format). MaxTokens 비우면 서버 기본.</summary>
public record LlmOptions(double Temperature = 0.2, bool Json = false, int? MaxTokens = null, int TimeoutSeconds = 120);

/// <summary>
/// 사내 AI 공통 클라이언트 — 모든 화면 기능·에이전트가 이것 하나로 부른다.
/// 서버: dy-linux SGLang(OpenAI 호환 /v1/chat/completions). 설정 Llm:BaseUrl · Llm:Model · Llm:ApiKey(user-secrets/환경변수, 저장소에 넣지 않음).
/// 키가 없으면 IsConfigured = false — 부르는 쪽은 규칙·기존 동작으로 돌아간다(AI 없이도 화면이 멈추지 않게).
/// 생각 과정(&lt;think&gt;)은 끄고, 혹시 섞여 오면 지운다. 기능 이름(feature)·걸린 시간·토큰 수를 로그로 남긴다.
/// </summary>
public sealed partial class Llm(IConfiguration config, IHttpClientFactory http, ILogger<Llm> log)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(config["Llm:ApiKey"]) && !string.IsNullOrWhiteSpace(config["Llm:BaseUrl"]);
    public string Model => config["Llm:Model"] ?? "qwen3.8-27b-fp8";

    /// <summary>글자로 답을 받는다.</summary>
    public async Task<string> ChatAsync(IReadOnlyList<LlmMessage> messages, string feature, LlmOptions? options = null, CancellationToken ct = default)
    {
        if (!IsConfigured) throw new LlmNotConfiguredException();
        var o = options ?? new LlmOptions();
        var client = http.CreateClient("llm");
        client.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
        using var req = new HttpRequestMessage(HttpMethod.Post, config["Llm:BaseUrl"]!.TrimEnd('/') + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["Llm:ApiKey"]);
        var body = new JsonObject
        {
            ["model"] = Model,
            ["temperature"] = o.Temperature,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            // Qwen 계열: 생각 과정 없이 바로 답(빠르고 JSON이 깨지지 않게)
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
        };
        if (o.Json) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        if (o.MaxTokens is { } mt) body["max_tokens"] = mt;
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        var sw = Stopwatch.StartNew();
        using var res = await client.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("사내 AI 오류 {Feature} {Status} {Ms}ms", feature, (int)res.StatusCode, sw.ElapsedMilliseconds);
            throw new InvalidOperationException($"사내 AI 응답 오류({(int)res.StatusCode})");
        }
        var j = JsonNode.Parse(text);
        var content = j?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        log.LogInformation("사내 AI {Feature} {Ms}ms 입력 {In} 출력 {Out} 토큰", feature, sw.ElapsedMilliseconds,
            j?["usage"]?["prompt_tokens"]?.GetValue<int>(), j?["usage"]?["completion_tokens"]?.GetValue<int>());
        return Think().Replace(content, "").Trim();
    }

    /// <summary>JSON 객체로 답을 받는다(앞뒤 군말이 붙어도 첫 { ~ 끝 }만 읽는다).</summary>
    public async Task<JsonObject> JsonAsync(IReadOnlyList<LlmMessage> messages, string feature, LlmOptions? options = null, CancellationToken ct = default)
    {
        var content = await ChatAsync(messages, feature, (options ?? new LlmOptions(Temperature: 0.1)) with { Json = true }, ct);
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("사내 AI가 JSON을 돌려주지 않았습니다.");
        return JsonNode.Parse(content[start..(end + 1)])?.AsObject() ?? throw new InvalidOperationException("사내 AI JSON을 읽지 못했습니다.");
    }

    [GeneratedRegex(@"<think>[\s\S]*?</think>")]
    private static partial Regex Think();
}

public sealed class LlmNotConfiguredException() : InvalidOperationException("사내 AI 키(Llm:ApiKey)가 설정되지 않았습니다.");
