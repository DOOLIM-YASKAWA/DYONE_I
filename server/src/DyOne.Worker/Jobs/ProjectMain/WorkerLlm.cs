using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DyOne.Worker.Jobs.ProjectMain;

/// <summary>
/// Worker용 사내 AI 글 호출(API의 Platform/Llm.cs와 같은 방식: SGLang OpenAI 호환, 생각 과정 끔, JSON 답).
/// 설정 Llm:BaseUrl · Llm:Model · Llm:ApiKey(user-secrets/환경변수). 키가 없으면 IsConfigured=false → 규칙 문장만 쓴다.
/// 데이터는 사내 서버로만 간다(회사 밖으로 나가지 않음).
/// </summary>
public sealed partial class WorkerLlm(IConfiguration config, ILogger logger)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public bool IsConfigured => !string.IsNullOrWhiteSpace(config["Llm:ApiKey"]) && !string.IsNullOrWhiteSpace(config["Llm:BaseUrl"]);

    public async Task<JsonObject> JsonAsync(string system, string user, string feature, CancellationToken ct, int maxTokens = 400)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, config["Llm:BaseUrl"]!.TrimEnd('/') + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["Llm:ApiKey"]);
        var body = new JsonObject
        {
            ["model"] = config["Llm:Model"] ?? "qwen3.8-27b-fp8",
            ["temperature"] = 0.2,
            ["max_tokens"] = maxTokens,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
        };
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var sw = Stopwatch.StartNew();
        using var res = await Http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"사내 AI 응답 오류 {(int)res.StatusCode}");
        var content = JsonNode.Parse(text)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        content = Think().Replace(content, "");
        logger.LogDebug("사내 AI {Feature} {Ms}ms", feature, sw.ElapsedMilliseconds);
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("사내 AI가 JSON을 돌려주지 않았습니다.");
        return JsonNode.Parse(content[start..(end + 1)])?.AsObject() ?? throw new InvalidOperationException("사내 AI JSON을 읽지 못했습니다.");
    }

    /// <summary>그림 한 장 + 지시로 JSON 답을 받는다(일정표 그림 읽기). 그림은 사내 서버로만 보낸다.</summary>
    public async Task<JsonObject> VisionJsonAsync(string prompt, byte[] image, string mime, string feature, CancellationToken ct, int maxTokens = 2000)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, config["Llm:BaseUrl"]!.TrimEnd('/') + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["Llm:ApiKey"]);
        var body = new JsonObject
        {
            ["model"] = config["Llm:Model"] ?? "qwen3.8-27b-fp8",
            ["temperature"] = 0,
            ["max_tokens"] = maxTokens,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = prompt },
                    new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{mime};base64,{Convert.ToBase64String(image)}" } }),
            }),
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
        };
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"사내 AI 응답 오류 {(int)res.StatusCode}");
        var content = Think().Replace(JsonNode.Parse(text)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "", "");
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("사내 AI가 JSON을 돌려주지 않았습니다.");
        return JsonNode.Parse(content[start..(end + 1)])?.AsObject() ?? throw new InvalidOperationException("사내 AI JSON을 읽지 못했습니다.");
    }

    [GeneratedRegex(@"<think>[\s\S]*?</think>")]
    private static partial Regex Think();
}
