using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace DyOne.Worker.Jobs.ProjectMain;

/// <param name="Label">선정 담당용 근거(DB 값, 숫자 포함)</param>
/// <param name="Plain">전 직원용 쉬운 설명(근거 보기). 금액은 적지 않고 규모 말로 바꾼다(11-04 사용자)</param>
/// <param name="Group">size(규모) · now(지금 진행)</param>
public sealed record ScorePart(string Key, int Pts, string Label, string Plain = "", string Group = "size");

public sealed record Suggestion(
    string Key, List<string> Codes, long? ProjectId, string Name, int Rule, int Trace, string Category, string CategoryBasis, List<ScorePart> Parts)
{
    public int Score => Rule + Trace;
}

/// <summary>
/// 회사 주요 후보 v3(11-04) — 판단은 규칙(이 클래스), 설명은 사내 AI(이유 문장·보조 꼬리표, 숫자 없음).
/// 기준과 근거: docs/roadmap/회사 주요 프로젝트 선정 기준 v3 (2026-11-04).md (v2 백업: docs/roadmap/backup/featured-v2 (2026-11-04)/)
///  대상: ERP 수주·영업중 묶음(옛→새 코드 합산)
///  제외: 소규모 유지보수(이름 낱말 & 10억 미만) · 지금 진행 점수 0
///  규모(최대 10): 계약 금액 +5/+4/+2 · 로봇 대수 +3/+2/+1 · 신공장·신기술(이름) +2
///  지금 진행(최대 9): ERP 발주 연속성(최근 3개월 중 발주 달 수, 마지막 발주 45일 넘으면 0) · NAS 작업 패턴(작업 폴더, 꾸준히/한 번에) · 모듈라 상태
///  뺀 것: 종료 임박 · 해외 · ES 기록(CS의 AS 대응) · NAS 파일 수만 보기 · 소규모 −2(→제외)
/// 분류 = 모듈라 공정에 도장 공정이 있으면 도장(하도는 비도장) → 없으면 PM 부서 → 그래도 없으면 이름으로 추정.
/// </summary>
public static partial class FeaturedScorer
{
    [GeneratedRegex("신공장|신설|신규|EVO|파일롯트|파일럿|증설|신축|신기술")] private static partial Regex NewRe();
    /// <summary>소규모 유지보수 낱말(티칭은 넣지 않음 — 신차 티칭은 후보, 11-04)</summary>
    [GeneratedRegex("수리|오버홀|점검|교체 지원|보완|클레임|SPARE|스페어|정비|그리스|AS대응|AS 대응|교육", RegexOptions.IgnoreCase)] private static partial Regex SmallRe();
    [GeneratedRegex("실러|샌딩|폴리싱|와이핑|검사|비젼|비전|헤라|붓|로딩|언로딩|에어블로우|스워드|센서|모니터링|마스킹|조립|하도")] private static partial Regex NonPaintNameRe();
    [GeneratedRegex("에코프로|LG화학|한국카본|자강|이든텍|우림|아사히|엠엘텍|에이원|니프코|영산")] private static partial Regex GeneralNameRe();
    /// <summary>모듈라 공정 칸의 도장 공정(범퍼 도장·외판·내판·중상도·프라이머·중도·베이스·클리어 등)</summary>
    [GeneratedRegex("프라이머|중도|베이스|클리어|전공정|2K|수성|유성|외판|내판|범퍼|중상도|도장", RegexOptions.IgnoreCase)] private static partial Regex PaintProcRe();
    [GeneratedRegex("하도|신기술")] private static partial Regex NonPaintProcRe();

    /// <summary>모듈라 상태 → 점수: 제작·설치·시운전·외부검수·지연 +3, 철거·팩킹 +2, 준비중 +1, 그 밖(마무리·완료·홀딩) 0</summary>
    private static (int Pts, string? Plain) ModularPart(IEnumerable<ModularInfo> mods)
    {
        var best = (Pts: 0, Name: (string?)null);
        foreach (var m in mods)
        {
            var st = (m.Status ?? "").Trim();
            var pts = st.StartsWith("2.") || st.StartsWith("3.") || st.StartsWith("4.") || st.StartsWith("5.") || st.StartsWith("7.") ? 3
                : st.StartsWith("6.") ? 2 : st.StartsWith("1.") ? 1 : 0;
            if (pts > best.Pts) best = (pts, System.Text.RegularExpressions.Regex.Replace(st, @"\s+", " "));
        }
        return best.Pts switch
        {
            3 => (3, best.Name!.StartsWith("7.") ? $"모듈라 '{best.Name}' — 공장 작업 중 지연이 생겨 살펴볼 필요가 있음" : $"모듈라 '{best.Name}' — 우리 공장에서 실물 작업 중"),
            2 => (2, $"모듈라 '{best.Name}' — 출하 준비 중"),
            1 => (1, $"모듈라 '{best.Name}' — 공장 작업을 곧 시작"),
            _ => (0, null),
        };
    }

    public static List<Suggestion> Score(MainData d, DateOnly today)
    {
        var rows = new List<Suggestion>();
        var keys = d.Erp.Keys.Where(c => c.StartsWith("DLP-")).Select(d.KeyOf).Distinct();
        foreach (var key in keys)
        {
            if (!d.Erp.TryGetValue(key, out var main) || main.Status is not ("수주" or "영업중")) continue;
            var name = d.NameOf(key);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var codes = d.CodesOf(key);
            var amt = codes.Sum(c => d.Erp.TryGetValue(c, out var e) ? e.Amount : 0) / 100_000_000m;
            if (SmallRe().IsMatch(name) && amt < 10) continue; // 소규모 유지보수 → 후보에서 제외
            var mods = d.ModularOf(key);
            var robots = mods.Sum(m => m.Robots);
            var t = d.TraceOf(key);

            // ── 규모 ──
            var parts = new List<ScorePart>();
            if (amt >= 100) parts.Add(new("amount", 5, $"계약 {amt:0}억", "회사에서 가장 큰 규모의 계약"));
            else if (amt >= 30) parts.Add(new("amount", 4, $"계약 {amt:0}억", "대형 계약"));
            else if (amt >= 10) parts.Add(new("amount", 2, $"계약 {amt:0}억", "중형 계약"));
            if (robots >= 30) parts.Add(new("robots", 3, $"로봇 {robots}대", $"로봇 {robots}대가 들어가는 큰 라인"));
            else if (robots >= 10) parts.Add(new("robots", 2, $"로봇 {robots}대", $"로봇 {robots}대 규모"));
            else if (robots >= 4) parts.Add(new("robots", 1, $"로봇 {robots}대", $"로봇 {robots}대"));
            if (NewRe().IsMatch(name)) parts.Add(new("new", 2, "신공장·신기술(이름)", "새 공장·새 라인·새 기술을 만드는 일"));
            var rule = parts.Sum(p => p.Pts);

            // ── 지금 진행 ──
            var now = new List<ScorePart>();
            if (t.LastPo is { } lp)
            {
                var months = (t.Po30 > 0 ? 1 : 0) + (t.Po31_60 > 0 ? 1 : 0) + (t.Po61_90 > 0 ? 1 : 0);
                var label = $"발주 {months}/3개월({t.Po61_90}·{t.Po31_60}·{t.Po30}건, 마지막 {lp.Month}/{lp.Day})";
                var stale = today.DayNumber - lp.DayNumber > 45;
                if (!stale && months > 0)
                    now.Add(new("po", months, label,
                        months == 3 ? "자재·외주 발주가 석 달 내내 이어지고 있음" : months == 2 ? "최근 석 달 중 두 달 발주가 있음" : "최근 발주가 있음", "now"));
            }
            if (t.Nas30 > 0)
            {
                var share = t.NasTopShare;
                var (pts, label, plain) =
                    t.NasDays14 >= 5 ? (3, $"NAS 꾸준히 작업(2주 중 {t.NasDays14}일 · {t.Nas30}개)", $"설계·도면 작업이 최근 2주 중 {t.NasDays14}일 이어짐")
                    : (t.NasDays14 >= 2 && t.Nas14 >= 10) || t.NasDays30 >= 5 ? (2, $"NAS 작업 중({t.NasDays30}일 · {t.Nas30}개)", "설계·도면 작업이 진행 중")
                    : t.NasDays30 <= 2 || share >= 0.7m ? (t.Nas30 >= 30 ? 1 : 0, $"NAS 한 번에 올림({t.NasDays30}일 · {t.Nas30}개, 하루 {share:P0})", "자료를 한꺼번에 올림(최근 작업은 적음)")
                    : (t.Nas30 >= 5 ? 1 : 0, $"NAS 가끔({t.NasDays30}일 · {t.Nas30}개)", "가끔 자료 작업이 있음");
                if (pts > 0) now.Add(new("nas", pts, label, plain, "now"));
            }
            var (mp, mplain) = ModularPart(mods);
            if (mp > 0) now.Add(new("modular", mp, mplain!.Split(" — ")[0], mplain, "now"));
            var trace = now.Sum(p => p.Pts);
            if (trace == 0) continue; // 지금 진행 흔적 없음 → 후보에서 제외

            var (cat, basis) = Category(mods, main.Pm is { } pm ? d.PmDept.GetValueOrDefault(pm) : null, name);
            rows.Add(new Suggestion(key, codes, codes.Select(c => d.ProjectId.TryGetValue(c, out var id) ? id : (long?)null).FirstOrDefault(x => x is not null),
                name, rule, trace, cat, basis, [.. parts, .. now]));
        }
        return rows.OrderByDescending(r => r.Score).ThenByDescending(r => r.Trace).ThenByDescending(r => r.Parts.FirstOrDefault(p => p.Key == "amount")?.Pts ?? 0).ThenBy(r => r.Key).ToList();
    }

    /// <summary>분류: ① 모듈라 공정에 도장 공정 → 도장, 하도·신기술 → 비도장 ② PM 부서 ③ 이름으로 추정</summary>
    public static (string Category, string Basis) Category(List<ModularInfo> mods, string? dept, string name)
    {
        foreach (var m in mods)
            if (PaintProcRe().IsMatch(m.Process ?? "")) return ("도장", "모듈라 공정");
        foreach (var m in mods)
            if (NonPaintProcRe().IsMatch(m.Process ?? "")) return ("비도장", "모듈라 공정");
        foreach (var m in mods)
            if (PaintProcRe().IsMatch(m.Detail ?? "")) return ("도장", "모듈라 공정");
        var dp = dept ?? "";
        if (dp.Contains("GI")) return ("일반 산업", "PM 부서");
        if (dp.Contains("자동화")) return ("비도장", "PM 부서");
        if (dp.Contains("도장")) return (NonPaintNameRe().IsMatch(name) ? "비도장" : "도장", "PM 부서");
        if (GeneralNameRe().IsMatch(name)) return ("일반 산업", "이름으로 추정");
        return (NonPaintNameRe().IsMatch(name) ? "비도장" : "도장", "이름으로 추정");
    }

    private static readonly string[] AllowedSignals = ["지연", "클레임", "사양 변경", "고객 독촉", "일정 압박"];

    /// <summary>오늘 후보를 저장한다. 상위 aiTop건은 사내 AI 이유 문장·꼬리표. 점수 근거가 전과 같으면(같은 날 다시 돌린 것 포함) 전 문장을 그대로 쓴다.</summary>
    public static async Task<int> SaveAsync(NpgsqlConnection db, List<Suggestion> rows, DateOnly today, int keep, int aiTop, WorkerLlm? llm, ILogger log, CancellationToken ct)
    {
        var top = rows.Take(keep).ToList();
        var reasons = new Dictionary<string, (string? Reason, string[]? Signals)>();
        for (var i = 0; i < Math.Min(aiTop, top.Count); i++)
        {
            var s = top[i];
            var partsJson = PartsJson(s.Parts);
            await using (var prev = new NpgsqlCommand("""
                select reason, signals::text from project.featured_suggestion
                 where code = @c and run_date <= @d and parts = @p::jsonb and reason is not null order by run_date desc, created_at desc limit 1
                """, db))
            {
                prev.Parameters.AddWithValue("c", s.Key);
                prev.Parameters.AddWithValue("d", today);
                prev.Parameters.AddWithValue("p", partsJson);
                await using var r = await prev.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    reasons[s.Key] = (r.GetString(0), r.IsDBNull(1) ? null : JsonSerializer.Deserialize<string[]>(r.GetString(1)));
                    continue;
                }
            }
            if (llm is null)
            {
                // 사내 AI가 없을 때: 근거가 조금 바뀌었어도 가장 최근 이유 문장을 그대로 쓴다(이유 문장엔 숫자가 없어 틀리지 않는다)
                await using var last = new NpgsqlCommand("""
                    select reason, signals::text from project.featured_suggestion
                     where code = @c and reason is not null order by run_date desc, created_at desc limit 1
                    """, db);
                last.Parameters.AddWithValue("c", s.Key);
                await using var lr = await last.ExecuteReaderAsync(ct);
                if (await lr.ReadAsync(ct)) reasons[s.Key] = (lr.GetString(0), lr.IsDBNull(1) ? null : JsonSerializer.Deserialize<string[]>(lr.GetString(1)));
                continue;
            }
            try
            {
                var texts = await MainData.ReadTextsAsync(db, s.Codes, 14, 6, ct);
                var user = new JsonObject
                {
                    ["프로젝트"] = s.Name,
                    ["분류"] = s.Category,
                    ["점수 근거"] = new JsonArray(s.Parts.Select(p => (JsonNode)p.Label).ToArray()),
                    ["최근 2주 글"] = new JsonArray(texts.Select(t => (JsonNode)$"{t.Source}: {t.Text}").ToArray()),
                }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                var j = await llm.JsonAsync(ReasonPrompt, user, "featured-reason", ct);
                var reason = j["reason"]?.ToString().Trim();
                if (reason is null || reason.Length is < 8 or > 120 || Regex.IsMatch(reason, @"\d")) reason = null; // 숫자가 섞이면 버린다(숫자는 화면이 DB 값으로)
                var signals = (j["signals"] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => AllowedSignals.Contains(x)).Distinct().ToArray();
                reasons[s.Key] = (reason, signals);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("회사 주요 이유 문장 실패 {Code}: {Msg}", s.Key, ex.Message);
            }
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var del = new NpgsqlCommand("delete from project.featured_suggestion where run_date = @d", db, tx))
        {
            del.Parameters.AddWithValue("d", today);
            await del.ExecuteNonQueryAsync(ct);
        }
        var rank = 0;
        foreach (var s in top)
        {
            rank++;
            reasons.TryGetValue(s.Key, out var rs);
            await using var ins = new NpgsqlCommand("""
                insert into project.featured_suggestion (run_date, code, codes, project_id, name, rank, score, rule_score, trace_score, category, category_basis, parts, reason, signals)
                values (@d, @c, @cs, @pid, @n, @rk, @s, @ru, @tr, @cat, @cb, @p::jsonb, @re, @sg::jsonb)
                """, db, tx);
            ins.Parameters.AddWithValue("d", today);
            ins.Parameters.AddWithValue("c", s.Key);
            ins.Parameters.AddWithValue("cs", s.Codes.ToArray());
            ins.Parameters.Add(new NpgsqlParameter("pid", NpgsqlDbType.Bigint) { Value = (object?)s.ProjectId ?? DBNull.Value });
            ins.Parameters.AddWithValue("n", s.Name);
            ins.Parameters.AddWithValue("rk", rank);
            ins.Parameters.AddWithValue("s", s.Score);
            ins.Parameters.AddWithValue("ru", s.Rule);
            ins.Parameters.AddWithValue("tr", s.Trace);
            ins.Parameters.AddWithValue("cat", s.Category);
            ins.Parameters.AddWithValue("cb", s.CategoryBasis);
            ins.Parameters.AddWithValue("p", PartsJson(s.Parts));
            ins.Parameters.Add(new NpgsqlParameter("re", NpgsqlDbType.Text) { Value = (object?)rs.Reason ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("sg", NpgsqlDbType.Text) { Value = rs.Signals is { Length: > 0 } sg ? JsonSerializer.Serialize(sg) : DBNull.Value });
            await ins.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return top.Count;
    }

    public static string PartsJson(List<ScorePart> parts) =>
        JsonSerializer.Serialize(parts.Select(p => new { key = p.Key, pts = p.Pts, label = p.Label, plain = p.Plain, group = p.Group }),
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private const string ReasonPrompt = """
        너는 두림야스카와 사내 프로젝트 포털의 '회사 주요 프로젝트' 추천 이유를 쓰는 비서다.
        주어진 사실(점수 근거, 최근 2주 글)만 보고, 회사가 이 프로젝트를 왜 지금 주목해야 하는지 한 문장(40~70자)으로 쓴다.
        - 숫자·금액·날짜·대수·코드는 절대 쓰지 않는다(화면이 숫자를 따로 보여 준다). 예: '대형 해외 신공장으로 설계 도면 작업과 발주가 활발합니다.'
        - 사실에 없는 내용을 지어내지 않는다. 프로젝트 이름은 다시 쓰지 않는다.
        - 점수 근거는 지금의 규모·흔적일 뿐 변화가 아니다. '확대·증가·순조롭게·막바지·해소' 같은 변화·평가 말은 최근 2주 글에 그 내용이 있을 때만 쓴다.
        - '~입니다' 또는 '~습니다'로 끝낸다.
        - 최근 글에 지연·클레임·사양 변경·고객 독촉·일정 압박 신호가 분명히 보이면 signals에 그 낱말을 넣고, 없으면 빈 배열.
        JSON으로만 답한다: {"reason":"…","signals":[]}
        """;
}
