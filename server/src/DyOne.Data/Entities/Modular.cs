namespace DyOne.Data.Entities;

// modular.* — 모듈라빌드 프로젝트 정보 (그룹웨어 WORKS applet 376 이관). DDL: server/db/modular_schema.sql
// 그룹웨어 미러 행(gw_doc_id 있음)은 DyOne.Worker GwModularSyncJob이 쓰고, API는 읽기만 한다.

/// <summary>modular.option — 선택지 (list_key + 그룹웨어 선택지 코드)</summary>
public class ModularOption
{
    public string ListKey { get; set; } = "";
    public int Code { get; set; }
    public string Label { get; set; } = "";
    public int Sort { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>modular.status — 진행 상태 (그룹웨어 status id 그대로)</summary>
public class ModularStatus
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int Sort { get; set; }
    public string? Color { get; set; }
    public bool IsEnd { get; set; }
    public bool IsBegin { get; set; }
}

/// <summary>modular.transition — 상태 전환 버튼</summary>
public class ModularTransition
{
    public long Id { get; set; }
    public long FromStatus { get; set; }
    public long ToStatus { get; set; }
    public string ActionName { get; set; } = "";
    public int Sort { get; set; }
}

/// <summary>modular.build — 그룹웨어 문서 1건 = 1행</summary>
public class ModularBuild
{
    public long Id { get; set; }
    public long? GwDocId { get; set; }
    public int? Year { get; set; }
    public string ProjectCode { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public long? StatusId { get; set; }
    public DateOnly? InstallStart { get; set; }
    public string? InstallStartRaw { get; set; }
    public DateOnly? CommissioningStart { get; set; }
    public string? CommissioningStartRaw { get; set; }
    public string? ExternalInspection { get; set; }
    public DateOnly? ShipDue { get; set; }
    public string? ShipMonth { get; set; }
    public string? OriginToolReceipt { get; set; }
    public string? Note { get; set; }
    public string? Customer { get; set; }
    public string? Plant { get; set; }
    public string? Process { get; set; }
    public string? ProcessDetail { get; set; }
    public int? RobotCount { get; set; }
    public string? RobotCountRaw { get; set; }
    public int? CategoryCode { get; set; }
    public int? RegionCode { get; set; }
    public string? MainIssue { get; set; }
    public int AttachmentCount { get; set; }
    public string? CreatedByName { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string? UpdatedByName { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? SyncedAt { get; set; }
    public bool IsDeleted { get; set; }
    /// <summary>true면 그룹웨어 변경을 더 이상 반영하지 않는다(DY ONE 관리 전환).</summary>
    public bool ManagedInDyone { get; set; }
    public DateTimeOffset? ManagedInDyoneAt { get; set; }
    public string? ManagedInDyoneBy { get; set; }
    /// <summary>그룹웨어에서 문서가 지워진 시각. DY ONE에서는 지우지 않고 표시만 한다.</summary>
    public DateTimeOffset? GwDeletedAt { get; set; }
}

/// <summary>modular.build_gw — 마지막으로 읽은 그룹웨어 값(비교용). Values 키는 ModularFieldSet.Fields</summary>
public class ModularBuildGw
{
    public long BuildId { get; set; }
    public long GwDocId { get; set; }
    public string Values { get; set; } = "{}";
    public DateTimeOffset? GwUpdatedAt { get; set; }
    public DateTimeOffset SyncedAt { get; set; }
}

/// <summary>modular.field_change — 칸별 변경 기록(source = groupware · dyone)</summary>
public class ModularFieldChange
{
    public long Id { get; set; }
    public long BuildId { get; set; }
    public string Field { get; set; } = "";
    public string? BeforeValue { get; set; }
    public string? AfterValue { get; set; }
    /// <summary>그룹웨어 변경을 반영하면서 덮인 DY ONE 값</summary>
    public string? Overwritten { get; set; }
    public string Source { get; set; } = "";
    public string? ByName { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>modular.build_option — 복수 선택 값</summary>
public class ModularBuildOption
{
    public long BuildId { get; set; }
    public string ListKey { get; set; } = "";
    public int Code { get; set; }
}

/// <summary>modular.build_member — 담당자(pm · pl · control · design · teach)</summary>
public class ModularBuildMember
{
    public long BuildId { get; set; }
    public string Role { get; set; } = "";
    public string NameRaw { get; set; } = "";
    public long? EmployeeId { get; set; }
}

/// <summary>modular.activity — 활동 글</summary>
public class ModularActivity
{
    public long Id { get; set; }
    public long BuildId { get; set; }
    public long? GwActivityId { get; set; }
    public string? BodyText { get; set; }
    public int CommentCount { get; set; }
    public int AttachCount { get; set; }
    public string? CreatedByName { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
}

/// <summary>modular.status_history — DY ONE에서 한 상태 전환 기록</summary>
public class ModularStatusHistory
{
    public long Id { get; set; }
    public long BuildId { get; set; }
    public long? FromStatus { get; set; }
    public long ToStatus { get; set; }
    public string? ActionName { get; set; }
    public string? ByName { get; set; }
    public DateTimeOffset At { get; set; }
    public string? Note { get; set; }
}

/// <summary>modular.history — 그룹웨어 변경 이력(go_action_logs)을 해석한 것</summary>
public class ModularHistory
{
    public long Id { get; set; }
    public long BuildId { get; set; }
    public long? GwLogId { get; set; }
    public string Kind { get; set; } = "";
    public string? Title { get; set; }
    public string? Detail { get; set; }
    public string? StatusFrom { get; set; }
    public string? StatusTo { get; set; }
    public string? ActorName { get; set; }
    public DateTimeOffset? At { get; set; }
}

/// <summary>modular.comment — 활동 글 댓글</summary>
public class ModularComment
{
    public long Id { get; set; }
    public long ActivityId { get; set; }
    public string? Body { get; set; }
    public string? AuthorName { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
}

/// <summary>modular.file — 첨부 파일 정보. 본체는 그룹웨어 서버(gw_path)에 있고, 복사하면 stored_path가 채워진다.</summary>
public class ModularFile
{
    public long Id { get; set; }
    public long BuildId { get; set; }
    public long? ActivityId { get; set; }
    public string Source { get; set; } = "";
    public string? Name { get; set; }
    public string? Ext { get; set; }
    public long? SizeBytes { get; set; }
    public string? StoredPath { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
}
