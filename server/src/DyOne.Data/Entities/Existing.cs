namespace DyOne.Data.Entities;

// 기존 dyone DB(PostgreSQL 18)의 테이블을 그대로 매핑한다. DY ONE 내부용은 이 테이블들을 읽기만 한다.
// 컬럼 의미는 각 테이블의 COMMENT(예: project.project = 그룹웨어 applet285 미러)를 따른다.

/// <summary>code.code — 공통 코드(프로젝트 상태·종류, 랩 테스트 종류 등). group_key로 묶인다.</summary>
public class CodeItem
{
    public long Id { get; set; }
    public string GroupKey { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public long? ParentId { get; set; }
    public int? SortOrder { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>project.project — 그룹웨어 applet285 미러(code 기준 upsert, 소프트 삭제)</summary>
public class Project
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public long? StatusId { get; set; }
    public long? TypeId { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? PartnerName { get; set; }
    public DateOnly? ErpRegDate { get; set; }
    /// <summary>그룹웨어 applet285 문서 수정일(GW에서 마지막으로 바뀐 시각). Worker가 수집. "최근 변경" 정렬용.</summary>
    public DateTime? SourceUpdatedAt { get; set; }
}

/// <summary>project.project_assignment — 공정(factory/booth/zone) ↔ 프로젝트 배당</summary>
public class ProjectAssignment
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public string Level { get; set; } = "";       // factory · booth · zone
    public long? FactoryId { get; set; }
    public long? BoothId { get; set; }
    public long? ZoneId { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset? AssignedAt { get; set; }
}

/// <summary>project.project_app — 프로젝트가 속한 앱(srs · smartlab …)</summary>
public class ProjectApp
{
    public long ProjectId { get; set; }
    public string AppKey { get; set; } = "";
}

/// <summary>
/// project.project_stage — dyone 자체 진행 단계(사용자 편집). 그룹웨어 상태(영업중/수주)와 별개.
/// 수주 이후 단계(설계/모듈라/현장설치/라인대응/완료)를 대응 인원이 dyone에서 직접 관리한다.
/// </summary>
public class ProjectStage
{
    public long ProjectId { get; set; }
    public long StageId { get; set; }          // code.code(group_key=project_stage).id
    public long? UpdatedBy { get; set; }        // org.employee.id
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>project.project_stage_status — 단계별 진행 상황(DY ONE에서 관리하는 설계·구매 이후 단계만). status: wait · run · done</summary>
public class ProjectStageStatus
{
    public long ProjectId { get; set; }
    public long StageId { get; set; }
    public string Status { get; set; } = "wait";
    public long? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// project.project_file — 프로젝트별 업로드 문서(사양서/설계/모듈라/라인 대응 등). dyone 자체 데이터.
/// 실물 파일은 파일 저장소(NAS 등)에 두고 여기엔 메타만. 분류=code.code(document_category).
/// </summary>
public class ProjectFile
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public long? CategoryId { get; set; }       // code.code(group_key=document_category)
    public long? StageId { get; set; }          // code.code(group_key=project_stage) — 업로드 당시 단계(선택)
    public string FileName { get; set; } = "";  // 원본 파일명
    public string StoredName { get; set; } = ""; // 저장소 Root 기준 상대경로(projects/<코드 이름>/<분류>/<원본파일명>)
    public string? ContentType { get; set; }
    public long SizeBytes { get; set; }
    public string? Description { get; set; }
    public long? UploadedBy { get; set; }        // org.employee.id
    public long? DepartmentId { get; set; }      // 업로더 소속 부서(org.department.id) 스냅샷
    public DateTimeOffset UploadedAt { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>plant.* — 공정 기준정보: 고객사 → 공장 → 부스 → 존 (MotoSim Launcher와 같은 구조)</summary>
public class Customer
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
}

public class Factory
{
    public long Id { get; set; }
    public long CustomerId { get; set; }
    public string Name { get; set; } = "";
    public string? Location { get; set; }
    public bool? IsDomestic { get; set; }
    public bool IsActive { get; set; }
}

public class Booth
{
    public long Id { get; set; }
    public long FactoryId { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
}

public class Zone
{
    public long Id { get; set; }
    public long BoothId { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
}

/// <summary>org.* — 그룹웨어 조직도 이관</summary>
public class Employee
{
    public long Id { get; set; }
    public string EmpNo { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Email { get; set; }
    public string? LoginId { get; set; }
    public long? DepartmentId { get; set; }
    public long? PositionId { get; set; }
    public long? DutyId { get; set; }
    public bool IsActive { get; set; }
}

public class Department
{
    public long Id { get; set; }
    public long? ParentId { get; set; }
    public string? Code { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
}

public class Position
{
    public long Id { get; set; }
    public string? Code { get; set; }
    public string Name { get; set; } = "";
    public int? Rank { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>org.role_mapping — 부서/직책 → 역할. (부서+직책) > (부서만) > (직책만), 가장 구체적인 것 우선</summary>
public class RoleMapping
{
    public long Id { get; set; }
    public long? DepartmentId { get; set; }
    public long? PositionId { get; set; }
    public long? DutyId { get; set; }
    public string RoleKey { get; set; } = "";
    public bool IsActive { get; set; }
}

/// <summary>smartlab.lab_test — SmartLab 랩 테스트</summary>
public class LabTest
{
    public long Id { get; set; }
    public long? ProjectId { get; set; }
    public string Name { get; set; } = "";
    public long? TestTypeId { get; set; }
    public long? OperatorId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>sync.state — 원천별 적재 워터마크</summary>
public class SyncState
{
    public string Source { get; set; } = "";
    public string Entity { get; set; } = "";
    public string? Watermark { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
