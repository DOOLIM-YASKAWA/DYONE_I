using System.Text.RegularExpressions;
using DyOne.Spec.Extraction;

namespace DyOne.Spec.Draft;

/// <summary>
/// 사양정리 2장 '기타 사항' 칸(규칙). 영업부 사양정리의 기타 사항에 자주 쓰는 종류(과거 사양정리 2025~2026 기타 사항 모음)를
/// 칸마다 정해 두고, 사양서에 그 내용이 있을 때만 짧은 문구로 적는다(사양서에 없는 말은 쓰지 않음).
/// 문구의 근거 원문 줄은 DraftRow.Source(화면 '원문', pptx 슬라이드 노트)에.
///  - Robot: 주행레일 길이(+2R1C) / Body: 설계 사이즈
///  - Utility: AO/AA 필터·차압계·에어히터, 배전반 별도 설치 / Color: Eco Paint Save
///  - Panel: 방진패드, 소화장치, 에어컨 / System: 임베디드
/// Others 줄(표 맨 아래, 영업부 양식)은 기타 사항이 아니라 값 칸에 부대 공사·부대 설비 목록을 " / "로 —
/// 과거 사양정리 Others 칸에 자주 나온 것(안전펜스, 라이트 커튼, 모니터링 시스템, 하부 보강, 그레이팅, 난연 장판, 판넬룸,
/// 레일하우스·양압덕트, 벨컵 세정 보관함, 로봇 자켓, 폐신너 회수 배관, 차종 비전, 엔코더 유닛) 중 사양서에 있는 것만.
///  - Training: 교육 인원·일정 추후 협의 / Stand by: 설비 보증(서보모터), M/D 리셋
/// 사양서 한 건에 맞춘 규칙이 아니라 같은 말이 나오면 어느 사양서든 같은 문구로 적는다.
/// </summary>
public static class RowNotes
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    private sealed record Rule(string Row, Regex Re, Func<Match, string, string?> Text);

    private static Rule R(string row, string re, Func<Match, string, string?> text) => new(row, new Regex(re, Opt), text);
    private static Rule R(string row, string re, string text) => R(row, re, (_, _) => text);

    private static readonly Rule[] Rules =
    [
        R("Robot", @"주행\s*레일\s*\(?\s*(?:레일\s*길이\s*)?[:：]?\s*(\d{1,2}(?:\.\d)?)\s*M\b", (m, t) =>
            $"주행레일 : {m.Groups[1].Value}m" + (Regex.Match(t, @"\b(\d)\s*R\s*(\d)\s*C\b", RegexOptions.IgnoreCase) is { Success: true } rc ? $" ({rc.Groups[1].Value}R{rc.Groups[2].Value}C)" : "")),
        R("Body Size", @"(?:기본\s*)?설계\s*(?:조건|사이즈|치수)\s*[:：]\s*(\d[\d,]*\s*\(?L\)?\s*[x×*]\s*\d[\d,]*\s*\(?W\)?\s*[x×*]\s*\d[\d,]*\s*\(?[WH]\)?)", (m, _) =>
            // 세 번째(높이) 값이 (W)로 적힌 사양서가 있어 고치지 않고 확인 표시
            $"설계 사이즈 : {m.Groups[1].Value.Trim()}" + (Regex.IsMatch(m.Groups[1].Value, @"W\)?\s*$") ? " — 마지막 값이 (W)로 적힘, 높이(H)인지 확인" : "")),
        R("Body Size", @"DESIGN\s*(?:MAX\.?\s*)?(?:BODY\s*)?SIZE\s*[:：(]?\s*(\d[\d,]*\s*[x×*].{5,30}?\d)", (m, _) => $"Design Max. Body Size ({m.Groups[1].Value.Trim()})"),
        R("Body Size", @"MAXIMUM\s*BODY\s*SIZE.{0,60}?(\d,\d{3}\s*mm\s*L\s*[x×*]\s*\d,\d{3}\s*mm\s*W\s*[x×*]\s*\d,\d{3}\s*mm\s*H)", (m, _) => $"Design Max. Body Size ({m.Groups[1].Value.Trim()})"),
        R("Utility", @"A\s*A\s*[,/]\s*A\s*O|A\s*O\s*[,/]\s*A\s*A", "AO/AA 필터"),
        R("Utility", @"차압\s*(?:계|게이지)", "차압계"),
        R("Utility", @"에어\s*히터|AIR\s*HEATER", "에어히터"),
        R("Utility", @"배전반[^.。]{0,15}별도\s*(?:로\s*)?설치", "배전반 별도 설치"),
        R("Color(CCV)", @"ECO\s*(?:PAINT\s*)?SAVE|에코\s*(?:페인트\s*)?세이브", "Eco Paint Save 적용"),
        // FGP: 기본 PSMP 5CC FGP와 다르게 볼 이유가 될 사양서 요구(분당 토출량·경화제·듀얼 FGP·압력센서·축세정)
        // 분당 토출량: 도장기(벨) 토출량("3만 RPM 이상에서 500cc/min")은 FGP가 아니라 뺀다 — 정량 펌프 항목의 값만
        R("FGP", @"(최대\s*)?(\d{2,4})\s*cc\s*/\s*(?:min|분)(\s*이상)?|분\s*당\s*(최대\s*)?(\d{2,4})\s*cc(\s*이상)?", (m, t) =>
        {
            var at = t[Math.Max(0, m.Index - 40)..Math.Min(t.Length, m.Index + m.Length + 10)];
            if (Regex.IsMatch(at, @"RPM|벨|BELL|도장기|ATOMIZER|무화", RegexOptions.IgnoreCase)) return null;
            var n = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[5].Value;
            var max = m.Groups[1].Success || m.Groups[4].Success;
            var over = m.Groups[3].Success || m.Groups[6].Success;
            var err = Regex.Match(t, @"오차[^.。|]{0,15}?(±\s*\d+(?:\.\d+)?\s*%)") is { Success: true } e ? $" (오차 {e.Groups[1].Value.Replace(" ", "")} 이내)" : "";
            return $"{(max ? "최대 " : "")}{n}cc/min{(over ? " 이상" : "")} 토출{err} — FGP 용량·회전수 확인";
        }),
        R("FGP", @"경화제|HARDENER|2\s*액|\b2\s*K\b", (_, t) => Regex.IsMatch(t, @"FGP|정량|펌프|PUMP|믹서|MIX|도료|PAINT|공급", RegexOptions.IgnoreCase) ? "경화제(2K) 사용 — 경화제 FGP 별도 확인" : null),
        R("FGP", @"(?:듀얼|더블|DUAL|DOUBLE)\s*FGP|(?:2|두)\s*개\s*이상의\s*FGP|FGP\s*2\s*(?:개|EA|SET)", "듀얼 FGP"),
        R("FGP", @"FGP[^.。|]{0,25}(?:전\s*/\s*후단|입\s*[,·/]?\s*출구|IN\s*/\s*OUT)[^.。|]{0,15}압력|압력\s*센서[^.。|]{0,25}FGP|(?:정량\s*펌프|FGP)[^.。|]{0,30}압력\s*센서", "입·출구 압력 센서 적용"),
        R("FGP", @"(?:FGP|정량\s*펌프|기어\s*펌프)[^.。|]{0,20}축\s*세정|축\s*세정[^.。|]{0,20}(?:FGP|펌프)", "FGP 축세정 기능"),
        R("Panel", @"방진\s*패드", "판넬 방진패드"),
        R("Panel", @"(?:판넬|제어반|반\s*내)[^.。]{0,25}소화\s*(?:설비|장치)|소화\s*(?:설비|장치)[^.。]{0,15}(?:판넬|제어반)", "판넬 소화장치"),
        R("Panel", @"RC\s*(?:판넬|제어반)[^.。]{0,25}에어컨|에어컨[^.。]{0,15}RC\s*(?:판넬|제어반)", "RC 판넬 에어컨"),
        R("Panel", @"(?:클린룸|판넬\s*룸|판낼\s*룸|제어반\s*설치\s*공간)[^.。]{0,30}에어컨\s*(\d)?\s*대?", (m, _) => "판넬룸 에어컨" + (m.Groups[1].Success ? $" {m.Groups[1].Value}대" : "")),
        R("Panel", @"메인\s*제어반[^.。]{0,25}에어컨", "메인 제어반 에어컨"),
        R("System", @"임베디드|EMBEDDED", (_, t) => Regex.IsMatch(t, @"업체\s*제안") ? "임베디드 (업체 제안)" : "임베디드"),
        R("Training", @"교육\s*(?:인원|일정)[^.。]{0,20}(?:추후|협의|통보)", "교육 인원·일정 추후 협의"),
        R("Stand by", @"(?:설비\s*보증|SYSTEM\s*WARRANTY)\D{0,25}?(\d{1,2})\s*(년|YEARS?|개월)", (m, t) =>
            $"설비 보증 : {m.Groups[1].Value}{(m.Groups[2].Value.StartsWith("개") ? "개월" : "년")}"),
        R("Stand by", @"서보\s*모터\D{0,20}?(\d)\s*년|(\d)\s*년\s*WARRANTY", (m, _) => $"로봇 서보모터 보증 {(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}년"),
        R("Stand by", @"M\s*/\s*D\s*리셋|리셋[^.。]{0,10}M\s*/\s*D", "중대 비가동 시 M/D 리셋"),

        // Others: 부대 공사·부대 설비(영업부 사양정리 Others 칸 순서)
        // 안전펜스: 색상표("Safety fence frame | Yellow | 2.5Y 8/14")는 설치 요구가 아니라 다음 일치로
        R("Others", @"안전\s*[펜팬]스|SAFETY\s*FENCE", (_, t) => Regex.IsMatch(t, @"\d\.\d\s*Y\s*\d|색\s*상|COLOR", RegexOptions.IgnoreCase) ? null
            : Regex.IsMatch(t, @"철거") ? "안전펜스 (철거 후 신규 설치)" : "안전펜스 설치"),
        R("Others", @"라이트\s*커튼|LIGHT\s*CURTAIN", "라이트 커튼"),
        R("Others", @"인체\s*감지\s*(?:센서|포토\s*센서|장치)", "인체 감지 센서"),
        R("Others", @"(?:로봇\s*)?모니터링\s*시스템|ROBOT\s*HEALTH\s*MONITORING|MONITORING\s*SYSTEM", (_, t) =>
            Regex.IsMatch(t, @"기존|EXISTING|연결|CONNECT", RegexOptions.IgnoreCase) ? "모니터링 시스템 (기존 시스템 연결)" : "모니터링 시스템 설치"),
        R("Others", @"하부\s*(?:추가\s*)?보강", "하부 보강"),
        // 그레이팅·레일하우스·자켓은 설비 설명에도 나와서("그레이팅에서 실사이드 하단까지", "레일하우스 포함 7축 로봇", "로봇 커버 탈거")
        // 설치·교체·제공 같은 말이 가까이 있을 때만
        R("Others", @"(?:그레이팅|GRATING)[^.。|]{0,25}(?:교체|설치|개조|신규|신작|보강|MODIF|REPLAC)|(?:보강|교체|개조)[^.。|]{0,15}그레이팅", "그레이팅"),
        R("Others", @"난연\S*\s*장판|장판[^.。]{0,20}난연", "난연 장판 교체"),
        R("Others", @"(?:판넬|판낼)\s*룸[^.。]{0,20}(?:설치|신설|신작)|PANEL\s*ROOM", "판넬룸"),
        R("Others", @"(?:레일\s*하우스|양압\s*덕트)[^.。|]{0,15}(?:신설|설치|신규|신작|개조|교체)", "레일하우스·양압덕트"),
        R("Others", @"(?:벨\s*컵|슈라우드)[^.。]{0,20}(?:보관|세정)\s*(?:박스|함|BOX)", "벨컵 세정 보관함"),
        R("Others", @"(?:로봇\s*(?:자켓|쟈켓|커버)|ROBOT\s*(?:JACKET|COVER))[^.。|]{0,30}(?:제공|지급|적용|SET|EA|SUPPLY|PROVIDE)", "로봇 자켓(커버)"),
        R("Others", @"폐\s*신[너나][^.。]{0,10}(?:회수|배관)", "폐신너 회수 배관"),
        R("Others", @"차종\s*(?:감지|인식|판별)?\s*비[전젼]", "차종 비전"),
        R("Others", @"엔코더\s*유닛|ENCODER\s*UNIT", "엔코더 유닛"),
        // 2026-10-27 추가(과거 Others: 광주2 부스 벽체, 동희 출입 도어, 전주 배기덕트, 화성4 PCS 폐순수)
        // 부스 벽체: "부스 개조"(공사 범위 머리말)만으로는 넣지 않고 벽체를 철거·신설·개조한다는 말이 있을 때
        R("Others", @"(?:부스\s*)?벽체[^.。|]{0,20}(?:철거|신규|신설|교체|개조|설치)|(?:철거|교체|개조)[^.。|]{0,10}부스\s*벽체|BOOTH\s*WALL[^.。|]{0,20}(?:MODIF|REPLAC|NEW)", (m, _) =>
            Regex.IsMatch(m.Value, "철거") && Regex.IsMatch(m.Value, "신규|신설|신작|설치") ? "부스 벽체 (철거 후 신규 설치)" : "부스 벽체 개조"),
        // 출입 도어: 부스·작업자 출입 도어(플랩 도어)를 설치·신설한다는 말일 때만(경고 스티커 부착, 계단·사다리 개구부 출입도어는 아님)
        R("Others", @"(?:(?:부스|작업자|보전|로봇\s*존)\s*출입\s*(?:도어|문)|플랩\s*도어)[^.。|]{0,15}(?:설치|신설|신작|추가|포함)|(?:설치|신설|신작)[^.。|]{0,10}(?:(?:부스|작업자|보전|로봇\s*존)\s*출입\s*(?:도어|문)|플랩\s*도어)", (m, t) =>
            Regex.IsMatch(t, "사다리|계단|개구부|메자닌") ? null : Regex.IsMatch(m.Value, "플랩") ? "플랩 도어" : "부스 출입 도어"),
        // 배기덕트: 연결·설치 요구일 때만("이동: 레일, 배관, 배기덕트 등 간섭 구조물"은 아님)
        R("Others", @"배기\s*덕트[^.。|,]{0,10}(?:연결|설치|신설)|(?:연결|설치|신설)[^.。|,]{0,8}배기\s*덕트|EXHAUST\s*DUCT[^.。|]{0,15}(?:CONNECT|INSTALL)", (m, _) => Regex.IsMatch(m.Value, @"연결|CONNECT", RegexOptions.IgnoreCase) ? "배기덕트 연결" : "배기덕트 설치"),
        R("Others", @"폐\s*순수|PCS[^.。|]{0,15}(?:순수|회수)[^.。|]{0,10}(?:연결|배관)", "PCS 폐순수 라인 연결"),
    ];

    // Others 문구 뒤에 사양서에 적힌 수량·위치(LH/RH, 3EA, 2개소) — 일치 자리부터 70자 안, 이미 문구에 있으면 붙이지 않는다
    private static readonly Regex QtyNear = new(@"(\d{1,3})\s*(EA|SET|SETS|개소|대|개)\b", RegexOptions.IgnoreCase);
    private static readonly Regex PosNear = new(@"L\s*H\s*/\s*R\s*H|R\s*H\s*/\s*L\s*H|LH\s*[,·]\s*RH|좌\s*/?\s*우|입\s*[/·,]?\s*출구|입구\s*[/·,]\s*출구|(?<![가-힣])(?:입구|출구)(?:측|부)?(?![가-힣])", RegexOptions.IgnoreCase);

    private static string WithDetail(string text, string t, Match m)
    {
        // 위치는 같은 표 칸 안의 앞(입출구측 안전펜스)·뒤, 수량은 뒤만 본다
        var from = Math.Max(0, m.Index - 15);
        var before = t[from..m.Index];
        before = before[(before.LastIndexOf('|') + 1)..];
        var after = t.Substring(m.Index, Math.Min(t.Length - m.Index, m.Length + 85));
        var adds = new List<string>();
        foreach (var pos in PosNear.Matches(before + after).Select(p => Regex.Replace(p.Value, @"\s+", "").ToUpperInvariant().Replace(",", "/").Replace("·", "/")).Distinct())
            if (!text.Contains(pos) && !(pos.StartsWith("입") && Regex.IsMatch(text, "입|출")) && !adds.Any(a => a.Contains(pos) || pos.Contains(a))) adds.Add(pos);
        if (QtyNear.Match(after) is { Success: true } q && !Regex.IsMatch(text, @"\d")) adds.Add($"{q.Groups[1].Value}{q.Groups[2].Value.ToUpperInvariant()}");
        // "벽체/유리 등 개조 필요시 업체에서 진행"처럼 조건부 요구는 표시
        if (Regex.IsMatch(after, @"필요\s*시|필요한\s*경우|IF\s*(?:THERE|NEEDED|REQUIRED)", RegexOptions.IgnoreCase)) adds.Add("필요시");
        if (adds.Count == 0) return text;
        // "안전펜스 설치" + (입출구, LH/RH) → "안전펜스 설치 (입출구, LH/RH)", 이미 괄호가 있으면 그 안에 덧붙인다
        return text.EndsWith(')') ? $"{text[..^1]}, {string.Join(", ", adds)})" : $"{text} ({string.Join(", ", adds)})";
    }

    public static void Apply(SummaryDraft d, IReadOnlyList<Block> blocks)
    {
        // 표 칸이 줄마다 나뉘어 "설비보증 | 최종검수 완료후 2년간"처럼 항목과 값이 떨어지기도 해서, 블록 하나와 이어진 두 블록을 함께 본다
        var texts = new List<(int page, string t)>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var t = Regex.Replace(blocks[i].Text, @"\s+", " ").Trim();
            if (t.Length == 0) continue;
            texts.Add((blocks[i].UnitIndex, t));
            if (i + 1 < blocks.Count && blocks[i + 1].UnitIndex == blocks[i].UnitIndex)
                texts.Add((blocks[i].UnitIndex, t + " " + Regex.Replace(blocks[i + 1].Text, @"\s+", " ").Trim()));
        }
        foreach (var rule in Rules)
        {
            // 문구를 만들 수 있는 첫 일치(규칙이 null을 돌려주면 — 예: 벨 토출량 — 다음 일치로)
            var found = texts.SelectMany(x => rule.Re.Matches(x.t).Select(mm => (x.page, x.t, m: mm, text: rule.Text(mm, x.t))))
                .FirstOrDefault(f => f.text is { Length: > 0 });
            if (found.text is not { Length: > 0 } text) continue;
            var hit = (found.page, found.t);
            var row = d.Rows.First(r => r.Key == rule.Row);
            // 문구 " — " 뒤(확인 안내: "FGP 용량·회전수 확인")는 사양서 말이 아니라 참고 사항으로
            string? remark = null;
            if (text.IndexOf(" — ", StringComparison.Ordinal) is var dash and > 0) (text, remark) = (text[..dash], $"{text[..dash]}: {text[(dash + 3)..]}");
            var others = rule.Row == "Others"; // Others는 값 칸에 목록으로
            if (others) text = WithDetail(text, found.t, found.m);
            var have = (others ? row.Value.Split(" / ") : row.Note.Split('\n')).Where(h => h.Length > 0).ToList();
            if (have.Any(h => h.Replace("*", "").Contains(text, StringComparison.OrdinalIgnoreCase))) continue;
            have.Add(text);
            if (others) row.Value = string.Join(" / ", have);
            else row.Note = string.Join("\n", have);
            if (remark is not null) row.AddRemark(remark);
            var m = found.m;
            var from = Math.Max(0, m.Index - 20);
            var src = hit.t.Substring(from, Math.Min(hit.t.Length - from, 90)).Trim();
            row.Source = [.. row.Source, $"{text} ← {(from > 0 ? "…" : "")}{src}{(from + 90 < hit.t.Length ? "…" : "")} (p{hit.page})"];
            if (!row.Pages.Contains(hit.page)) row.Pages = [.. row.Pages.Append(hit.page).Order()];
        }
    }
}
