using System.Text.Json.Nodes;
using DyOne.Spec.Equipment;

namespace DyOne.Spec.Draft;

/// <summary>
/// 사양정리 초안(영업부 V5 양식): 2장 요약표 16행 + 일정 + 기타 사항·PCS 체크리스트.
/// 값은 이번 고객 사양서에서만 가져온다(Pages = 근거 쪽). 표시: **굵게** {{파랑=사양서 핵심}} [[빨강=선정·확인 필요]].
/// 모델이 정해지지 않은 칸은 Slots로 따로 두고, 과거 프로젝트 모델 후보(Candidates)와 사용자 결정(Decision)을 붙인다.
/// </summary>
public sealed class SummaryDraft
{
    public long FileId { get; set; }
    public string FileName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>ai(사내 AI가 작성) | rule(규칙으로 읽을 수 있는 칸만)</summary>
    public string Engine { get; set; } = "rule";
    public string? EngineNote { get; set; }
    public ProjectContext Context { get; set; } = new("", "", []);
    /// <summary>모델을 정할 때 사양서에서 읽은 조건(공장·공정·외/내판·부위·도료·하전)</summary>
    public SpecConditions? Conditions { get; set; }
    /// <summary>사양서가 공정을 나눈 경우(1공정 프론트 도어 FRT / 2공정 리어 도어 RR)와 공정별 로봇 대수. 하나뿐이면 비어 있음.</summary>
    public List<SpecProcess> Processes { get; set; } = [];
    /// <summary>사양서 공정별 설비사양 표로 열을 나눈 초안(ProcessMatrix) — 여러 공정 프로젝트</summary>
    public bool ProcessTable { get; set; }

    public string Title { get; set; } = "";
    public string Cover { get; set; } = "";
    public string Total { get; set; } = "";
    public string ColHeader { get; set; } = "";
    public List<DraftRow> Rows { get; set; } = [];
    public List<DraftCheck> Etc { get; set; } = [];
    public List<DraftCheck> Pcs { get; set; } = [];
    /// <summary>제공 항목(스페어·특수공구·키락·로봇 커버·교육 등, 사양서 문장 — pptx '제공 항목' 장)</summary>
    public List<DraftCheck> Supply { get; set; } = [];

    /// <summary>사양서 그림을 잘라 붙일 위치(PDF 쪽·영역 pt). AI가 찾았을 때만.</summary>
    public PageClip? LayoutImage { get; set; }
    public PageClip? ScheduleImage { get; set; }
    public string ScheduleNote { get; set; } = "";
    public JsonObject? EduTable { get; set; }
}

public sealed class DraftRow
{
    /// <summary>V5 양식 줄 머리(Robot, Booth size, …)</summary>
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    /// <summary>기타 사항 칸: 영업부 양식처럼 짧은 문구만(예: "Dual Circuit 적용", "설비 보증 : 2년"). 사양서에서 찾은 것만 쓴다.</summary>
    public string Note { get; set; } = "";
    /// <summary>
    /// 참고 사항 칸(화면만, pptx에는 넣지 않음): 사양서 말이 아니라 초안이 덧붙인 확인 안내·계산·기준 설명
    /// (예: "오프너 툴 형식이 사양서에 없음 — 부위 기준", "사이클타임 계산", "PLC 확정 아님 — 확인"). 2026-10-27 사용자.
    /// </summary>
    public string Remark { get; set; } = "";
    /// <summary>표에서 뺀 줄(화면 맨 아래 '뺀 칸'에서 되살림). pptx에는 넣지 않고, 초안을 다시 만들어도 뺀 채로 둔다.</summary>
    public bool Hidden { get; set; }
    /// <summary>여러 공정 초안에서 사용자가 공정 칸을 한 칸으로 합친 줄(화면 '칸 합치기') — 다시 만들어도 합친 채로</summary>
    public bool Merged { get; set; }
    /// <summary>공정 칸 병합(스프레드시트처럼): Split[i] 칸이 오른쪽으로 Span[i]칸을 차지, 덮인 칸은 0. 비어 있으면 모두 1</summary>
    public List<int> Span { get; set; } = [];
    /// <summary>사용자가 표에 추가한 줄(행 추가) — 초안을 다시 만들어도 남는다, 행 삭제로 지운다</summary>
    public bool Custom { get; set; }
    public void AddRemark(string text)
    {
        if (text.Length == 0 || Remark.Split('\n').Contains(text)) return;
        Remark = Remark.Length == 0 ? text : $"{Remark}\n{text}";
    }
    /// <summary>값·기타 사항의 사양서 원문 근거 줄("… (p2)"). 표 칸에는 쓰지 않고 화면의 '원문'·pptx 슬라이드 노트로만.</summary>
    public List<string> Source { get; set; } = [];
    /// <summary>근거 쪽 번호(사양서)</summary>
    public List<int> Pages { get; set; } = [];
    /// <summary>
    /// 공정별 값(SummaryDraft.Processes 순서, 예: FRT·RR). 공정마다 값이 다를 때만 채운다(예: Robot 대수).
    /// 비어 있으면 Value가 모든 공정에 같은 값 — 표에서 한 칸으로 합친다(영업부 양식).
    /// </summary>
    public List<string> Split { get; set; } = [];
    public List<ModelSlot> Slots { get; set; } = [];
    /// <summary>사용자가 표에서 직접 고친 줄(값·기타 사항). 초안을 다시 만들어도 이 줄은 고친 글을 그대로 둔다.</summary>
    public bool Edited { get; set; }
    public string? EditedBy { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
}

public sealed record DraftCheck(string Text, List<int> Pages);

public sealed record PageClip(int Page, double[] Clip);

/// <summary>
/// 모델 자리 하나(Robot 칸의 '스프레이 로봇'·'오프너 로봇', 도장기, FGP). ModelPicker가 정한다.
/// Status: given = 사양서에 모델이 적힘 / existing = 사양서가 '기설치와 동일'을 요구 / unspecified = 사양서에 모델이 없음.
/// How: 사양서 지정 | 기설치 이력 | 공장 이력 | 경향 규칙 | 선정 필요, Why: 고른 이유. Candidates[0] = 고른 모델(제안), 나머지 = 다른 후보.
/// </summary>
public sealed class ModelSlot
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";      // robot | atomizer | fgp | ccv
    public string Role { get; set; } = "";      // 스프레이 | 오프너 (로봇만)
    public string Label { get; set; } = "";     // 화면·pptx 표시용 이름
    /// <summary>여러 공정 초안에서 이 자리가 속한 공정 열(SummaryDraft.Processes 순서). null이면 줄 전체(공정이 하나)</summary>
    public int? Col { get; set; }
    /// <summary>여러 공정 초안에서 도장기·FGP 칸에 함께 적을 수량(그 공정 스프레이 로봇 수, 영업부 "PPH707 EXT : 16")</summary>
    public int? Qty { get; set; }
    public string Status { get; set; } = "unspecified";
    public string How { get; set; } = "";
    public string Why { get; set; } = "";
    /// <summary>사양서에 적힌 모델(Status = given)</summary>
    public string SpecModel { get; set; } = "";
    /// <summary>사양서 원문 문구(기설치와 동일 …·모델이 적힌 줄)와 쪽</summary>
    public string SpecText { get; set; } = "";
    public int? SpecPage { get; set; }
    public List<SlotCandidate> Candidates { get; set; } = [];
    public SlotDecision? Decision { get; set; }
}

/// <summary>모델 후보: 대표 사례(RecordId = 설비 이력 공정 단위 id, 원본 파일 열기용) + 같은 묶음 건수(Excerpt) + 다른 사례(Context)</summary>
public sealed record SlotCandidate(
    string RecordId, string Model, string Role, string Year, string ProjectFolder, string? ProjectCode,
    string SourceKind, string FileName, string Excerpt, string Context, List<string> Reasons);

/// <summary>
/// 사용자 결정: candidate(후보 채택) | custom(직접 입력) | none(선정 필요로 둠).
/// Applied = 채택·입력한 모델을 칸 값에 넣은 글(DraftEdit). 결정을 바꾸거나 취소하면 이 글을 칸에서 뺀다.
/// </summary>
public sealed record SlotDecision(string Kind, string? RecordId, string? Text, string By, DateTimeOffset At, string? Applied = null);

public static class DraftLayout
{
    /// <summary>V5 양식 2장 표의 행(순서 고정). Others는 영업부 사양정리처럼 표 맨 아래(템플릿에 없으면 렌더러가 줄을 붙인다)</summary>
    public static readonly string[] RowKeys =
    [
        "Robot", "Booth size", "Basic Condition", "Body Size", "Utility", "Bell Cleaner", "Atomizer", "Paint",
        "Color(CCV)", "FGP", "Panel", "System", "Training", "Schedule", "Stand by", "Others",
    ];

    public static string KindLabel(string kind, string role) => kind switch
    {
        "robot" => role.Length > 0 ? $"{role} 로봇" : "로봇",
        "atomizer" => "도장기",
        "fgp" => "FGP",
        "ccv" => "CCV",
        "cleaner" => "벨컵 세정장치",
        _ => kind,
    };

    /// <summary>사양서에 있을 때만 붙이는 줄(고정방식·Tracking rail·오프너 클리너·Vision & MIP·에어 제습기) — Others 바로 앞에 넣는다</summary>
    public static DraftRow EnsureRow(SummaryDraft d, string key)
    {
        if (d.Rows.FirstOrDefault(r => r.Key == key) is { } have) return have;
        var row = new DraftRow { Key = key };
        var at = d.Rows.FindIndex(r => r.Key == "Others");
        d.Rows.Insert(at < 0 ? d.Rows.Count : at, row);
        return row;
    }

    public static string RowOf(string kind) => kind switch
    {
        "robot" => "Robot",
        "atomizer" => "Atomizer",
        "fgp" => "FGP",
        "ccv" => "Color(CCV)",
        "cleaner" => "Bell Cleaner",
        _ => "Robot",
    };

    /// <summary>ModelPicker 후보 → 표시용. 대표 사례는 최근 것, 나머지 사례는 Context에 줄로.</summary>
    public static SlotCandidate ToCandidate(PickOption o, string how, string why, bool first)
    {
        var u = o.Examples[0];
        return new SlotCandidate(u.Id, o.Model, "", u.Year, u.ProjectFolder + (u.Unit.Length > 0 ? " · " + System.Text.RegularExpressions.Regex.Replace(u.Unit, @"\s+", " ") : ""),
            u.ProjectCode, u.SourceKind, u.FileName, o.Total > 1 ? $"{o.Count}/{o.Total}건" : "",
            string.Join("\n", o.Examples.Skip(1).Select(e => e.Label)),
            first ? [how, why] : o.Note is { Length: > 0 } note ? [note] : [o.Total > 1 ? $"같은 묶음 {o.Count}/{o.Total}건" : "다른 후보"]);
    }
}
