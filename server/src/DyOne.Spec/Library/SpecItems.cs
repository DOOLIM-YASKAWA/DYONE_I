using System.Text.RegularExpressions;
using DyOne.Spec.Extraction;

namespace DyOne.Spec.Library;

/// <summary>
/// 사양정리(당사 내부 정리본) 표의 항목. NAS 사양정리 pptx 2번 슬라이드 표가 프로젝트마다 같은 줄 머리(Process·Robot·Booth size …)를 쓴다.
/// Header: 사양정리 표의 줄 머리(첫 칸)로 알아보는 규칙. Keywords: 고객 사양서 원문에서 이 항목과 관련된 줄을 찾는 단어.
/// </summary>
public sealed record SpecItem(string Key, string Label, Regex Header, Regex Keywords);

public static class SpecItems
{
    private static SpecItem Item(string key, string label, string header, string keywords) =>
        new(key, label,
            new Regex($@"^\s*(?:{header})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(keywords, RegexOptions.IgnoreCase | RegexOptions.Compiled));

    public static readonly IReadOnlyList<SpecItem> All =
    [
        Item("process", "공정", "Process|공정명|공\\s*정|구\\s*분", "공정\\s*구성|설비\\s*용도|장비\\s*명|적용\\s*공정|공사\\s*개요"),
        Item("robot", "로봇", "Robot|로봇", "로봇|robot|MPX|EPX|IRB"),
        Item("booth", "부스 크기", "Booth|부스", "부스|booth"),
        Item("basic", "기본 조건", "Basic\\s*Condition|기본\\s*조건|생산\\s*조건", "UPH|사이클|cycle|컨베이어|conveyor|피치|생산조건|반송"),
        Item("body", "차종·모델", "Body|Model|차종", "차종|모델|model"),
        Item("utility", "유틸리티", "Utility|유틸", "전원|전압|AC\\s*\\d{3}\\s*V|압축\\s*공기|compressed|dry\\s*air|유틸"),
        Item("rail", "주행 레일", "Tracking|Rail|레일|주행", "레일|주행|tracking|rail"),
        Item("cleaner", "벨컵 세정장치", "Bell\\s*Clean\\w*|벨\\s*컵?\\s*세정\\w*|세정\\s*장치|Cleaner", "세정\\s*장치|cleaner|크리스탈|crystal"),
        Item("atomizer", "도장기", "Atomizer|도장기", "도장기|atomizer|정전\\s*벨|벨컵|bell|PPH|nanobell"),
        Item("paint", "도료", "Paint|도료", "도료|유성|수성|paint"),
        Item("color", "칼라(CCV)", "Color|CCV|칼라|컬러", "CCV|칼라|컬러|color|체인저"),
        Item("fgp", "정량펌프(FGP)", "FGP|정량\\s*펌프|기어\\s*펌프", "FGP|정량\\s*펌프|기어\\s*펌프|듀얼\\s*펌프"),
        Item("panel", "판넬", "Panel|판넬|제어반", "판넬|패널|제어반|분전반|panel"),
        Item("power", "전원 장치", "Power\\s*Unit|전원\\s*장치|파워\\s*유닛", "VSP|UPS|AVR|순간\\s*정전|정전\\s*보상"),
        Item("system", "제어 시스템", "System|PLC|시스템", "PLC|HMI|MELSEC|시스템"),
        Item("reinforce", "하부 보강", "하부\\s*보강", "하부\\s*보강|보강"),
        Item("training", "교육", "Training|교육", "교육|training"),
        Item("schedule", "일정", "Schedule|일정", "SOP|일정|납기|셧\\s*다운|shut\\s*down"),
        Item("standby", "양산 대기", "Stand\\s*-?\\s*by|양산\\s*대기|양산\\s*대응", "양산\\s*대기|양산\\s*대응|M/D|stand\\s*-?\\s*by"),
        Item("others", "기타", "Others|기\\s*타(?!\\s*사항)", "모니터링|안전\\s*펜스|라이트\\s*커튼|UPS|소화"),
    ];

    private static readonly Regex NoteHeader = new(@"^\s*(기타\s*사항|비\s*고|remark|note)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static SpecItem? MatchHeader(string cell)
    {
        var c = cell.Trim();
        if (c.Length == 0 || c.Length > 30) return null;
        return All.FirstOrDefault(i => i.Header.IsMatch(c));
    }

    /// <summary>
    /// 사양정리 표 읽기: 첫 칸이 항목 머리인 줄을 모은다. '기타 사항' 열(머리 줄에서 찾음)은 메모(Note)로 따로 둔다.
    /// 같은 항목이 여러 번 나오면(공정별 표가 여러 장) 처음 것을 쓴다.
    /// </summary>
    public static List<SheetRow> ReadSheet(IEnumerable<Block> blocks)
    {
        var rows = new List<SheetRow>();
        var seen = new HashSet<string>();
        foreach (var table in blocks.Where(b => b.Table > 0).GroupBy(b => (b.UnitIndex, b.Table)))
        {
            var list = table.ToList();
            var noteCol = -1;
            foreach (var b in list)
            {
                var item = MatchHeader(b.Cells.FirstOrDefault() ?? "");
                if (item is null) continue;
                // 머리 줄(보통 Process 줄)에 '기타 사항' 열이 있으면 그 열 번호를 기억
                var nc = b.Cells.FindIndex(1, c => NoteHeader.IsMatch(c));
                if (nc > 0) noteCol = nc;
                if (!seen.Add(item.Key)) continue;
                var valueCells = b.Cells.Skip(1).Where((_, i) => noteCol < 0 || i + 1 < noteCol).Where(c => c.Length > 0);
                var value = string.Join(" / ", valueCells.Distinct());
                var note = noteCol > 0 && noteCol < b.Cells.Count ? b.Cells[noteCol] : "";
                if (item.Key == "process" && NoteHeader.IsMatch(note)) note = "";
                rows.Add(new SheetRow(item.Key, item.Label, value, note, b.UnitIndex));
            }
        }
        return rows.Where(r => r.Value.Length > 0 || r.Note.Length > 0).ToList();
    }
}

/// <summary>사양정리 표 한 줄: 항목, 값(공정 열들을 합침), 기타 사항(당사 메모), 슬라이드 번호</summary>
public sealed record SheetRow(string Key, string Label, string Value, string Note, int Slide);
