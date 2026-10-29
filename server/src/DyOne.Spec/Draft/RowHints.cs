using DyOne.Spec.Equipment;
using DyOne.Spec.Library;

namespace DyOne.Spec.Draft;

/// <summary>
/// 사양서에서 찾지 못한 칸에 붙이는 '과거 사양정리 참고'. 값을 만들지 않고, 과거 프로젝트 당사 사양정리 표의 같은 칸을 그대로 보여 준다.
///  - 대상 칸: 프로젝트마다 비슷하게 쓰는 칸(Panel·System·Training·Stand by, Utility는 같은 공장일 때만).
///    부스 크기·Basic Condition·차종·일정처럼 프로젝트 사실값과, 사양서에 없으면 넣지 않는 Bell Cleaner는 붙이지 않는다.
///  - 순서: 같은 공장·같은 공정 → 같은 공정·같은 외/내판 → 같은 공장 → 2022년 이후 최근. 같은 단계는 최근 순, 값이 같은 것은 한 번만.
///  - 이번 프로젝트 자신(번호·같은 사양서가 있는 폴더)과 범퍼·부품 업체는 뺀다. 근거 파일은 화면에서 바로 연다(lib:{라이브러리 id}).
/// </summary>
public static class RowHints
{
    private static readonly Dictionary<string, (string item, bool samePlantOnly)> Rows = new()
    {
        ["Panel"] = ("panel", false), ["System"] = ("system", false),
        ["Training"] = ("training", false), ["Stand by"] = ("standby", false), ["Utility"] = ("utility", true),
    };

    public static void Apply(SummaryDraft d, IReadOnlyList<LibraryDoc> docs, SpecConditions c, string? excludeCode,
        IReadOnlySet<string>? excludeDirs, SummaryDraft? previous = null)
    {
        var oldSlots = previous?.Rows.SelectMany(r => r.Slots).Where(s => s.Kind == "row" && s.Decision is not null).ToDictionary(s => s.Id) ?? [];
        var part = c.Part.Split('/')[0];
        var usable = docs.Where(doc => doc.HasSheet && doc.Summary is not null
            && !(excludeDirs?.Contains(doc.ProjectDir) ?? false)
            && !(excludeCode is not null && (string.Equals(doc.ProjectCode, excludeCode, StringComparison.OrdinalIgnoreCase) || doc.ProjectFolder.Contains(excludeCode, StringComparison.OrdinalIgnoreCase)))
            && !EquipmentUnits.IsPartsMaker(doc.ProjectFolder)).ToList();

        foreach (var row in d.Rows.Where(r => r.Value.Length == 0 && Rows.ContainsKey(r.Key)))
        {
            var (item, samePlantOnly) = Rows[row.Key];
            var cand = new List<(LibraryDoc doc, SheetRow sheet, int tier, List<string> reasons)>();
            foreach (var doc in usable)
            {
                if (doc.Rows.FirstOrDefault(x => x.Key == item && x.Value.Length > 0) is not { } sheet) continue;
                var head = $"{doc.CustomerFolder} {doc.ProjectFolder}";
                var site = EquipmentUnits.SiteOf(head);
                var proc = EquipmentUnits.ProcessOf(doc.ProjectFolder) is { Length: > 0 } p ? p
                    : EquipmentUnits.ProcessOf(doc.Rows.FirstOrDefault(x => x.Key == "process")?.Value ?? "");
                var docPart = EquipmentUnits.PartOf(doc.ProjectFolder);
                bool samePlant = c.Plant.Length > 0 && site == c.Plant, sameProc = c.Process.Length > 0 && proc == c.Process,
                    samePart = part.Length > 0 && docPart.Contains(part);
                var year = int.TryParse(doc.Year.Length >= 4 ? doc.Year[..4] : doc.Year, out var y) ? y : 0;
                int tier = samePlant && sameProc ? 1 : sameProc && samePart ? 2 : samePlant ? 3 : year >= 2022 ? 4 : 9;
                if (tier == 9 || (samePlantOnly && !samePlant)) continue;
                var reasons = new List<string>();
                if (samePlant) reasons.Add($"같은 공장({site})");
                if (sameProc) reasons.Add($"같은 공정({proc})");
                if (samePart) reasons.Add($"같은 {part}");
                if (tier == 4) reasons.Add("2022년 이후 최근");
                reasons.Add("당사 사양정리");
                cand.Add((doc, sheet, tier, reasons));
            }
            var picked = cand.OrderBy(x => x.tier).ThenByDescending(x => x.doc.Year)
                .DistinctBy(x => Norm(x.sheet.Value)).Take(3).ToList();
            if (picked.Count == 0) continue;

            var slot = new ModelSlot
            {
                Id = $"row:{row.Key}",
                Kind = "row",
                Label = row.Key,
                Status = "unspecified",
                How = "과거 사양정리 참고",
                Why = $"사양서에 없음 → {string.Join(", ", picked[0].reasons)} 프로젝트의 같은 칸",
                Candidates = picked.Select((x, i) => new SlotCandidate(
                    $"lib:{x.doc.Id}", Short(x.sheet.Value), "", x.doc.Year.Length >= 4 ? x.doc.Year[..4] : x.doc.Year, x.doc.ProjectFolder, x.doc.ProjectCode,
                    "summary", x.doc.Summary!.FileName, x.sheet.Note.Length > 0 ? Short(x.sheet.Note) : "", "",
                    i == 0 ? ["과거 사양정리 참고", string.Join(", ", x.reasons)] : x.reasons)).ToList(),
            };
            if (oldSlots.TryGetValue(slot.Id, out var old)) slot.Decision = DraftBuilder.KeepDecision(old, slot);
            row.Slots.Add(slot);
        }
    }

    private static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());
    private static string Short(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length <= 90 ? s : s[..90] + "…";
    }
}
