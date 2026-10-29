using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DyOne.Spec.Equipment;
using DyOne.Spec.Extraction;
using DyOne.Spec.Library;

namespace DyOne.Spec.Draft;

/// <summary>
/// 사양정리 초안 만들기(AI 없이 규칙으로 읽을 수 있는 칸) + 모델 자리(ModelPicker 결정) 채우기 + pptx 렌더러용 JSON.
/// AI가 작성한 초안도 모델 자리·후보·렌더 JSON은 여기서 같은 규칙으로 처리한다.
/// </summary>
public static class DraftBuilder
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Compiled;
    // "10,000(L) × 6,000(W) × 4,000(H)" / "14,000mmL × 5,300mmW × 4,000mmH"
    private static readonly Regex Booth = new(@"(\d{1,2},?\d{3})\s*(?:mm)?\s*\(?\s*L\s*\)?\s*[x×*]\s*(\d{1,2},?\d{3})\s*(?:mm)?\s*\(?\s*W\s*\)?\s*[x×*]\s*(\d{1,2},?\d{3})\s*(?:mm)?\s*\(?\s*H", Opt);
    private static readonly Regex RobotCountPart = new(@"([^\s:,()]+)\s*:\s*(\d+)\s*대", Opt);
    private const string Circled = "①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮";

    /// <summary>규칙으로 읽을 수 있는 칸만 채운다(숫자·모델명). 나머지는 빈 칸으로 두어 화면·pptx에서 '확인 필요'로 보인다.</summary>
    /// <param name="visibleLines">PDF '보이는 줄'(SpecExtractor.ExtractPdfLines) — 표 인식이 합친 칸의 수량 찾기용, 없으면 null</param>
    public static SummaryDraft FromRules(IReadOnlyList<Block> blocks, string projectName, ProjectContext ctx, IReadOnlyList<Block>? visibleLines = null)
    {
        var values = SpecValues.Extract(blocks);
        var d = NewDraft(projectName, ctx);
        void Set(string row, string key, Func<string, string>? map = null)
        {
            if (!values.TryGetValue(key, out var v)) return;
            var r = d.Rows.First(x => x.Key == row);
            r.Value = map is null ? v.Value : map(v.Value);
            r.Pages = [v.Unit];
        }

        Set("Robot", "robot", v => string.Join("  /  ", RobotCountPart.Matches(v).Select(m => $"{m.Groups[1].Value} 로봇 * {m.Groups[2].Value}ea")) is { Length: > 0 } s ? s : v);
        BasicCondition.Fill(d, blocks);
        Set("Body Size", "body");
        BodySize.Fill(d, blocks);
        Set("Utility", "utility");
        Set("Atomizer", "atomizer");
        Set("System", "system");
        Set("Training", "training");
        Set("Schedule", "schedule");
        Set("Stand by", "standby");
        UtilitySystem.Fill(d, blocks);
        // 부스(존) 크기 = 공정사양의 L×W×H. 사양서에 다른 크기가 더 있으면(애매) 값은 첫 크기, 나머지는 기타 사항에 원문과 함께
        // 차량 치수(전장 6m 미만)·설계 조건은 부스가 아님
        var booths = blocks.SelectMany(b => Booth.Matches(b.Text).Select(m => (b, m)))
            .Where(x => int.Parse(x.m.Groups[1].Value.Replace(",", "")) >= 6000
                && !Regex.IsMatch(x.b.Text, @"설계\s*(?:조건|사이즈|치수)|차량\s*제원|바디|BODY", RegexOptions.IgnoreCase))
            .Select(x => (x.b, text: $"{x.m.Groups[1].Value}(L) * {x.m.Groups[2].Value}(W) * {x.m.Groups[3].Value}(H)"))
            .DistinctBy(x => x.text).ToList();
        if (booths.Count > 0)
        {
            var r = d.Rows.First(x => x.Key == "Booth size");
            r.Value = booths[0].text;
            r.Pages = [booths[0].b.UnitIndex];
            foreach (var (b, text) in booths.Skip(1))
            {
                var src = Regex.Replace(b.Text, @"\s+", " ").Trim();
                r.Note = string.Join("\n", new[] { r.Note, $"다른 크기: {text}" }.Where(s => s.Length > 0));
                r.AddRemark("사양서에 부스 크기가 여러 개 — 값은 첫 크기, 확인");
                r.Source = [.. r.Source, $"다른 크기: {text} ← {src[..Math.Min(90, src.Length)]} (p{b.UnitIndex})"];
                if (!r.Pages.Contains(b.UnitIndex)) r.Pages = [.. r.Pages.Append(b.UnitIndex).Order()];
            }
        }
        if (blocks.FirstOrDefault(b => Regex.IsMatch(b.Text, "도\\s*료") && Regex.IsMatch(b.Text, "유성|수성|유용성|수용성")) is { } pb)
        {
            var r = d.Rows.First(x => x.Key == "Paint");
            r.Value = Regex.IsMatch(pb.Text, "수성|수용성") && !Regex.IsMatch(pb.Text, "유성|유용성") ? "수성" : "유성";
            r.Pages = [pb.UnitIndex];
        }
        // Color(CCV)·Bell Cleaner·Panel, 그리고 규칙으로 못 채운 칸의 사양서 원문 줄(위 규칙들 다음에 — 이미 찬 칸은 건드리지 않음)
        EquipmentRows.Fill(d, blocks, visibleLines);
        StandBy.Fill(d, blocks);
        RowNotes.Apply(d, blocks);
        var counts = RobotCountPart.Matches(values.GetValueOrDefault("robot")?.Value ?? "").Select(m => (role: m.Groups[1].Value, n: int.Parse(m.Groups[2].Value))).ToList();
        if (counts.Count > 0)
            d.Total = $"Total {{{{{counts.Sum(c => c.n)} robots}}}}\n({string.Join(" + ", counts.Select(c => $"{c.role} {c.n}"))})";
        ApplyProcesses(d, blocks);
        // 공정별 설비사양 표가 있으면(여러 공정 프로젝트) 공정마다 열 — FRT/RR 나누기보다 우선
        d.ProcessTable = ProcessMatrix.Apply(d, blocks);
        // 사양서에 있을 때만 붙는 영업부 줄(고정방식·Tracking rail·오프너 클리너·Vision & MIP·에어 제습기)
        ExtraRows.Fill(d, blocks);
        // PCS 사항·제공 항목 장(사양서 문장)
        ExtraSections.Fill(d, blocks);
        // 여러 공정: Stand by·Others 말고는 공정마다 칸을 나눈다(화면에서 줄마다 합치기·나누기)
        ProcessMatrix.Unmerge(d);
        int Role(string re) => counts.Where(c => Regex.IsMatch(c.role, re)).Sum(c => c.n);
        var (spray, opener) = d.Processes.Count > 0 ? (d.Processes.Sum(p => p.Spray), d.Processes.Sum(p => p.Opener)) : (Role("스프레이|도장"), Role("오프너"));
        // 공정별 표(여러 공정·여러 라인)는 판넬 수를 로봇 대수로 셀 수 없다(라인·시스템 묶음마다 다름) — 사양서 수량만
        if (!d.ProcessTable) EquipmentRows.PanelCounts(d, spray, opener, Math.Max(1, d.Processes.Count));
        if (opener > 0) OpenerTool(d, blocks, ctx);
        d.Engine = "rule";
        d.EngineNote = "사내 AI가 연결되지 않아 규칙으로 읽을 수 있는 칸(숫자·모델명)만 채웠습니다. 빈 칸·기타 사항·PCS는 사양서를 보고 채워 주세요.";
        return d;
    }

    /// <summary>
    /// 사양서가 공정을 나눴으면(1공정 프론트 도어 / 2공정 리어 도어) Robot 칸·Total·열 제목을 공정별로 쓴다.
    /// 예: "1공정 프론트 도어 (FRT): 스프레이 로봇 * 2ea, 오프너 로봇 * 2ea / 2공정 리어 도어 (RR): …", Total 8 robots (스프레이 4 + 오프너 4).
    /// </summary>
    public static void ApplyProcesses(SummaryDraft d, IReadOnlyList<Block> blocks)
    {
        var ps = ProcessSplit.Read(blocks);
        d.Processes = ps;
        if (ps.Count == 0) return;
        string Robots(SpecProcess p) => string.Join(", ", new[] { p.Spray > 0 ? $"스프레이 로봇 * {p.Spray}ea" : "", p.Opener > 0 ? $"오프너 로봇 * {p.Opener}ea" : "" }.Where(s => s.Length > 0));
        var robot = d.Rows.First(r => r.Key == "Robot");
        robot.Value = string.Join("\n", ps.Select(p => $"**{p.Title}** : {Robots(p)}"));
        // Robot은 공정마다 설치하는 설비라 대수가 같아도 공정별 칸(영업부 양식: FRT·RR 각 "스프레이 2ea, 오프너 2ea"). 그 밖의 줄은 한 칸으로 합친다.
        robot.Split = ps.Select(Robots).ToList();
        robot.Pages = ps.Select(p => p.Page).Concat(ProcessSplit.CodePages(blocks)).Distinct().Order().ToList();
        int spray = ps.Sum(p => p.Spray), opener = ps.Sum(p => p.Opener);
        d.Total = $"Total {{{{{spray + opener} robots}}}}\n({string.Join(" + ", new[] { spray > 0 ? $"스프레이 {spray}" : "", opener > 0 ? $"오프너 {opener}" : "" }.Where(s => s.Length > 0))})";
        d.ColHeader = $"{d.ColHeader} ({string.Join(" / ", ps.Select(p => p.Code.Length > 0 ? p.Code : $"{p.No}공정"))})".Trim();
    }

    /// <summary>
    /// Atomizer 칸에 오프너 툴(영업부: "SAMES PPH707 SB : 2 (…) / Opener : 1개 (후크 타입)") — "스프레이: … / 오프너: 그리퍼 타입".
    /// 사양서에 그리퍼·후크가 있으면 그 말과 쪽, 없으면 부위로(과거 사양정리: 도어 = 그리퍼 — 아산·인도네시아, 후드·트렁크·테일게이트 = 후크 — 광주2·인도네시아)
    /// 정하고 기타 사항에 '사양서에 없음 — 확인'.
    /// </summary>
    private static void OpenerTool(SummaryDraft d, IReadOnlyList<Block> blocks, ProjectContext ctx)
    {
        var row = d.Rows.First(r => r.Key == "Atomizer");
        if (row.Split.Count > 0) return;
        var grip = blocks.FirstOrDefault(b => Regex.IsMatch(b.Text, @"그리퍼|그립퍼|GRIPPER", RegexOptions.IgnoreCase));
        var hook = blocks.FirstOrDefault(b => Regex.IsMatch(b.Text, @"후크|(?<![가-힣])훅(?![가-힣])|\bHOOK\b", RegexOptions.IgnoreCase));
        string tool;
        if (grip is not null || hook is not null)
        {
            tool = grip is not null && hook is not null ? "그리퍼·후크" : grip is not null ? "그리퍼 타입" : "후크 타입";
            foreach (var b in new[] { grip, hook }.OfType<Block>())
            {
                if (!row.Pages.Contains(b.UnitIndex)) row.Pages = [.. row.Pages.Append(b.UnitIndex).Order()];
                var t = Regex.Replace(b.Text, @"\s+", " ").Trim();
                row.Source = [.. row.Source, $"오프너 {tool} ← {(t.Length > 90 ? t[..90] + "…" : t)} (p{b.UnitIndex})"];
            }
        }
        else
        {
            var parts = string.Join(" ", ctx.Parts) + " " + d.ColHeader;
            tool = Regex.IsMatch(parts, "도어|DOOR", RegexOptions.IgnoreCase) && !Regex.IsMatch(parts, "후드|트렁크|테일|T/G|HOOD|TRUNK", RegexOptions.IgnoreCase) ? "그리퍼 타입"
                : Regex.IsMatch(parts, "후드|트렁크|테일|T/G|HOOD|TRUNK", RegexOptions.IgnoreCase) ? "후크 타입" : "";
            if (tool.Length == 0) return;
            row.AddRemark($"오프너 {tool}: 사양서에 툴 형식 없음 — 부위 기준(도어 그리퍼, 후드·트렁크 후크), 확인");
        }
        row.Value = $"스프레이: {row.Value} / 오프너: {tool}";
    }

    public static SummaryDraft NewDraft(string projectName, ProjectContext ctx)
    {
        var title = Regex.Replace(projectName, @"\s*사양\s*정리\s*$", "");
        return new SummaryDraft
        {
            CreatedAt = DateTimeOffset.Now,
            Context = ctx,
            Title = $"{title} 사양정리",
            Cover = $"{title}\n사양정리 (사양서 기준 초안)",
            Total = "Total {{? robots}}",
            ColHeader = ctx.Label,
            Rows = DraftLayout.RowKeys.Select(k => new DraftRow { Key = k }).ToList(),
        };
    }

    /// <summary>
    /// 모델 자리 채우기: ModelPicker 결정(로봇 용도별·도장기·FGP)을 자리로 옮긴다.
    /// 사양서에 모델이 있으면 given(제안 없음), '기설치와 동일'이면 existing, 그 밖은 unspecified.
    /// 이전 초안의 사용자 결정은 같은 자리·같은 후보가 있으면 유지.
    /// </summary>
    public static void ApplyPicks(SummaryDraft d, SpecConditions conditions, IReadOnlyList<ModelPick> picks, SummaryDraft? previous = null)
    {
        var oldSlots = previous?.Rows.SelectMany(r => r.Slots).Where(s => s.Decision is not null).ToDictionary(s => s.Id) ?? [];
        d.Conditions = conditions;
        // 규칙 초안의 열 제목은 사양서에서 읽은 조건으로(DY ONE 프로젝트 이름과 사양서가 다를 수 있음). AI 초안은 AI가 쓴 제목을 둔다.
        if (d.Engine == "rule")
        {
            var head = string.Join(" ", new[] { conditions.Plant, conditions.Process, string.Join("·", new[] { conditions.Part, conditions.Area }.Where(s => s.Length > 0)) }.Where(s => s.Length > 0));
            var codes = d.Processes.Count > 0 ? $" ({string.Join(" / ", d.Processes.Select(p => p.Code.Length > 0 ? p.Code : $"{p.No}공정"))})" : "";
            if (head.Length > 0) d.ColHeader = head + codes;
        }
        foreach (var r in d.Rows) r.Slots = [];
        // 공정별 표(여러 공정)는 줄 전체 모델 자리를 두지 않는다 — 공정 열마다 따로(ApplyColumnPicks)
        if (d.ProcessTable) picks = [];
        foreach (var p in picks)
        {
            var s = new ModelSlot
            {
                Id = $"{p.Kind}:{p.Role}",
                Kind = p.Kind,
                Role = p.Role,
                Label = p.Label,
                Status = p.How == "사양서 지정" ? "given" : p.How == "기설치 이력" ? "existing" : "unspecified",
                How = p.How,
                Why = p.Why,
                SpecModel = p.How == "사양서 지정" ? p.Model : "",
                SpecText = p.SpecText,
                SpecPage = p.SpecPage,
                Candidates = p.How == "사양서 지정" ? [] : p.Options.Select((o, i) => DraftLayout.ToCandidate(o, p.How, p.Why, i == 0)).ToList(),
            };
            if (oldSlots.TryGetValue(s.Id, out var old)) s.Decision = KeepDecision(old, s);
            d.Rows.First(r => r.Key == DraftLayout.RowOf(p.Kind)).Slots.Add(s);
        }
    }

    /// <summary>
    /// 여러 공정 초안: 공정 열마다 고른 모델(ColumnPicks)을 그 열의 자리로(Id "robot:스프레이:c2", Label "상도 베이스 외판 스프레이 로봇").
    /// 이전 초안의 사용자 결정은 같은 자리면 이어받는다.
    /// </summary>
    public static void ApplyColumnPicks(SummaryDraft d, IReadOnlyList<(int col, List<ModelPick> picks)> cols, SummaryDraft? previous = null)
    {
        var oldSlots = previous?.Rows.SelectMany(r => r.Slots).Where(s => s.Decision is not null).ToDictionary(s => s.Id) ?? [];
        foreach (var (col, picks) in cols)
            foreach (var p in picks)
            {
                var name = col < d.Processes.Count ? d.Processes[col].Label : $"{col + 1}공정";
                var s = new ModelSlot
                {
                    Id = $"{p.Kind}:{p.Role}:c{col}",
                    Kind = p.Kind,
                    Role = p.Role,
                    Label = $"{name} {p.Label}",
                    Col = col,
                    Qty = p.Kind is "atomizer" or "fgp" && col < d.Processes.Count && d.Processes[col].Spray > 0 ? d.Processes[col].Spray : null,
                    Status = p.How == "사양서 지정" ? "given" : p.How == "기설치 이력" ? "existing" : "unspecified",
                    How = p.How,
                    Why = p.Why,
                    SpecModel = p.How == "사양서 지정" ? p.Model : "",
                    SpecText = p.SpecText,
                    SpecPage = p.SpecPage,
                    Candidates = p.How == "사양서 지정" || p.Options.Count == 0 ? [] : p.Options.Where(o => o.Examples.Count > 0).Select((o, i) => DraftLayout.ToCandidate(o, p.How, p.Why, i == 0)).ToList(),
                };
                if (oldSlots.TryGetValue(s.Id, out var old)) s.Decision = KeepDecision(old, s);
                d.Rows.First(r => r.Key == DraftLayout.RowOf(p.Kind)).Slots.Add(s);
            }
    }

    /// <summary>제안(또는 채택)된 후보. 결정 없음 → 1순위 후보, 선정 필요로 둠·직접 입력 → 없음.</summary>
    /// <summary>
    /// 초안을 다시 만들 때 이전 결정 이어받기. 후보 근거 사례가 바뀌어(규칙·이력 갱신) 같은 기록이 없어도
    /// 채택한 모델이 새 후보에 있으면 그 후보로 옮긴다(예: 오프너 MPX2600 채택 → 새 MPX2600 후보).
    /// </summary>
    public static SlotDecision? KeepDecision(ModelSlot old, ModelSlot now)
    {
        var dec = old.Decision;
        // 기본 사양(1순위 자동 기입)은 이어받지 않는다 — 다시 만들 때 새 1순위로 다시 넣는다(DraftEdit.ApplyDefaults)
        if (dec?.Kind == "default") return null;
        if (dec is null || dec.Kind != "candidate" || now.Candidates.Any(c => c.RecordId == dec.RecordId)) return dec;
        var model = old.Candidates.FirstOrDefault(c => c.RecordId == dec.RecordId)?.Model ?? dec.Applied;
        return now.Candidates.FirstOrDefault(c => c.Model == model) is { } same ? dec with { RecordId = same.RecordId } : null;
    }

    public static SlotCandidate? Chosen(ModelSlot s) => s.Decision switch
    {
        { Kind: "candidate" or "default" } dec => s.Candidates.FirstOrDefault(c => c.RecordId == dec.RecordId),
        null => s.Candidates.FirstOrDefault(),
        _ => null,
    };

    /// <summary>근거 번호(①…): 고른 후보의 프로젝트·출처마다 하나, 표 순서대로</summary>
    public static Dictionary<string, string> Numbering(SummaryDraft d)
    {
        var map = new Dictionary<string, string>();
        foreach (var c in d.Rows.SelectMany(r => r.Slots).Select(Chosen).OfType<SlotCandidate>())
        {
            var key = NumberKey(c);
            if (!map.ContainsKey(key) && map.Count < Circled.Length) map[key] = Circled[map.Count].ToString();
        }
        return map;
    }

    public static string NumberKey(SlotCandidate c) => $"{c.ProjectFolder}|{c.SourceKind}|{c.FileName}";

    /// <summary>공정 열 제목(한 줄, 영업부 양식 "중도 DOOR 내판 (FRT)"): 열 머리(공정 코드 괄호는 뺌) + (FRT). 코드가 없으면 공정 이름.</summary>
    public static string ColumnTitle(SummaryDraft d, SpecProcess p)
    {
        // 공정별 설비사양 표(여러 공정)는 공정 이름이 곧 열 제목(영업부 화성4: WIPING | PRIMER EXT | BASE INNER …)
        if (d.ProcessTable) return p.Code.Length > 0 ? p.Code : p.Label;
        var head = Regex.Replace(d.ColHeader, @"\s*\([^)]*/[^)]*\)\s*$", "").Trim();
        var tag = p.Code.Length > 0 ? p.Code : p.Title;
        return head.Length > 0 ? $"{head} ({tag})" : p.Title;
    }

    /// <summary>pptx 렌더러(python/render_spec_summary.py) 입력 JSON</summary>
    public static JsonObject ToRenderJson(SummaryDraft d, string? sourcePdf)
    {
        var numbers = Numbering(d);
        var rows = new JsonObject();
        // 표에서 뺀 줄은 pptx에도 넣지 않는다(렌더러가 템플릿의 그 줄을 지운다)
        foreach (var r in d.Rows.Where(r => !r.Hidden))
        {
            // 슬라이드 한 장에 표가 들어가도록 기타 사항 문구는 한 줄로 이어 쓴다(영업부 양식도 칸 안에서 이어 씀). 모델 제안은 줄을 나눈다.
            var note = string.Join(" / ", r.Note.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            // 기설치·모델 제안 표시("제안 = … [[사용 여부 확인]]")는 화면 참고 사항에만 — pptx 기타 사항에는 사양서 말만(2026-10-27 사용자)
            var value = r.Value.Length > 0 ? r.Value : "[[사양서에서 찾지 못함 — 확인 필요]]";
            var src = string.Join("·", r.Pages.Select(p => $"p{p}"));
            var nums = r.Slots.Select(Chosen).OfType<SlotCandidate>().Select(c => numbers.GetValueOrDefault(NumberKey(c))).Where(n => n is not null).Distinct().ToList();
            if (nums.Count > 0) src += (src.Length > 0 ? " + " : "") + "모델 근거 " + string.Concat(nums);
            // 원문 근거 줄은 표가 아니라 슬라이드 노트로(기타 사항 칸은 짧은 문구만)
            if (r.Source.Count > 0) src += "\n    원문: " + string.Join("\n    원문: ", r.Source);
            // 공정이 나뉘고 이 줄에 공정별 값이 있으면 값 자리에 배열(공정 순서), 아니면 문자열(모든 공정 한 칸)
            JsonNode valueNode = d.Processes.Count >= 2 && r.Split.Count == d.Processes.Count
                ? new JsonArray(r.Split.Select(v => (JsonNode)v).ToArray())
                : value;
            // 넷째: 공정 칸 병합 폭(스프레드시트처럼 합친 칸) — 렌더러가 그 칸들을 합친다
            var spans = valueNode is JsonArray && r.Span.Count == r.Split.Count && r.Span.Any(x => x != 1)
                ? new JsonArray(r.Span.Select(x => (JsonNode)x).ToArray()) : null;
            rows[r.Key] = new JsonArray(valueNode, note.Length > 0 ? note : " ", src, spans);
        }

        var history = new JsonArray();
        foreach (var (key, no) in numbers)
        {
            var slots = d.Rows.SelectMany(r => r.Slots).Where(s => Chosen(s) is { } c && NumberKey(c) == key).ToList();
            var c = Chosen(slots[0])!;
            history.Add(new JsonObject
            {
                ["id"] = no,
                ["kind"] = string.Join("\n", slots.Select(s => s.Status == "existing" ? $"기설치 동일{(s.SpecPage is { } p ? $" (p{p})" : "")}" : s.How).Distinct()),
                ["item"] = string.Join("·", slots.Select(s => s.Label)),
                ["value"] = string.Join(" / ", slots.Select(s => $"{s.Label} {Chosen(s)!.Model}")),
                ["basis"] = $"{c.Year} {c.ProjectFolder} – {(c.SourceKind == "summary" ? "당사 사양정리" : "도면 제목")}{(c.Excerpt.Length > 0 ? $" (같은 묶음 {c.Excerpt})" : "")}",
                ["file"] = c.FileName,
                ["check"] = string.Join(" / ", slots.Select(s => s.Why).Distinct()) + (slots.Any(s => s.Decision is null) ? " / [[사용 여부 확인]]" : " / 채택"),
            });
        }

        var json = new JsonObject
        {
            ["source_pdf"] = sourcePdf,
            ["title"] = d.Title,
            ["cover"] = d.Cover,
            ["total"] = d.Total,
            ["lines"] = "",
            ["col_header"] = d.ColHeader.Length > 0 ? d.ColHeader : "사양",
            // 공정별 열 제목(영업부 양식 "중도 DOOR 내판 (FRT) | (RR)"). 2개 이상일 때만 렌더러가 값 열을 나눈다.
            ["columns"] = d.Processes.Count >= 2 ? new JsonArray(d.Processes.Select(p => (JsonNode)ColumnTitle(d, p)).ToArray()) : null,
            ["rows"] = rows,
            ["schedule_note"] = d.ScheduleNote.Length > 0 ? d.ScheduleNote
                : d.ScheduleImage is { } sn ? $"사양서 {sn.Page}쪽 일정표" : "[[사양서 일정표 위치를 찾지 못함 — 사양서 확인]]",
            ["edu_title"] = "**교육 일정** (사양서 기준)",
            ["edu_caption"] = "",
            ["etc"] = Checks(d.Etc, "[[기타 사항(회의·협의 결과)을 채워 주세요]]"),
            ["pcs"] = Checks(d.Pcs, "[[PCS 사항이 사양서에 없음 — 현장·협의 내용을 채워 주세요]]"),
            // 제공 항목 장: 사양서에 스페어·공구·키락 등 제공 요구가 있을 때만(템플릿 PCS 장을 복사해 붙인다)
            ["supply"] = d.Supply.Count > 0 ? Checks(d.Supply, "") : null,
        };
        if (sourcePdf is not null && d.LayoutImage is { } li) json["layout_image"] = Clip(li);
        if (sourcePdf is not null && d.ScheduleImage is { } si)
        {
            var o = Clip(si);
            o["left"] = 0.99; o["top"] = 2.1; o["height"] = 6.8;
            json["schedule_image"] = o;
        }
        if (d.EduTable is not null) json["edu_table"] = d.EduTable.DeepClone();
        if (history.Count > 0)
            json["history"] = new JsonObject
            {
                ["title"] = "모델 제안 근거",
                // 내부 검토용 장(사용자 2026-10-27: pptx에 남김). 표에는 모델만 들어가고 근거 번호는 슬라이드 노트에 있다
                ["intro"] = "(내부 검토용) 사양서에 모델이 없거나 '기설치와 동일'인 항목에 넣은 모델과 그 근거입니다. 같은 공장 이력 또는 과거 경향(도료·하전·외/내판·공정)으로 골랐습니다.",
                ["rows"] = history,
                ["note"] = "순서: 사양서 지정 → 기설치 이력 → 같은 공장 이력 → 경향 규칙(도장기 = 도료·하전, 오프너 = 도어 MPX2600·후드 MPX3500, 스프레이·FGP = 같은 외/내판·공정 2022년 이후 보통). 최종 모델은 고객 협의·현장 확인으로 정한다.",
            };
        return json;
    }

    /// <summary>모델 자리 한 줄: 기설치/제안 + 모델 + 근거 번호 + 확인 표시</summary>
    public static string SlotText(ModelSlot s, Dictionary<string, string> numbers)
    {
        if (s.Status == "given") return $"{{{{사양서 = {s.Label} {s.SpecModel}}}}}";
        var head = s.Status == "existing" ? "기설치" : "제안";
        return s.Decision?.Kind switch
        {
            "custom" => $"**{s.Label} {s.Decision.Text}** (직접 입력)",
            "none" => $"[[{s.Label} 선정 필요]]",
            // 과거 사양정리 참고(RowHints): 칸 이름은 줄 머리에 있으므로 값만
            _ when s.Kind == "row" && Chosen(s) is { } c =>
                $"<<참고 = {c.Model} {numbers.GetValueOrDefault(NumberKey(c))}>>" + (s.Decision is null or { Kind: "default" } ? " [[확인]]" : ""),
            _ when Chosen(s) is { } c =>
                $"<<{head} = {s.Label} {c.Model} {numbers.GetValueOrDefault(NumberKey(c))}>>" + (s.Decision is null or { Kind: "default" } ? " [[사용 여부 확인]]" : ""),
            _ => $"[[{s.Label} 선정 필요 — 비슷한 이력 없음]]",
        };
    }

    private static JsonArray Checks(List<DraftCheck> items, string empty) =>
        items.Count == 0
            ? new JsonArray(new JsonArray(empty, ""))
            : new JsonArray(items.Select(i => (JsonNode)new JsonArray(i.Text, string.Join("·", i.Pages.Select(p => $"p{p}")))).ToArray());

    private static JsonObject Clip(PageClip c) => new() { ["page"] = c.Page, ["clip"] = new JsonArray(c.Clip.Select(v => (JsonNode)v).ToArray()) };

    private static string Short(string s) => s.Length <= 120 ? s.Trim() : s[..120].Trim() + "…";
}
