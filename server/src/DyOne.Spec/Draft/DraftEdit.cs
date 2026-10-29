using System.Text.RegularExpressions;

namespace DyOne.Spec.Draft;

/// <summary>
/// 표에서 직접 고치기 + 채택한 모델을 칸 값에 넣기.
///  - 로봇: 역할 뒤에 모델 — "스프레이 로봇 * 2ea" → "스프레이 로봇 MPX3500 * 2ea"(공정별 칸 모두)
///  - 그 밖(도장기·FGP·세정장치·과거 사양정리 참고): 값 끝에 " / 모델", 값이 비었으면 모델만
/// 넣은 글은 SlotDecision.Applied에 남겨, 다른 후보로 바꾸거나 취소하면 그 글만 칸에서 뺀다(사용자가 고친 다른 글은 그대로).
/// </summary>
public static class DraftEdit
{
    public static DraftRow? RowOf(SummaryDraft d, ModelSlot slot) => d.Rows.FirstOrDefault(r => r.Slots.Contains(slot));

    /// <summary>결정에 따라 칸 값을 바꾸고 새 결정(Applied 포함)을 돌려준다. model이 비면 넣지 않는다(선정 필요·취소).</summary>
    public static SlotDecision? Decide(DraftRow row, ModelSlot slot, SlotDecision? decision, string? model)
    {
        if (slot.Decision?.Applied is { Length: > 0 } old) Remove(row, slot, old);
        if (decision is null) return null;
        if (string.IsNullOrWhiteSpace(model)) return decision with { Applied = null };
        return decision with { Applied = Insert(row, slot, model.Trim()) ? model.Trim() : null };
    }

    /// <summary>초안을 다시 만들 때: 사용자가 고친 줄은 고친 글로, 그 밖의 줄은 남아 있는 채택 모델을 다시 넣는다.</summary>
    public static void Restore(SummaryDraft d, SummaryDraft? previous)
    {
        // 사용자가 추가한 줄: 이전 초안에서 바로 앞 줄 뒤에 그대로
        if (previous is not null)
            for (var k = 0; k < previous.Rows.Count; k++)
                if (previous.Rows[k].Custom && d.Rows.All(r => r.Key != previous.Rows[k].Key))
                {
                    var after = k > 0 ? d.Rows.FindIndex(r => r.Key == previous.Rows[k - 1].Key) : -1;
                    d.Rows.Insert(after + 1, previous.Rows[k]);
                }
        if (previous is not null)
            foreach (var old in previous.Rows.Where(r => r.Hidden || r.Merged))
                if (d.Rows.FirstOrDefault(r => r.Key == old.Key) is { } kept)
                {
                    kept.Hidden |= old.Hidden;
                    if (old.Merged && kept.Split.Count > 0) { kept.Value = string.Join(" / ", kept.Split.Where(x => x.Length > 0 && x != "-").Distinct()); kept.Split = []; kept.Merged = true; }
                }
        if (previous is not null)
            foreach (var old in previous.Rows.Where(r => r.Edited))
                if (d.Rows.FirstOrDefault(r => r.Key == old.Key) is { } row)
                {
                    row.Value = old.Value; row.Split = old.Split; row.Note = old.Note; row.Span = old.Span;
                    row.Edited = true; row.EditedBy = old.EditedBy; row.EditedAt = old.EditedAt;
                }
        foreach (var row in d.Rows.Where(r => !r.Edited))
            foreach (var slot in row.Slots)
            {
                // 이 기능 전에 한 채택(Applied 없음)도 채택한 모델을 칸에 넣는다
                var model = slot.Decision switch
                {
                    { Applied: { Length: > 0 } a } => a,
                    { Kind: "candidate" } dec => slot.Candidates.FirstOrDefault(c => c.RecordId == dec.RecordId)?.Model,
                    { Kind: "custom" } dec => dec.Text,
                    _ => null,
                };
                if (string.IsNullOrWhiteSpace(model)) continue;
                slot.Decision = slot.Decision! with { Applied = Insert(row, slot, model) ? model : null };
            }
    }

    /// <summary>
    /// 기본 사양: 아직 정하지 않은 자리(결정 없음, 사양서 지정 아님)는 1순위 후보를 칸에 넣어 둔다(결정 종류 default).
    /// 사람이 채택·직접 입력·선정 필요로 둔 자리와 직접 고친 줄은 건드리지 않는다. 화면의 '참고'에서 다른 후보로 바꿀 수 있다.
    /// </summary>
    public static void ApplyDefaults(SummaryDraft d)
    {
        foreach (var row in d.Rows.Where(r => !r.Edited))
            foreach (var slot in row.Slots)
                ApplyDefault(row, slot);
    }

    public static void ApplyDefault(DraftRow row, ModelSlot slot)
    {
        if (slot.Decision is not null || slot.Status == "given" || slot.Candidates.FirstOrDefault() is not { } first) return;
        slot.Decision = Decide(row, slot, new SlotDecision("default", first.RecordId, null, "기본 사양", DateTimeOffset.Now), first.Model);
    }

    private static readonly Regex AtomParts = new(@"^(스프레이:\s*)?(.*?)(\s*/\s*오프너:.*)?$", RegexOptions.Singleline);

    private static Regex RobotRole(ModelSlot slot) => new($@"({Regex.Escape(slot.Role)}\s*로봇)");

    // 공정 열 칸의 로봇 모델 머리: "스프레이 MPX3600 / 오프너 MPX2600 : 로봇 7대 × 2라인 (…)"
    private static readonly Regex ColHead = new(@"^((?:스프레이|오프너|와이핑)\s+[^:]+?)\s:\s(.*)$", RegexOptions.Singleline);

    /// <summary>여러 공정 초안: 모델을 그 공정 열 칸(Split[Col])에만 넣는다. 로봇은 칸 앞 머리, 도장기·FGP는 "모델 (칸 내용)".</summary>
    private static bool InsertCol(DraftRow row, ModelSlot slot, int col, string model)
    {
        if (col >= row.Split.Count) return false;
        var cell = row.Split[col];
        var empty = cell.Trim() is "" or "-";
        if (slot.Kind == "robot")
        {
            var item = $"{slot.Role} {model}";
            // 용도별 대수 칸("스프레이 : 8 / 오프너 : 6 (…)")이면 그 용도 자리에 모델: "스프레이 MPX3600 : 8"
            var roleAt = Regex.Match(cell, $@"(?<![가-힣]){Regex.Escape(slot.Role)}\s:\s");
            if (roleAt.Success)
            {
                cell = cell[..roleAt.Index] + $"{slot.Role} {model} : " + cell[(roleAt.Index + roleAt.Length)..];
                SetCol(row, col, cell);
                return true;
            }
            if (cell.Contains($"{item} : ")) return false;
            if (ColHead.Match(cell) is { Success: true } m)
            {
                if (m.Groups[1].Value.Split(" / ").Contains(item)) return false;
                cell = $"{m.Groups[1].Value} / {item} : {m.Groups[2].Value}";
            }
            else cell = empty ? item : $"{item} : {cell}";
        }
        else
        {
            if (slot.Kind == "cleaner" && cell.StartsWith(EquipmentRows.CleanerPlaceholder))
            {
                SetCol(row, col, EquipmentRows.CleanerLabel(model) + cell[EquipmentRows.CleanerPlaceholder.Length..]);
                return true;
            }
            if (cell.Contains(model, StringComparison.OrdinalIgnoreCase)) return false;
            var q = slot.Qty is > 0 ? $" : {slot.Qty}" : "";
            cell = empty ? model + q : $"{model}{q} ({cell})";
        }
        SetCol(row, col, cell);
        return true;
    }

    private static void RemoveCol(DraftRow row, ModelSlot slot, int col, string model)
    {
        if (col >= row.Split.Count) return;
        var cell = row.Split[col];
        if (slot.Kind == "robot")
        {
            var item = $"{slot.Role} {model}";
            if (cell.Contains($"{item} : ") && !ColHead.Match(cell).Groups[1].Value.Split(" / ").Contains(item))
            {
                var at = cell.IndexOf($"{item} : ", StringComparison.Ordinal);
                cell = cell[..at] + $"{slot.Role} : " + cell[(at + item.Length + 3)..];
                SetCol(row, col, cell);
                return;
            }
            if (ColHead.Match(cell) is { Success: true } m)
            {
                var rest = m.Groups[1].Value.Split(" / ").Where(x => x != item).ToList();
                cell = rest.Count > 0 ? $"{string.Join(" / ", rest)} : {m.Groups[2].Value}" : m.Groups[2].Value;
            }
            else if (cell == item) cell = "-";
        }
        else if (slot.Kind == "cleaner" && EquipmentRows.CleanerLabel(model) is var cl && cell.StartsWith(cl)) cell = EquipmentRows.CleanerPlaceholder + cell[cl.Length..];
        else if (Regex.Match(cell, $@"^{Regex.Escape(model)}(?: : \d+)? \((.*)\)$", RegexOptions.Singleline) is { Success: true } am) cell = am.Groups[1].Value;
        else if (Regex.IsMatch(cell, $@"^{Regex.Escape(model)}(?: : \d+)?$")) cell = "-";
        SetCol(row, col, cell);
    }

    // 공정 칸을 바꾸면 값 칸의 그 공정 줄("**중도 외판** : …")도 같이 바꾼다
    private static void SetCol(DraftRow row, int col, string cell)
    {
        row.Split[col] = cell;
        var lines = row.Value.Split('\n');
        if (lines.Length == row.Split.Count && lines[col].IndexOf("** : ", StringComparison.Ordinal) is var k and > 0)
        {
            lines[col] = lines[col][..(k + 5)] + cell;
            row.Value = string.Join("\n", lines);
        }
    }

    private static bool Insert(DraftRow row, ModelSlot slot, string model)
    {
        if (slot.Col is int col) return InsertCol(row, slot, col, model);
        if (slot.Kind == "robot" && slot.Role.Length > 0)
        {
            var role = RobotRole(slot);
            var hit = false;
            string Put(string s)
            {
                if (!role.IsMatch(s) || Regex.IsMatch(s, $@"{Regex.Escape(slot.Role)}\s*로봇\s+{Regex.Escape(model)}")) return s;
                hit = true;
                return role.Replace(s, $"$1 {model}", 1);
            }
            row.Value = Put(row.Value);
            row.Split = row.Split.Select(Put).ToList();
            if (hit) return true;
        }
        // 세정장치: "세정장치 : 4 set" → "크리스탈 CC-3000 : 4 set"(영업부 표기, 넣은 글은 Applied에 표기 그대로 남는다)
        if (slot.Kind == "cleaner" && row.Value.StartsWith(EquipmentRows.CleanerPlaceholder))
        {
            row.Value = EquipmentRows.CleanerLabel(model) + row.Value[EquipmentRows.CleanerPlaceholder.Length..];
            return true;
        }
        if (Plain(row.Value).Contains(model, StringComparison.OrdinalIgnoreCase)) return false; // 이미 칸에 있음
        // 도장기: 영업부 표기처럼 모델을 앞에 "PPH707 SB (정전벨 · 내부하전 · 유성)"
        // "스프레이: 정전벨 · … / 오프너: 그리퍼 타입"이면 스프레이 쪽에만 "스프레이: PPH707 SB (정전벨 · …) / 오프너: …"
        if (slot.Kind == "atomizer" && row.Value.Length > 0 && row.Split.Count == 0)
        {
            var m = AtomParts.Match(row.Value);
            row.Value = m.Groups[2].Length > 0 ? $"{m.Groups[1].Value}{model} ({m.Groups[2].Value}){m.Groups[3].Value}" : $"{m.Groups[1].Value}{model}{m.Groups[3].Value}";
            return true;
        }
        row.Value = row.Value.Length == 0 ? model : $"{row.Value} / {model}";
        if (row.Split.Count > 0) row.Split = row.Split.Select(s => s.Length == 0 ? model : $"{s} / {model}").ToList();
        return true;
    }

    private static void Remove(DraftRow row, ModelSlot slot, string model)
    {
        if (slot.Col is int col) { RemoveCol(row, slot, col, model); return; }
        if (slot.Kind == "cleaner" && EquipmentRows.CleanerLabel(model) is var label && row.Value.StartsWith(label))
        {
            row.Value = EquipmentRows.CleanerPlaceholder + row.Value[label.Length..];
            return;
        }
        var esc = Regex.Escape(model);
        string Take(string s)
        {
            if (slot.Kind == "robot" && slot.Role.Length > 0)
                s = Regex.Replace(s, $@"({Regex.Escape(slot.Role)}\s*로봇)\s+{esc}", "$1");
            if (slot.Kind == "atomizer" && Regex.Match(s, $@"^(스프레이:\s*)?{esc}(?: \((.*?)\))?(\s*/\s*오프너:.*)?$", RegexOptions.Singleline) is { Success: true } am)
                return am.Groups[1].Value + am.Groups[2].Value + am.Groups[3].Value;
            s = Regex.Replace(s, $@"\s*/\s*{esc}(?=\s*(?:/|$))", "");
            return s.Trim() == model ? "" : s;
        }
        row.Value = Take(row.Value);
        row.Split = row.Split.Select(Take).ToList();
    }

    /// <summary>표에서 고친 글 저장. field: value | note | split(index = 공정 순서) | reset(직접 고침 풀기)</summary>
    public static bool SetCell(DraftRow row, string field, int? index, string text, string by)
    {
        text = text.Replace("\r\n", "\n").Trim();
        switch (field)
        {
            case "value": row.Value = text; break;
            case "note": row.Note = text; break;
            // 참고 사항은 화면 메모라 고쳐도 '직접 고침'(값·기타 사항 고정)으로 보지 않는다
            case "remark": row.Remark = text; return true;
            // 줄 빼기·되살리기: 표·pptx에서 빼기만 하고 값은 그대로 둔다(직접 고침 아님)
            case "hide": row.Hidden = true; return true;
            case "show": row.Hidden = false; return true;
            // 되돌리기: '직접 고침'을 풀어 초안을 다시 만들 때 사양서 값으로 돌아가게 한다
            case "reset":
                row.Edited = false; row.EditedBy = null; row.EditedAt = null;
                row.Merged = false; row.Span = []; // 줄 합치기·셀 병합도 풀어 사양서 값으로
                return true;
            case "split" when index is int i && i >= 0 && i < row.Split.Count:
                row.Split[i] = text;
                break;
            // 엑셀처럼 칸 합치기·나누기(여러 공정): 합치면 공정 칸 글을 한 칸으로(같으면 하나만), 나누면 한 칸 글을 공정 수(index)만큼
            case "merge" when row.Split.Count > 0:
                row.Value = string.Join(" / ", row.Split.Where(x => x.Length > 0 && x != "-").Distinct());
                row.Split = [];
                row.Span = [];
                row.Merged = true;
                break; // '직접 고침'으로 남겨 '되돌리기'로 사양서 칸 나눔으로 돌아갈 수 있게
            // 줄 전체 칸 나누기: 글은 첫 칸에만(엑셀 병합 해제처럼), 나머지 칸은 비운다
            case "unmerge" when index is int n && n >= 2:
                row.Split = [row.Value, .. Enumerable.Repeat("", n - 1)];
                row.Span = [];
                row.Merged = false;
                break;
            // 셀 병합: index 칸을 오른쪽 이웃 칸(묶음)과 합친다 — 글은 둘 다 있으면 " / "로
            case "merge-right" when index is int i && i >= 0 && i < row.Split.Count:
            {
                var span = Spans(row);
                var next = i + span[i];
                if (next >= row.Split.Count) return false;
                var texts = new[] { row.Split[i], row.Split[next] }.Where(x => x.Trim().Length > 0 && x != "-").Distinct().ToList();
                row.Split[i] = string.Join(" / ", texts);
                row.Split[next] = "";
                span[i] += span[next];
                span[next] = 0;
                row.Span = span;
                break;
            }
            // 범위 병합(스프레드시트에서 여러 칸 고르고 병합): index 칸부터 text(끝 칸 번호)까지 한 칸으로
            case "merge-range" when index is int a && int.TryParse(text, out var end) && a >= 0 && end > a && end < row.Split.Count:
            {
                var span = Spans(row);
                var texts = new List<string>();
                for (var j = a; j <= end; j++)
                    if (span[j] > 0 && row.Split[j].Trim().Length > 0 && row.Split[j] != "-" && !texts.Contains(row.Split[j])) texts.Add(row.Split[j]);
                // 끝 칸이 다른 병합 안이면 그 병합 끝까지
                var last = end;
                for (var j = a; j <= end; j++) if (span[j] > 1) last = Math.Max(last, j + span[j] - 1);
                for (var j = a + 1; j <= last; j++) { span[j] = 0; row.Split[j] = ""; }
                span[a] = last - a + 1;
                row.Split[a] = string.Join(" / ", texts);
                row.Span = span;
                break;
            }
            // 셀 나누기: 합친 칸을 하나씩으로 — 글은 첫 칸에만
            case "unmerge-cell" when index is int i2 && i2 >= 0 && i2 < row.Split.Count:
            {
                var span = Spans(row);
                for (var j = i2 + 1; j < i2 + span[i2] && j < span.Count; j++) span[j] = 1;
                span[i2] = 1;
                row.Span = span;
                break;
            }
            default: return false;
        }
        row.Edited = true; row.EditedBy = by; row.EditedAt = DateTimeOffset.Now;
        return true;
    }

    /// <summary>칸별 병합 폭(없으면 모두 1)</summary>
    public static List<int> Spans(DraftRow row) =>
        row.Span.Count == row.Split.Count ? [.. row.Span] : [.. Enumerable.Repeat(1, row.Split.Count)];

    private static string Plain(string s) => Regex.Replace(s, @"\*\*|\{\{|\}\}|\[\[|\]\]|<<|>>", "");
}
