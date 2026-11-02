using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using DyOne.Api.Platform;

namespace DyOne.Api.Modules.Ai;

public record AgentMessage(string Role, string Content);
/// <param name="Context">화면이 넘기는 지금 상황(보고 있는 프로젝트·목록 요약 등). 서버가 길이를 자른다.</param>
public record AgentRequest(IReadOnlyList<AgentMessage> Messages, string? Context);
public record AgentReply(string Reply, string Agent, string Model);

public record DocCategory(string Code, string Name, string? What);
/// <param name="Key">화면이 돌려받을 때 맞출 열쇠(파일 줄·폴더 묶음)</param>
/// <param name="Path">끌어다 놓은 경로(폴더 묶음이면 맨 위 폴더 이름)</param>
/// <param name="Files">폴더 묶음이면 안 파일 경로 일부(최대 40개)</param>
/// <param name="Sample">글자 파일이면 앞부분(최대 1,500자)</param>
/// <param name="RuleCode">규칙이 고른 종류 — AI에게 참고로만 준다</param>
public record DocItem(string Key, string Path, long? Size, IReadOnlyList<string>? Files, string? Sample, string? RuleCode, IReadOnlyList<string>? RuleReasons);
public record ClassifyRequest(IReadOnlyList<DocItem> Items, IReadOnlyList<DocCategory> Categories, string? ProjectName);
public record ClassifyResult(string Key, string Code, string Reason, double Confidence);

/// <summary>
/// 화면별 AI 에이전트와 공통 AI 기능. 프롬프트(에이전트 역할)는 서버에서만 정한다 — 화면은 에이전트 이름과 상황(context)만 보낸다.
/// 새 화면에 에이전트를 붙이려면 Agents에 한 줄 추가하고, 화면에서 &lt;AgentPanel agent="키"&gt;를 쓰면 된다(web/src/ai).
/// </summary>
public static class AiAgentModule
{
    const int MaxContext = 30_000;
    const int MaxTurns = 20;

    /// <summary>에이전트 목록: 키 → (보이는 이름, 역할 지시)</summary>
    public static readonly Dictionary<string, (string Name, string System)> Agents = new()
    {
        ["project-docs"] = ("자료함 도우미", """
            너는 두림야스카와(로봇 도장 설비 업체) DY ONE의 '프로젝트 자료함' 도우미다.
            사용자는 프로젝트 인원이고, 아래 [화면 상황]에 이 프로젝트 자료 목록(종류·단계·올린 사람·날짜)이 있다.
            할 일: 자료 찾기, 어느 종류(NAS 13칸 + 현장 백업)에 올릴지 안내, 빠진 자료 짚기, 목록 요약.
            규칙: [화면 상황]에 있는 것만 근거로 답하고 없는 자료를 지어내지 않는다. 한국어로 짧게, 파일 이름은 그대로 쓴다.
            """),
        ["general"] = ("DY ONE 도우미", """
            너는 두림야스카와 사내 업무 시스템 DY ONE의 도우미다. 한국어로 짧고 정확하게 답한다.
            [화면 상황]이 있으면 그것을 근거로 답하고, 모르는 것은 모른다고 말한다. 사내 비밀번호·개인정보는 묻지도 답하지도 않는다.
            """),
    };

    public static void MapAiAgents(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/ai").WithTags("AI");

        // 사내 AI 연결 상태(화면이 AI 버튼을 켤지 정한다)
        g.MapGet("/status", (Llm llm) => Results.Ok(new
        {
            configured = llm.IsConfigured,
            model = llm.Model,
            agents = Agents.Select(a => new { key = a.Key, name = a.Value.Name }),
        }));

        // 화면 에이전트 대화
        g.MapPost("/agent/{agent}", async (string agent, AgentRequest req, ClaimsPrincipal p, Llm llm, AuditLog audit, CancellationToken ct) =>
        {
            if (!Agents.TryGetValue(agent, out var def)) return Results.NotFound(new { message = "없는 에이전트입니다." });
            if (!llm.IsConfigured) return Results.Json(new { message = "사내 AI가 연결되지 않았습니다(Llm:ApiKey)." }, statusCode: 503);
            var turns = req.Messages.Where(m => m.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(m.Content)).TakeLast(MaxTurns).ToList();
            if (turns.Count == 0 || turns[^1].Role != "user") return Results.BadRequest(new { message = "질문이 없습니다." });
            var user = p.Current();
            var system = new StringBuilder(def.System.Trim())
                .Append($"\n\n[사용자] {user.Name}")
                .Append(string.IsNullOrWhiteSpace(req.Context) ? "" : $"\n\n[화면 상황]\n{Cut(req.Context, MaxContext)}");
            var messages = new List<LlmMessage> { new("system", system.ToString()) };
            messages.AddRange(turns.Select(t => new LlmMessage(t.Role, Cut(t.Content, 4000))));
            var reply = await llm.ChatAsync(messages, $"agent:{agent}", new LlmOptions(Temperature: 0.3, MaxTokens: 1500), ct);
            audit.Record("ai.agent", "ai", agent, Cut(turns[^1].Content, 200));
            return Results.Ok(new AgentReply(reply, agent, llm.Model));
        });

        // 자료 종류 분류: 파일·폴더 묶음마다 종류 하나 + 이유. 규칙 결과는 참고로만.
        g.MapPost("/classify-docs", async (ClassifyRequest req, Llm llm, CancellationToken ct) =>
        {
            if (!llm.IsConfigured) return Results.Json(new { message = "사내 AI가 연결되지 않았습니다(Llm:ApiKey)." }, statusCode: 503);
            if (req.Items.Count is 0 or > 60) return Results.BadRequest(new { message = "한 번에 1~60개까지 분류합니다." });
            var codes = req.Categories.Select(c => c.Code).ToHashSet();
            var cats = string.Join("\n", req.Categories.Select(c => $"- {c.Code}: {c.Name}{(string.IsNullOrWhiteSpace(c.What) ? "" : $" — {c.What}")}"));
            var items = new JsonArray(req.Items.Select(i => (JsonNode)new JsonObject
            {
                ["key"] = i.Key,
                ["path"] = Cut(i.Path, 300),
                ["size"] = i.Size,
                ["files"] = i.Files is { Count: > 0 } f ? new JsonArray(f.Take(40).Select(x => (JsonNode)JsonValue.Create(Cut(x, 200))!).ToArray()) : null,
                ["sample"] = i.Sample is { Length: > 0 } s ? Cut(s, 1500) : null,
                ["rule"] = i.RuleCode is null ? null : $"{i.RuleCode} ({string.Join(", ", i.RuleReasons ?? [])})",
            }).ToArray());
            var system = $$"""
                너는 두림야스카와(로봇 도장 설비 업체) 프로젝트 자료 분류 담당이다. 자료마다 아래 종류 중 하나를 고른다.
                종류(code: 이름 — 넣는 것):
                {{cats}}
                판단 근거(사내 NAS 프로젝트 폴더 173개 실제 배치):
                - .dwg .sldprt .sldasm .step → design. 도면 pdf도 design, 취급설명서·매뉴얼 pdf는 manual.
                - .JBI .CND(로봇 JOB·조건) → teach. 단 시운전 기간 결과물 묶음(최종 티칭·체크시트·날짜별 데이터)이면 commission.
                - GX Works .gxw .gpj .wpg, LS .xgdb, EVO(.xch .xmls), IP 리스트 → plc. .pgx .alog(HMI 화면·알람) → hmi.
                - SRS_BACKUP, CMOS.BIN, ALL.PRM, '백업'·'backup' 폴더, 시운전 뒤 날짜별 스냅샷 → backup.
                - 공정 검토·레이아웃 시뮬레이션·OLP·견적·입찰 → quote. 사양서·사양정리·사양설명회 → spec. 공사수행계획서·킥오프 → schedule.
                - 회의록·미팅 → report. 완료보고서·교육 실시 보고서·작업일보 → final. 현장 사진(카톡·HEIC)·참고 → etc.
                - 폴더 묶음(files가 있음)은 안 파일 전체를 보고 묶음 하나의 종류를 고른다.
                - rule은 이름 규칙이 고른 값이다. 참고만 하고, 틀렸다고 보면 바꾼다.
                출력(JSON만): {"items":[{"key":"…","code":"종류 code","reason":"한국어 한 줄 근거","confidence":0.0~1.0}]}
                code는 위 목록에 있는 값만 쓴다. 모든 key에 답한다.
                """;
            var user = $"프로젝트: {req.ProjectName ?? "-"}\n자료:\n{items.ToJsonString()}";
            var j = await llm.JsonAsync([new("system", system), new("user", user)], "classify-docs", new LlmOptions(Temperature: 0, TimeoutSeconds: 180), ct);
            var results = (j["items"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(o => new ClassifyResult(
                    o["key"]?.ToString() ?? "",
                    o["code"]?.ToString() ?? "",
                    o["reason"]?.ToString() ?? "",
                    double.TryParse(o["confidence"]?.ToString(), out var c) ? Math.Clamp(c, 0, 1) : 0.5))
                .Where(r => r.Key.Length > 0 && codes.Contains(r.Code))
                .ToList();
            return Results.Ok(new { model = llm.Model, items = results });
        });
    }

    static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
}
