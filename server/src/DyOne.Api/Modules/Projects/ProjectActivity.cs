using Npgsql;

namespace DyOne.Api.Modules.Projects;

/// <summary>프로젝트의 가장 최근 활동. Kind는 아래 Events의 종류 코드, Label은 화면 문구, By는 한 사람(알 수 있을 때).</summary>
public record ProjectActivityInfo(DateTimeOffset At, string Kind, string Label, string? By, int Recent7d);

/// <summary>
/// 프로젝트 '최근 변경' = 이 프로젝트에서 일어난 모든 작업 중 가장 최근 것(목록·홈·상세 공통).
/// 단계 변경만이 아니라 파일 업로드·인원 배정·그룹웨어 수정·모듈라·ES 이슈·SmartLab·SRS 작업까지 본다.
/// 새 작업 종류가 생기면 Events에 한 줄(union all) 추가하면 된다.
/// </summary>
public static class ProjectActivity
{
    /// <summary>종류 코드 → 화면 문구</summary>
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["stage"] = "진행 상황 변경",
        ["gw_status"] = "그룹웨어 상태 변경",
        ["file"] = "파일 업로드",
        ["member"] = "인원 배정",
        ["gw"] = "그룹웨어 신규 등록",
        ["erp"] = "ERP 등록",
        ["modular"] = "모듈라 작업",
        ["es"] = "ES 이슈",
        ["smartlab"] = "SmartLab 테스트",
        ["srs"] = "SRS 측정",
    };

    /// <summary>
    /// 활동 한 줄씩: project_id, at(timestamptz), kind, emp(DY ONE 직원 id — DY ONE에서 한 일만), who(이름 — 그룹웨어에서 온 일).
    /// 그룹웨어 시각(source_updated_at)과 ERP 등록일은 한국 시간 기준으로 바꾼다. 모듈라·ES는 프로젝트 코드로 붙인다.
    /// </summary>
    public const string Events = @"
        select h.project_id, h.changed_at as at, case when h.source = 'groupware' then 'gw_status' else 'stage' end as kind, h.changed_by as emp, null::text as who
          from project.project_stage_history h
        union all
        select f.project_id, f.uploaded_at, 'file', f.uploaded_by, null from project.project_file f where f.is_active
        union all
        select m.project_id, m.assigned_at, 'member', m.assigned_by, null from project.project_member m
        union all
        select p.id, p.source_updated_at at time zone 'Asia/Seoul', 'gw', null, null from project.project p where p.source_updated_at is not null
        union all
        select p.id, p.erp_reg_date::timestamp at time zone 'Asia/Seoul', 'erp', null, null from project.project p where p.erp_reg_date is not null
        union all
        select p.id, b.updated_at, 'modular', null, b.updated_by_name
          from modular.build b join project.project p on p.code = b.project_code where not b.is_deleted and b.updated_at is not null
        union all
        select p.id, d.updated_at, 'es', null, d.updated_by_name
          from works.doc d
          join works.field f on f.applet_id = d.applet_id and f.props->>'integrationAppletId' = '285'
          join project.project p on p.code = substring(d.display->>f.cid from '(DL[A-Z]*-\d{2}-\d+)')
         where not d.is_deleted and d.updated_at is not null
        union all
        -- SmartLab·SRS의 updated_at은 데이터 이관 때 한꺼번에 찍힌 값이 많아 실제 시험·측정 날짜를 쓴다
        select l.project_id, coalesce(l.ended_at, l.started_at), 'smartlab', null, null from smartlab.lab_test l where l.project_id is not null
        union all
        select m.project_id, m.captured_at, 'srs', null, null from srs.measurement m where m.project_id is not null";

    /// <summary>프로젝트별 가장 최근 활동(ids가 null이면 전체). 표가 아직 없는 환경(예: project_member 미적용)이면 빈 결과.</summary>
    public static async Task<Dictionary<long, ProjectActivityInfo>> LatestAsync(NpgsqlConnection conn, IReadOnlyCollection<long>? ids, CancellationToken ct)
    {
        var map = new Dictionary<long, ProjectActivityInfo>();
        if (ids is { Count: 0 }) return map;
        await using var cmd = new NpgsqlCommand($@"
            with ev as ({Events}),
            scoped as (select * from ev where at is not null and at <= now() + interval '1 day' {(ids is null ? "" : "and project_id = any(@ids)")}),
            ranked as (
              select s.*, row_number() over (partition by project_id order by at desc) as rn,
                     count(*) filter (where at >= now() - interval '7 days') over (partition by project_id) as recent
                from scoped s)
            select r.project_id, r.at, r.kind, coalesce(e.name, r.who), r.recent::int
              from ranked r left join org.employee e on e.id = r.emp
             where r.rn = 1", conn);
        if (ids is not null) cmd.Parameters.AddWithValue("ids", ids.ToArray());
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var kind = r.GetString(2);
                map[r.GetInt64(0)] = new ProjectActivityInfo(r.GetFieldValue<DateTimeOffset>(1), kind, Labels.GetValueOrDefault(kind, kind),
                    r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4));
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable) { }
        return map;
    }
}
