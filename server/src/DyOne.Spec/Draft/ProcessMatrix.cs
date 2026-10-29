using System.Text.RegularExpressions;
using DyOne.Spec.Extraction;

namespace DyOne.Spec.Draft;

/// <summary>
/// 여러 공정을 한 번에 하는 프로젝트(화성4 신공장: 와이핑·중도 외판·베이스 내판(후드/트렁크)·베이스 내판(도어)·베이스 외판·클리어 내판·클리어 외판)의
/// 사양서 '설비사양' 표 — 열이 공정, 행이 항목(UPH·부스 길이/폭·C/V 방식·피치·로봇 타입/Axis/가반하중·도장기 방식/타입·세정장치·디그레이드·도어오픈).
/// "←"는 왼쪽 칸과 같다는 뜻. 표가 있으면 사양정리 표도 공정마다 열을 만들어 각각 적는다(영업부 화성4 사양정리 요약과 같은 꼴, 2026-10-28 사용자).
/// 공정별 로봇 대수는 같은 쪽 '설치 대수' 목록("중도외판 4대×2라인", "베이스 내판#1 7대×2라인 후드/트렁크내판")에서 이름이 가장 잘 맞는 열에.
/// </summary>
public static class ProcessMatrix
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Compiled;
    private static readonly Regex ProcWord = new(@"와이핑|WIPING|외판|내판|도어|후드|트렁크|클리어|베이스|프라이머|중도|상도|하도|EXT|INT|IBS|LASD|UBS|UBC|실러|샌딩", Opt);
    private static readonly Regex Group = new(@"^(?:중도|상도|하도|제진\s*장치|전처리|전착)$", Opt);
    private static readonly Regex InstallQty = new(@"(\d{1,3})\s*대\s*(?:[×xX*]\s*(\d)\s*라인)?", Opt);

    public sealed record Column(int Index, string Name, Dictionary<string, string> Items, int Page)
    {
        public int Robots { get; set; }
        public int Lines { get; set; }
    }

    /// <summary>공정 열(2개 이상일 때만). 없으면 빈 목록</summary>
    public static List<Column> Read(IReadOnlyList<Block> blocks) => Read(blocks, out _);

    /// <param name="extras">설치 대수 목록 중 공정 열에 안 맞은 것(교육장 1라인 등) — Total에 더한다</param>
    public static List<Column> Read(IReadOnlyList<Block> blocks, out List<(string key, int n, int lines)> extras)
    {
        extras = [];
        foreach (var table in blocks.Where(b => b.Cells.Count >= 6).GroupBy(b => (b.UnitIndex, b.Table)))
        {
            var rows = table.OrderBy(b => b.Row).ToList();
            // 공정 이름 행: 공정 낱말이 든 칸이 4개 이상
            var head = rows.Select((b, i) => (b, i, n: b.Cells.Count(c => ProcWord.IsMatch(c) && c.Trim().Length <= 30))).Where(x => x.n >= 4).OrderByDescending(x => x.n).FirstOrDefault();
            if (head.b is null) continue;
            var idx = head.b.Cells.Select((c, i) => (c: Clean(c), i)).Where(x => x.c.Length > 0 && ProcWord.IsMatch(x.c) || x.c is "와이핑").Select(x => x.i).ToList();
            // 이름 행 왼쪽 칸(항목 이름 자리)은 빼고, 값 칸만
            var firstCol = idx.Count > 0 ? idx.Min() : 0;
            idx = idx.Where(i => i >= firstCol).ToList();
            if (idx.Count < 2) continue;
            // 바로 위 행이 묶음(중도·상도·제진장치)이면 이름 앞에 붙인다
            var groupRow = head.i > 0 ? rows[head.i - 1] : null;
            string GroupOf(int col)
            {
                if (groupRow is null) return "";
                for (var j = Math.Min(col, groupRow.Cells.Count - 1); j >= firstCol; j--)
                    if (Clean(groupRow.Cells[j]) is { Length: > 0 } g && Group.IsMatch(g)) return Regex.Replace(g, @"\s+", "");
                return "";
            }
            var cols = idx.Select(i =>
            {
                var name = Clean(head.b.Cells[i]);
                var g = GroupOf(i);
                if (g.Length > 0 && !name.Contains(g) && g is not "제진장치") name = $"{g} {name}";
                return new Column(i, name, [], table.Key.UnitIndex);
            }).ToList();

            // 항목 행: 왼쪽 칸들(항목·소항목), 소항목만 있으면 위 항목을 이어받는다(부스 → 길이/폭)
            var cat = "";
            foreach (var r in rows.Skip(head.i + 1))
            {
                var label = r.Cells.Take(firstCol).Select(Clean).Where(c => c.Length > 0).ToList();
                if (label.Count == 0) continue;
                if (label.Count >= 2) cat = label[^2];
                var item = label.Count >= 2 ? $"{label[^2]} {label[^1]}" : $"{cat} {label[0]}".Trim();
                // "←"는 왼쪽 칸과 같다는 뜻 — 칸 안 줄마다 따로("고효율벨 / DualFGP" 오른쪽 "← / -"는 고효율벨만 같고 DualFGP는 없음)
                string[] prev = [];
                foreach (var c in cols)
                {
                    var raw = c.Index < r.Cells.Count ? r.Cells[c.Index] : "";
                    var parts = raw.Split('\n').Select(x => x.Trim()).ToArray();
                    // 한 줄짜리 "←"는 왼쪽 칸 전체(왼쪽 칸이 글자 넘침으로 두 줄이 된 "유성내부하 / 전"도 통째로)
                    if (parts.Length == 1 && parts[0].StartsWith('←'))
                        parts = [.. prev.Take(Math.Max(1, prev.Length)).Select((x, j) => j == 0 ? (x + " " + parts[0].TrimStart('←').Trim()).Trim() : x)];
                    else
                        for (var j = 0; j < parts.Length; j++)
                            if (parts[j].StartsWith('←'))
                                parts[j] = ((j < prev.Length ? prev[j] : string.Join(" ", prev)) + " " + parts[j].TrimStart('←').Trim()).Trim();
                    c.Items[item] = Clean(string.Join(" ", parts.Where(x => x is not "-" || parts.Length == 1)));
                    prev = parts;
                }
            }
            if (cols.Count(c => c.Items.Values.Any(v => v.Length > 0)) < 2) continue;

            // 공정별 로봇 대수: 같은 쪽 '설치 대수' 목록
            var install = new List<(string key, int n, int lines)>();
            var last = "";
            foreach (var r in rows.Take(head.i))
            {
                var t = string.Join(" ", r.Cells.Select(Clean).Where(c => c.Length > 0));
                if (InstallQty.Match(t) is not { Success: true } q) continue;
                var words = r.Cells.Select(Clean).Where(c => c.Length > 0 && !InstallQty.IsMatch(c) && !Regex.IsMatch(c, "설치|대수|합계")).ToList();
                if (words.Count == 0) continue;
                // "베이스 | 내판#1" 다음 줄 "내판#2"처럼 앞 칸이 비면 위 줄의 공정 이름을 이어받는다
                if (Regex.IsMatch(words[0], "^(?:중도|상도|베이스|클리어|와이핑|프라이머|교육장)", RegexOptions.IgnoreCase)) last = Regex.Match(words[0], "^(중도|상도|베이스|클리어|와이핑|프라이머|교육장)").Value;
                var key = (words[0].StartsWith(last) ? "" : last + " ") + string.Join(" ", words);
                install.Add((key, int.Parse(q.Groups[1].Value), q.Groups[2].Success ? int.Parse(q.Groups[2].Value) : 1));
            }
            var used = new HashSet<string>();
            foreach (var c in cols)
            {
                var best = install.Select(x => (x, s: Score(c.Name, x.key))).Where(x => x.s > 0).OrderByDescending(x => x.s).FirstOrDefault();
                if (best.s > 0) { c.Robots = best.x.n; c.Lines = best.x.lines; used.Add(best.x.key); }
            }
            extras = install.Where(x => !used.Contains(x.key) && !Regex.IsMatch(x.key, "합계")).ToList();
            return cols;
        }
        return [];
    }

    /// <summary>
    /// 같은 사양서의 다른 공정별 표(화성4 12쪽 "로봇시스템(1라인기준)": 와이핑로봇 | 중도외판 | 베이스내판 | 베이스외판 | 클리어내판 | 클리어외판)에서
    /// 항목 줄(label 정규식) 값을 공정 열로 옮긴다. 머리는 값 줄 위의 공정 이름 줄들(칸이 두 줄로 나뉜 "베이스외 / 판")을 이어 붙여 읽고,
    /// 이름이 가장 잘 맞는 공정 열에(베이스내판 → H/T·DOOR 둘 다) 넣는다.
    /// </summary>
    private static Dictionary<int, (string value, int page)> SideItem(IReadOnlyList<Block> blocks, List<Column> cols, Regex label)
    {
        var result = new Dictionary<int, (string, int)>();
        foreach (var table in blocks.Where(b => b.Cells.Count >= 4).GroupBy(b => (b.UnitIndex, b.Table)))
        {
            var rows = table.OrderBy(b => b.Row).ToList();
            var at = rows.FindIndex(r => r.Cells.Take(4).Any(c => label.IsMatch(Clean(c))));
            if (at < 0) continue;
            var heads = rows.Take(at).Where(r => r.Cells.Count(c => ProcWord.IsMatch(c)) >= 2).TakeLast(3).ToList();
            if (heads.Count == 0) continue;
            // 바로 위 머리 줄과 그 위 줄(글자 넘침으로 나뉜 이름)을 칸마다 이어 붙인다
            var top = rows.Take(rows.IndexOf(heads[0])).LastOrDefault();
            var headRows = (top is not null ? new[] { top } : []).Concat(heads).ToList();
            var row = rows[at];
            var labelCol = row.Cells.FindIndex(c => label.IsMatch(Clean(c)));
            for (var i = labelCol + 1; i < row.Cells.Count; i++)
            {
                var v = Clean(row.Cells[i]);
                if (v.Length == 0) continue;
                var name = Regex.Replace(string.Concat(headRows.Select(h => i < h.Cells.Count ? Clean(h.Cells[i]) : "")), @"\s+", "");
                if (!ProcWord.IsMatch(name)) continue;
                var best = cols.Select((c, k) => (k, sc: Score(c.Name.Replace(" ", ""), name))).Where(x => x.sc > 0).ToList();
                if (best.Count == 0) continue;
                var top2 = best.Max(x => x.sc);
                foreach (var b in best.Where(x => x.sc == top2)) result.TryAdd(b.k, (v, table.Key.UnitIndex));
            }
            if (result.Count > 0) return result;
        }
        return result;
    }

    /// <summary>공정 이름이 얼마나 같은가: 공정(와이핑·중도·베이스·클리어)·외/내판·부위(도어·후드·트렁크)가 같은 만큼</summary>
    private static int Score(string col, string key)
    {
        int s = 0;
        foreach (var w in new[] { "와이핑", "중도", "베이스", "클리어", "프라이머" })
            if (col.Contains(w) && key.Contains(w)) s += 3;
            else if (col.Contains(w) != key.Contains(w) && (col.Contains(w) || key.Contains(w)) && w != "중도") s -= 2;
        foreach (var w in new[] { "외판", "내판" })
            if (col.Contains(w) && key.Contains(w)) s += 2; else if (col.Contains(w) && Regex.IsMatch(key, "외판|내판")) s -= 3;
        foreach (var w in new[] { "도어", "후드", "트렁크" })
            if (col.Contains(w) && key.Contains(w)) s += 2; else if (col.Contains(w) && Regex.IsMatch(key, "도어|후드|트렁크")) s -= 3;
        return s;
    }

    /// <summary>
    /// 여러 공정 초안은 Stand by·Others(·기타)만 한 칸으로 두고 나머지 줄은 모두 공정마다 칸을 나눈다 — 같은 값이면 같은 글을 칸마다(2026-10-28 사용자).
    /// 칸 합치기·나누기는 화면에서 줄마다 할 수 있다(DraftEdit "merge"/"unmerge").
    /// </summary>
    public static void Unmerge(SummaryDraft d)
    {
        // 공정 열이 2개 이상이면(여러 공정 표든 FRT/RR 나누기든) 같은 규칙
        if (d.Processes.Count < 2) return;
        // FRT/RR처럼 나눈 초안의 Panel: 사양서 수량은 전체 합계라 칸마다 복사하면 두 배가 된다 → 공정 로봇 수로 다시 셈(사양정리 기준: 판넬은 항상 수량과 함께)
        var panel = d.Rows.First(r => r.Key == "Panel");
        if (!d.ProcessTable && panel.Split.Count == 0 && !panel.Merged && !panel.Edited && panel.Value.Length > 0)
        {
            var kinds = panel.Value.Split(" / ").Select(x => Regex.Replace(x, @"^\d+", "").Trim()).Where(x => x.Length > 0).ToList();
            panel.Split = d.Processes.Select(p => string.Join(" / ", kinds.Select(k => k switch
            {
                "RC" => p.Spray + p.Opener > 0 ? $"{p.Spray + p.Opener}RC" : "RC",
                "AP" => p.Spray > 0 ? $"{p.Spray}AP" : "AP",
                _ => $"1{k}",
            }))).ToList();
            panel.Remark = string.Join("\n", panel.Remark.Split('\n').Where(n => !n.StartsWith("수량은 사양서에 없어")));
            panel.AddRemark($"사양서 판넬 수량({panel.Value})은 전체 합계라 공정마다 다시 셈: RC = 공정 로봇 수, AP = 스프레이 수, MCP·OP·VSP·분전반 = 공정마다 1 — VSP·분전반은 공용일 수 있음, 확인");
        }
        // (같은 글을 칸마다 복사하지 않는다 — 2026-10-28 사용자. 공정마다 값이 다른 줄만 칸이 나뉘고, 나머지는 한 칸. 화면에서 셀 병합·나누기·행 추가·삭제)
    }

    private static readonly Regex RoleSplit = new(@"(H\s*/\s*T|후드|트렁크|DOOR|도어)?\s*[:：]?\s*(\d{1,2})\s*\(\s*(?:SPRAY|스프레이)\s*(\d{1,2})\s*/\s*(?:OPENER|오프너)\s*(\d{1,2})\s*\)", Opt);
    private static readonly Regex PaintHead = new(@"(중도|베이스|클리어)\(((?:\dK|수성|유성|\dL|,)+)\)", Opt);

    /// <summary>영업부 열 코드: 공정(PRIMER·BASE·CLEAR·WIPING) + 부위(DOOR·H/T) + 외/내판(EXT·INT)</summary>
    public static string CodeOf(string name)
    {
        if (Regex.IsMatch(name, "와이핑|WIPING", RegexOptions.IgnoreCase)) return "WIPING";
        var proc = Regex.IsMatch(name, "클리어") ? "CLEAR" : Regex.IsMatch(name, "베이스") ? "BASE" : Regex.IsMatch(name, "중도") ? "PRIMER" : Regex.IsMatch(name, "하도") ? "UNDER" : "";
        var area = Regex.IsMatch(name, "후드|트렁크") ? "H/T" : Regex.IsMatch(name, "도어") ? "DOOR" : "";
        var part = Regex.IsMatch(name, "외판") ? "EXT" : Regex.IsMatch(name, "내판") ? "INT" : "";
        var code = string.Join(" ", new[] { proc, area, part }.Where(x => x.Length > 0));
        return code.Length > 0 ? code : "";
    }

    // "8.5 m" → "8,500", "12.0 m" → "12,000"
    private static string Mm(string v) =>
        Regex.Match(v, @"(\d+(?:\.\d+)?)\s*m\b") is { Success: true } m && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var x)
            ? (x * 1000).ToString("#,0", System.Globalization.CultureInfo.InvariantCulture) : v;

    private static string Clean(string c) => Regex.Replace(c.Replace("\n", " "), @"\s+", " ").Trim().Replace("베이스내 판", "베이스 내판").Replace("클리어내 판", "클리어 내판")
        .Replace("(후드/트렁 크)", "(후드/트렁크)").Replace("내 판", "내판").Replace("외 판", "외판").Replace("부하 전", "부하전");

    /// <summary>
    /// 표가 있으면 초안을 공정 열로: Robot·Booth size·Basic Condition·Atomizer·Bell Cleaner·FGP 줄을 공정마다(Split), Total 대수, 기타 사항(디그레이드·도어오픈).
    /// 값은 사양서 표 그대로(지어내지 않음). 공정별 모델 추천은 아직 하지 않는다(참고 사항에 안내).
    /// </summary>
    public static bool Apply(SummaryDraft d, IReadOnlyList<Block> blocks)
    {
        var cols = Read(blocks, out var extras);
        if (cols.Count < 2) return false;
        string Get(Column c, params string[] keys) =>
            keys.Select(k => c.Items.FirstOrDefault(kv => Regex.IsMatch(Regex.Replace(kv.Key, @"\s+", ""), k, RegexOptions.IgnoreCase)).Value).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
        var page = cols[0].Page;
        // 공정별 스프레이/오프너(라인당): 사양서 "H/T : 7 (Spray 4 / Opener 3)", "Door : 4 (Spray2 / Opener 2)"(화성4 12쪽) — 내판 열에 부위·대수가 맞는 것
        var splits = blocks.SelectMany(b => RoleSplit.Matches(Regex.Replace(b.Text, @"\s+", " "))).Select(m =>
            (area: m.Groups[1].Value, n: int.Parse(m.Groups[2].Value), spray: int.Parse(m.Groups[3].Value), opener: int.Parse(m.Groups[4].Value))).ToList();
        (int spray, int opener, bool known) RolesOf(Column c)
        {
            if (Regex.IsMatch(c.Name, "와이핑")) return (c.Robots, 0, true);
            var hit = splits.FirstOrDefault(x => x.n == c.Robots && x.spray + x.opener == x.n
                && (Regex.IsMatch(c.Name, "후드|트렁크") ? Regex.IsMatch(x.area, @"H\s*/\s*T|후드|트렁크", RegexOptions.IgnoreCase)
                    : Regex.IsMatch(c.Name, "도어") ? Regex.IsMatch(x.area, "DOOR|도어", RegexOptions.IgnoreCase) : x.area.Length == 0));
            if (hit.n > 0) return (hit.spray, hit.opener, true);
            return Regex.IsMatch(c.Name, "외판") ? (c.Robots, 0, true) : (c.Robots, 0, false); // 외판은 오프너 없음, 내판인데 나눔이 없으면 모름
        }
        var roles = cols.Select(RolesOf).ToList();
        // 열 제목은 영업부 사양정리처럼 영문 코드(WIPING · PRIMER EXT · BASE H/T INT · BASE DOOR INT · BASE EXT · CLEAR DOOR INT · CLEAR EXT)
        d.Processes = cols.Select((c, i) => new SpecProcess(0, c.Name, CodeOf(c.Name),
            roles[i].spray * Math.Max(1, c.Lines), roles[i].opener * Math.Max(1, c.Lines), page)).ToList();
        void Split(string key, Func<Column, string> f)
        {
            var row = d.Rows.First(r => r.Key == key);
            if (row.Edited) return;
            var vals = cols.Select(f).ToList();
            if (vals.All(v => v.Length == 0)) return;
            row.Split = vals.Select(v => v.Length > 0 ? v : "-").ToList();
            row.Value = string.Join("\n", cols.Zip(row.Split, (c, v) => $"**{c.Name}** : {v}"));
            if (!row.Pages.Contains(page)) row.Pages = [.. row.Pages.Append(page).Order()];
        }
        Split("Robot", c =>
        {
            // 영업부 표기 "MPX3600 : 8 / MPX2600 : 4"처럼 용도별 라인 합계 대수 — 모델은 기본 사양·채택 때 "스프레이 MPX3600 : 8"로 들어간다
            var i = cols.IndexOf(c);
            var lines = Math.Max(1, c.Lines);
            var (sp, op, known) = roles[i];
            var head = c.Robots == 0 ? "" : Regex.IsMatch(c.Name, "와이핑") ? $"와이핑 : {sp * lines}"
                : known && op > 0 ? $"스프레이 : {sp * lines} / 오프너 : {op * lines}" : known ? $"스프레이 : {sp * lines}" : $"로봇 : {c.Robots * lines}";
            // 로봇 칸은 영업부처럼 '모델 : 대수'만 — 축·방폭·가반하중은 고정방식 줄에
            return head;
        });
        Split("Booth size", c =>
        {
            string len = Get(c, "부스길이", "길이"), wid = Get(c, "부스폭", "^.*폭$");
            // 영업부 표기 "8,500 X 5,500mm"(m → mm). 폭 뒤 괄호("주행축구간7.1m")는 뒤에 그대로
            var extra = Regex.Match(wid, @"\s*\(.*\)\s*$").Value.Trim();
            if (extra.Length > 0) wid = wid[..^Regex.Match(wid, @"\s*\(.*\)\s*$").Length].Trim();
            return len.Length > 0 || wid.Length > 0 ? $"{Mm(len)} X {Mm(wid)}mm{(extra.Length > 0 ? " " + extra : "")}" : "";
        });
        // Basic Condition은 영업부처럼 한 칸: "30 UPH / Continuous + Stop&Go + Continuous / 사이클타임 120초 / 피치 7.2m"
        // (C/V 방식은 공정 순서대로 바뀌는 곳만 +로, 사이클타임은 사양서에 없으면 3600 ÷ UPH 계산 — 참고 사항에 표시)
        {
            var basic = d.Rows.First(r => r.Key == "Basic Condition");
            var uph = cols.Select(c => Get(c, "UPH운영", "UPH")).Where(v => v.Length > 0).Distinct().ToList();
            var cv = new List<string>();
            foreach (var v in cols.Select(c => Get(c, "C/V방식", "방식$")).Where(v => v.Length > 0))
                if (cv.Count == 0 || cv[^1] != v) cv.Add(v);
            var pitch = cols.Select(c => Get(c, "피치")).Where(v => v.Length > 0).Distinct().ToList();
            if (!basic.Edited && (uph.Count > 0 || cv.Count > 0))
            {
                var parts = new List<string>();
                if (uph.Count > 0) parts.Add(string.Join("·", uph) + " UPH");
                if (cv.Count > 0) parts.Add(string.Join(" + ", cv));
                if (uph.Count == 1 && double.TryParse(uph[0], System.Globalization.CultureInfo.InvariantCulture, out var u) && u > 0)
                {
                    parts.Add($"사이클타임 {Math.Round(3600 / u, 1)}초");
                    basic.AddRemark($"사이클타임은 {uph[0]} UPH 기준 계산 (3600 ÷ {uph[0]})");
                }
                if (pitch.Count > 0) parts.Add("피치 " + string.Join("·", pitch));
                basic.Value = string.Join(" / ", parts);
                basic.Split = [];
                if (!basic.Pages.Contains(page)) basic.Pages = [.. basic.Pages.Append(page).Order()];
            }
        }
        // 공정별 도료: 사양서 도료 조건 표 머리 "중도(1K,수성,2L) | 베이스(1K,수성,2L) | 클리어(1K,유성,2L)"
        var paints = blocks.SelectMany(b => PaintHead.Matches(Regex.Replace(b.Text, @"\s+", "")).Select(m => (b, m)))
            .GroupBy(x => x.m.Groups[1].Value).ToDictionary(g => g.Key, g => g.First());
        if (paints.Count > 0)
        {
            Split("Paint", c =>
            {
                var proc = Regex.Match(c.Name, "중도|베이스|클리어").Value;
                if (!paints.TryGetValue(proc, out var hit)) return "";
                var m = hit.m;
                var k = Regex.Match(m.Groups[2].Value, @"(\d)K").Groups[1].Value;
                var water = Regex.Match(m.Groups[2].Value, "수성|유성").Value;
                return string.Join(" / ", new[] { water, k.Length > 0 ? $"{k}K" : "" }.Where(x => x.Length > 0));
            });
            var pr = d.Rows.First(r => r.Key == "Paint");
            pr.Remark = string.Join("\n", pr.Remark.Split('\n').Where(n => !n.StartsWith("사양서 원문 줄")));
            var pp = paints.Values.First().b.UnitIndex;
            if (!pr.Pages.Contains(pp)) pr.Pages = [.. pr.Pages.Append(pp).Order()];
        }
        // "← -"(왼쪽과 같음, 둘째 줄 없음)에서 남는 '-'와 FGP 표기는 뺀다
        static string Tidy(string v) => Regex.Replace(Regex.Replace(v, @"(?<!\S)-(?!\S)|Dual\s*FGP", "", RegexOptions.IgnoreCase), @"\s+", " ").Trim();
        Split("Atomizer", c => string.Join(" · ", new[] { Tidy(Get(c, "도장기방식")), Tidy(Get(c, "도장기타입")) }.Where(s => s.Length > 0)));
        // 세정장치는 그 공정 스프레이 수만큼(영업부 "벨 클리너 : 16")
        Split("Bell Cleaner", c => Get(c, "세정장치") switch
        {
            "○" or "O" or "●" => roles[cols.IndexOf(c)].spray is > 0 and var n ? $"{EquipmentRows.CleanerPlaceholder} : {n * Math.Max(1, c.Lines)}" : "세정장치 적용",
            "X" or "x" or "-" => "-",
            var v => v,
        });
        // 고정방식(영업부 줄): 로봇 Axis 칸 — "6축 (Half 벽걸이형)" → 하프 벽걸이, "7축 (주행축포함)" → 7축 레일
        {
            // 괄호에 사양서 로봇 사양(축·방폭·가반하중) — 로봇 칸에서 옮겨 옴
            var fix = cols.Select(c =>
            {
                var ax = Get(c, "Axis", "축");
                var type = Get(c, "로봇타입", "타입$");
                var load = Get(c, "가반");
                var mount = Regex.IsMatch(ax, "벽걸이|HALF", RegexOptions.IgnoreCase) ? "하프 벽걸이"
                    : Regex.IsMatch(ax, @"주행|7\s*축") ? "7축 레일" : Regex.IsMatch(type, "비방폭") ? "고정" : "";
                var spec = string.Join(", ", new[] { Regex.Replace(ax, @"\s*\(.*\)", "").Trim(), type, load.Length > 0 ? $"가반 {load}" : "" }.Where(s => s.Length > 0));
                return mount.Length > 0 ? mount + (spec.Length > 0 ? $" ({spec})" : "") : spec;
            }).ToList();
            if (fix.Any(f => f.Length > 0))
            {
                var fr = DraftLayout.EnsureRow(d, "고정방식");
                fr.Split = fix.Select(f => f.Length > 0 ? f : "-").ToList();
                fr.Value = string.Join("\n", cols.Zip(fr.Split, (c, v) => $"**{c.Name}** : {v}"));
                fr.Pages = [page];
                fr.Source = [$"고정방식 ← 설비사양 표 로봇 Axis (p{page})"];
            }
        }
        Split("FGP", c => Regex.IsMatch(Get(c, "도장기타입"), "DUAL\\s*FGP", RegexOptions.IgnoreCase) ? "Dual FGP" : "");
        // 공정별 ○/X 항목은 사양서 말이라 기타 사항에 공정 이름으로
        void Mark(string rowKey, string item, string label)
        {
            var on = cols.Where(c => Get(c, item) is { Length: > 0 } v && v is not ("X" or "x" or "-")).Select(c => Get(c, item) is "○" or "O" ? c.Name : $"{c.Name}({Get(c, item)})").ToList();
            if (on.Count == 0) return;
            var row = d.Rows.First(r => r.Key == rowKey);
            row.Note = string.Join("\n", new[] { row.Note, $"{label}: {string.Join(", ", on)}" }.Where(s => s.Length > 0));
            row.Source = [.. row.Source, $"{label}: {string.Join(", ", on)} ← 설비사양 표 (p{page})"];
        }
        // Color(CCV): 공정별 표의 CCV 밸브 수량·CCV 타입(싱글/듀얼) — 화성4 12쪽
        {
            var qty = SideItem(blocks, cols, new Regex(@"CCV\s*밸브\s*수량|CCV\s*수량|칼라\s*수", RegexOptions.IgnoreCase));
            var kind = SideItem(blocks, cols, new Regex(@"CCV\s*타입", RegexOptions.IgnoreCase));
            if (qty.Count > 0)
            {
                Split("Color(CCV)", c =>
                {
                    var k = cols.IndexOf(c);
                    if (!qty.TryGetValue(k, out var q) || q.value is "-" or "") return "";
                    var t = kind.TryGetValue(k, out var kv) ? (Regex.IsMatch(kv.value, "듀얼|DUAL", RegexOptions.IgnoreCase) ? "듀얼 " : Regex.IsMatch(kv.value, "싱글|SINGLE", RegexOptions.IgnoreCase) ? "싱글 " : "") : "";
                    return $"{t}CCV : {q.value}";
                });
                var cr = d.Rows.First(r => r.Key == "Color(CCV)");
                var qp = qty.Values.First().page;
                if (!cr.Pages.Contains(qp)) cr.Pages = [.. cr.Pages.Append(qp).Order()];
                cr.Source = [.. cr.Source, $"CCV 밸브 수량·타입 ← 공정별 로봇시스템 표 (p{qp})"];
                cr.Remark = string.Join("\n", cr.Remark.Split('\n').Where(n => !n.StartsWith("사양서 원문 줄")));
            }
        }
        // 스윙암 정전건: 베이스 내판처럼 도장기 칸이 '정전건'이고 사양서에 스윙암이 있으면 로봇과 별도 수량(라인당 1) — 기타 사항에
        var swing = blocks.FirstOrDefault(b => Regex.IsMatch(b.Text, @"스윙\s*암"));
        if (swing is not null)
            foreach (var c in cols.Where(c => Regex.IsMatch(Get(c, "도장기방식"), "건")))
            {
                var ar = d.Rows.First(r => r.Key == "Atomizer");
                var text = $"{CodeOf(c.Name)}: 스윙암 정전건 {Math.Max(1, c.Lines)}대(라인당 1) 별도";
                ar.Note = string.Join("\n", new[] { ar.Note, text }.Where(x => x.Length > 0));
                var src = Regex.Replace(swing.Text, @"\s+", " ").Trim();
                ar.Source = [.. ar.Source, $"{text} ← {(src.Length > 90 ? src[..90] + "…" : src)} (p{swing.UnitIndex})"];
            }
        Mark("Others", "디그레이드", "디그레이드");
        Mark("Robot", "도어오픈", "도어오픈");
        // 공정별 대수는 표(Robot 줄)에 있으니 Total에는 합계와 공정·라인 수만(왼쪽 글이 표를 덮지 않게). 공정 열에 없는 설치(교육장 1대)는 더해 따로 적는다
        var extraRobots = extras.Sum(x => x.lines > 1 ? x.n * x.lines : x.n);
        var total = cols.Sum(c => c.Robots * Math.Max(1, c.Lines)) + extraRobots;
        var lines = cols.Max(c => Math.Max(1, c.Lines));
        var extraText = string.Concat(extras.Select(x => $" + {Regex.Replace(x.key, @"\s*(교육용|기준).*$", "").Trim()} {(x.lines > 1 ? x.n * x.lines : x.n)}"));
        // 사양서 설치 대수 '합계'가 더 크면(교육장 "1라인"처럼 대수 없이 적힌 설치) 차이를 교육장·기타로
        if (blocks.Where(b => b.UnitIndex == page).Select(b => Regex.Match(Regex.Replace(b.Text, @"\s+", " "), @"합\s*계\s*\|?\s*(\d{1,3})\s*대")).FirstOrDefault(m => m.Success) is { } sum
            && int.Parse(sum.Groups[1].Value) is var specTotal && specTotal > total)
        {
            var label = blocks.Any(b => b.UnitIndex == page && Regex.IsMatch(b.Text, "교육")) ? "교육장" : "기타";
            extraText += $" + {label} {specTotal - total}";
            total = specTotal;
        }
        if (total > 0) d.Total = $"Total {{{{{total} robots}}}}\n(공정 {cols.Count}개{(lines > 1 ? $" × {lines}라인" : "")}{extraText})";
        // 판넬 수량(사양정리 기준: 판넬은 항상 수량과 함께): 공정마다 RC = 그 공정 로봇 수, AP = 스프레이 수, MCP·OP·VSP = 라인 수(영업부 화성4 "MCP 2 / OP 2 / VSP 2")
        {
            var pr = d.Rows.First(r => r.Key == "Panel");
            if (!pr.Edited && pr.Value.Length > 0)
            {
                var kinds = pr.Value.Split(" / ").Select(x => Regex.Replace(x, @"^\d+", "").Trim()).Where(x => x.Length > 0).ToList();
                pr.Split = cols.Select((c, k) =>
                {
                    var lines = Math.Max(1, c.Lines);
                    var robots = c.Robots * lines;
                    // 와이핑 로봇은 도장 공압판넬(AP)이 없다
                    var spray = Regex.IsMatch(c.Name, "와이핑") ? 0 : roles[k].spray * lines;
                    return string.Join(" / ", kinds.Select(kd => kd switch
                    {
                        "RC" => robots > 0 ? $"{robots}RC" : "RC",
                        "AP" => spray > 0 ? $"{spray}AP" : "-",
                        // MCP·OP·VSP·분전반 등 나머지는 라인마다 1(판넬은 항상 수량과 함께)
                        _ => $"{lines}{kd}",
                    }).Where(x => x != "-"));
                }).ToList();
                pr.Value = string.Join("\n", cols.Zip(pr.Split, (c, v) => $"**{c.Name}** : {v}"));
                pr.Remark = string.Join("\n", pr.Remark.Split('\n').Where(n => !n.StartsWith("수량은 사양서에 없어")));
                pr.AddRemark("판넬 수량은 사양서에 없어 공정·라인으로 셈: RC = 그 공정 로봇 수, AP = 스프레이 수(와이핑 없음), MCP·OP·VSP·분전반 = 라인 수(라인마다 1) — 확인");
            }
        }
        d.Rows.First(r => r.Key == "Robot").AddRemark("공정이 여러 개라 사양서 설비사양 표대로 공정마다 열을 나눔 — 로봇·도장기·FGP 모델은 공정마다 따로 추천(기본 사양), '참고'에서 공정별로 바꿈");
        return true;
    }
}
