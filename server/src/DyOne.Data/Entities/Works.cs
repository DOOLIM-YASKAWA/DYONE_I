namespace DyOne.Data.Entities;

// works.* — 그룹웨어 WORKS 앱을 앱 번호 기준으로 옮긴 공통 저장소(첫 대상 281 ES 이슈). DDL: server/db/works_schema.sql
// values · display · layout 등 jsonb 열은 문자열(JSON)로 읽는다.

public class WorksApp
{
    public long AppletId { get; set; }
    public string Name { get; set; } = "";
    public string? Layout { get; set; }
    public string? ViewColumns { get; set; }
    public DateTimeOffset? SyncedAt { get; set; }
}

public class WorksField
{
    public long AppletId { get; set; }
    public string Cid { get; set; } = "";
    public string Label { get; set; } = "";
    public string FieldType { get; set; } = "";
    public string? ValueType { get; set; }
    public bool Required { get; set; }
    public int Seq { get; set; }
    public string? Options { get; set; }
    public string? Props { get; set; }
    public bool Editable { get; set; }
}

public class WorksStatus
{
    public long Id { get; set; }
    public long AppletId { get; set; }
    public string Name { get; set; } = "";
    public int Sort { get; set; }
    public string? Color { get; set; }
    public bool IsEnd { get; set; }
    public bool IsBegin { get; set; }
}

public class WorksTransition
{
    public long Id { get; set; }
    public long AppletId { get; set; }
    public long FromStatus { get; set; }
    public long ToStatus { get; set; }
    public string ActionName { get; set; } = "";
    public int Sort { get; set; }
}

/// <summary>works.doc — DY ONE 값(화면·수정 대상)</summary>
public class WorksDoc
{
    public long Id { get; set; }
    public long AppletId { get; set; }
    public long? GwDocId { get; set; }
    public long? StatusId { get; set; }
    public string Values { get; set; } = "{}";
    public string Display { get; set; } = "{}";
    public string? CreatedByName { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string? UpdatedByName { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? SyncedAt { get; set; }
    public bool IsDeleted { get; set; }
    public bool ManagedInDyone { get; set; }
    public DateTimeOffset? ManagedInDyoneAt { get; set; }
    public string? ManagedInDyoneBy { get; set; }
    public DateTimeOffset? GwDeletedAt { get; set; }
}

/// <summary>works.doc_gw — 마지막으로 읽은 그룹웨어 값(비교용)</summary>
public class WorksDocGw
{
    public long DocId { get; set; }
    public long GwDocId { get; set; }
    public long? StatusId { get; set; }
    public string Values { get; set; } = "{}";
    public string Display { get; set; } = "{}";
    public DateTimeOffset? GwUpdatedAt { get; set; }
    public DateTimeOffset SyncedAt { get; set; }
}

public class WorksFieldChange
{
    public long Id { get; set; }
    public long DocId { get; set; }
    public string Field { get; set; } = "";
    public string? BeforeValue { get; set; }
    public string? AfterValue { get; set; }
    public string? Overwritten { get; set; }
    public string Source { get; set; } = "";
    public string? ByName { get; set; }
    public DateTimeOffset At { get; set; }
}

public class WorksStatusHistory
{
    public long Id { get; set; }
    public long DocId { get; set; }
    public long? FromStatus { get; set; }
    public long ToStatus { get; set; }
    public string? ActionName { get; set; }
    public string? ByName { get; set; }
    public DateTimeOffset At { get; set; }
    public string? Note { get; set; }
}

public class WorksActivity
{
    public long Id { get; set; }
    public long DocId { get; set; }
    public long? GwActivityId { get; set; }
    public string? BodyText { get; set; }
    public string? CreatedByName { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
}

public class WorksComment
{
    public long Id { get; set; }
    public long ActivityId { get; set; }
    public string? Body { get; set; }
    public string? AuthorName { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
}

public class WorksHistory
{
    public long Id { get; set; }
    public long DocId { get; set; }
    public long? GwLogId { get; set; }
    public string Kind { get; set; } = "";
    public string? Title { get; set; }
    public string? Detail { get; set; }
    public string? StatusFrom { get; set; }
    public string? StatusTo { get; set; }
    public string? ActorName { get; set; }
    public DateTimeOffset? At { get; set; }
}

public class WorksFile
{
    public long Id { get; set; }
    public long DocId { get; set; }
    public long? ActivityId { get; set; }
    public string Source { get; set; } = "";
    public string? Name { get; set; }
    public string? Ext { get; set; }
    public long? SizeBytes { get; set; }
    public string? StoredPath { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
}
