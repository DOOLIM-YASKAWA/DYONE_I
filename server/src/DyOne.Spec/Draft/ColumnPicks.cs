using System.Text.RegularExpressions;
using DyOne.Spec.Equipment;
using DyOne.Spec.Extraction;
using DyOne.Spec.Library;

namespace DyOne.Spec.Draft;

/// <summary>
/// 여러 공정 초안(ProcessMatrix, 화성4 신공장)의 공정별 모델 추천: 공정 열마다 조건(공정·외/내판·부위·도료·하전)을 따로 잡아
/// ModelPicker를 돌리고, 스프레이·오프너 로봇·도장기·FGP 자리를 그 열에만 둔다(ModelSlot.Col). 2026-10-28 사용자.
///  - 공정: 열 이름("중도 외판", "상도 베이스 내판 (도어)", "상도 클리어 외판") → 중도·베이스·클리어
///  - 도료·하전: 그 열 도장기 칸("수성외부하전", "유성내부하전"), 없으면 사양서 전체 조건
///  - 오프너: 내판 공정만(도어 MPX2600, 후드·트렁크 MPX3500 — 영업 기준)
///  - 와이핑(비방폭·가반 180kg): 과거 사양정리 와이핑·샌딩·검사 GP180 — 도장기·FGP 자리 없음
/// </summary>
public static class ColumnPicks
{
    public static List<(int col, List<ModelPick> picks)> Pick(SummaryDraft d, IReadOnlyList<Block> blocks, SpecConditions all,
        IReadOnlyList<EquipUnit> units, string? excludeCode, IReadOnlySet<string>? excludeDirs, IReadOnlyList<LibraryDoc>? docs = null)
    {
        var result = new List<(int, List<ModelPick>)>();
        if (!d.ProcessTable) return result;
        var atom = d.Rows.First(r => r.Key == "Atomizer").Split;
        var clean = d.Rows.First(r => r.Key == "Bell Cleaner").Split;
        for (var i = 0; i < d.Processes.Count; i++)
        {
            var name = d.Processes[i].Label;
            if (Regex.IsMatch(name, "와이핑|WIPING", RegexOptions.IgnoreCase))
            {
                result.Add((i, Wiping(docs ?? [])));
                continue;
            }
            var a = i < atom.Count ? atom[i] : "";
            var part = EquipmentUnits.PartOf(name) is { Length: > 0 } p ? p.Split('/')[0] : all.Part;
            var area = Regex.Match(name, "도어|후드|트렁크").Value;
            var c = all with
            {
                Process = EquipmentUnits.ProcessOf(name) is { Length: > 0 } pr ? pr : all.Process,
                Part = part,
                Area = area.Length > 0 ? area : part == "외판" ? "" : all.Area,
                Paint = Regex.IsMatch(a, "수성") ? "수성" : Regex.IsMatch(a, "유성") ? "유성" : all.Paint,
                PaintNote = a.Length > 0 ? $"{name} 도장기 칸: {a}" : all.PaintNote,
                Charge = Regex.IsMatch(a, @"외부\s*하\s*전") ? "외부하전" : Regex.IsMatch(a, @"내부\s*하\s*전") ? "내부하전" : all.Charge,
                // 오프너: 사양서가 나눈 오프너 대수가 있으면 그대로, 내판인데 나눔이 없으면 있다고 본다
                Roles = d.Processes[i].Opener > 0 || (part == "내판" && d.Processes[i].Opener == 0 && !Regex.IsMatch(d.Rows.First(r => r.Key == "Robot").Split.ElementAtOrDefault(i) ?? "", "스프레이 : \\d+ \\(")) ? ["스프레이", "오프너"] : ["스프레이"],
            };
            // 세정장치는 그 공정에 세정장치가 있을 때만(설비사양 표 ○)
            var hasCleaner = i < clean.Count && clean[i].StartsWith(EquipmentRows.CleanerPlaceholder);
            var picks = ModelPicker.Pick(blocks, c, units, excludeCode, excludeDirs)
                .Where(x => x.Kind is "robot" or "atomizer" or "fgp" || (x.Kind == "cleaner" && hasCleaner)).ToList();
            // 후드·트렁크 오프너: 영업 기준 MPX3500이 기본, 무거운 후드·트렁크를 7축 레일로 여는 곳은 핸들링 로봇 MHP45L을 쓴 사례(화성4 신공장 2023)를 참고 후보로
            if (area is "후드" or "트렁크" && picks.FirstOrDefault(x => x.Kind == "robot" && x.Role == "오프너") is { } op
                && op.Options.All(o => !o.Model.Contains("MHP45L")) && HandlingOpener(docs ?? []) is { } mhp)
                op.Options.Add(mhp);
            result.Add((i, picks));
        }
        return result;
    }

    // 과거 사양정리에서 오프너를 MHP45L(핸들링, 가반 45kg)로 쓴 프로젝트(최근 순)
    private static PickOption? HandlingOpener(IReadOnlyList<LibraryDoc> docs)
    {
        var ex = docs.Where(d => d.Rows.Any(r => r.Key == "robot" && r.Value.Contains("MHP45L", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(d => d.Year).Select(Unit).ToList();
        return ex.Count == 0 ? null : new PickOption("MHP45L", ex.Count, ex.Count, ex.Take(6).ToList(),
            $"후드·트렁크 오프너를 핸들링 로봇 MHP45L(가반 45kg)로 쓴 사례 {ex.Count}건 — 화성4 신공장 BASE INNER & H/T(2023)");
    }

    private static EquipUnit Unit(LibraryDoc d) => new($"lib:{d.Id}", d.Year.Length >= 4 ? d.Year[..4] : d.Year, d.ProjectFolder, d.ProjectDir, d.ProjectCode, "",
        "", "", "", "", "", "", "", [], [], [], [], [], "summary", d.Summary?.Path ?? "", d.Summary?.FileName ?? "");

    // 와이핑 로봇: 설비 이력(도장 로봇만 모음)에는 GP 기록이 없어, 과거 사양정리 표에서 와이핑·샌딩·검사 로봇이 GP180인 프로젝트(최근 순)를 근거로
    private static List<ModelPick> Wiping(IReadOnlyList<LibraryDoc> docs)
    {
        var ex = docs.Where(d => d.Rows.Any(r => r.Key == "robot" && r.Value.Contains("GP180", StringComparison.OrdinalIgnoreCase))
                && d.Rows.Any(r => Regex.IsMatch(r.Value, "와이핑|샌딩|검사|WIPING|SANDING", RegexOptions.IgnoreCase)))
            .OrderByDescending(d => d.Year)
            .Select(d => new EquipUnit($"lib:{d.Id}", d.Year.Length >= 4 ? d.Year[..4] : d.Year, d.ProjectFolder, d.ProjectDir, d.ProjectCode, "",
                "", "", "", "", "", "", "", [], [], ["GP180"], [], [], "summary", d.Summary?.Path ?? "", d.Summary?.FileName ?? ""))
            .ToList();
        if (ex.Count == 0) return [];
        var why = "와이핑(비방폭·가반 180kg) — 과거 사양정리 와이핑·샌딩·검사 로봇 GP180";
        return [new ModelPick("robot", "와이핑", "로봇", "GP180", "기본 규칙", why, null, "", [new PickOption("GP180", ex.Count, ex.Count, ex.Take(6).ToList())])];
    }
}
