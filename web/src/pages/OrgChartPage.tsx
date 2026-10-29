import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api } from '../api/client'
import type { OrgEmployee, OrgEmployeeProject } from '../api/orgTypes'
import { ErrorBox, Loading, PageHead } from '../components/ui'
import { byRank, deptAndBelow, DeptTree, jobTitle, Phone, phoneText, useOrg, useOrgIndex } from '../components/org/OrgTree'

/**
 * 조직도: 그룹웨어 조직도(org.*) 미러를 부서 트리 + 부서 인원으로 보여 준다(읽기 전용).
 * 사람을 누르면 그 사람이 PM·투입 인원으로 들어간 프로젝트가 나온다.
 */
export function OrgChartPage() {
  const org = useOrg()
  const idx = useOrgIndex(org.data)
  const [dept, setDept] = useState<number | null>(null)
  const [search, setSearch] = useState('')
  const [person, setPerson] = useState<OrgEmployee | null>(null)

  if (org.isLoading) return <Loading />
  if (org.error || !org.data || !idx) return <ErrorBox error={org.error} />

  const root = (idx.children.get(null) ?? []).find((d) => (idx.total.get(d.id) ?? 0) > 0)
  const selected = dept ?? root?.id ?? null
  const q = search.trim().toLowerCase()
  // 검색어가 있으면 전체에서, 없으면 고른 부서(하위 포함)에서
  const scope = q || selected == null ? null : deptAndBelow(idx, selected)
  const people = org.data.employees.filter((e) =>
    q
      ? [e.name, e.position, e.duty, e.email, e.mobile, e.mobile?.replace(/-/g, ''), idx.path(e.departmentId)].some((v) => v?.toLowerCase().includes(q))
      : scope?.has(e.departmentId ?? -1),
  )
  // 부서별로 묶는다(트리 순서)
  const order: number[] = []
  const walk = (id: number) => { order.push(id); (idx.children.get(id) ?? []).forEach((c) => walk(c.id)) }
  ;(idx.children.get(null) ?? []).forEach((r) => walk(r.id))
  const groups = order
    .map((id) => ({ id, list: people.filter((e) => e.departmentId === id).sort(byRank) }))
    .filter((g) => g.list.length > 0)

  return (
    <>
      <PageHead eyebrow="ORGANIZATION" title="조직도" sub={`그룹웨어 조직도 기준 · 재직 ${org.data.employees.length}명 (매일 동기화, DY ONE에서는 조회만)`} />
      <div className="org-layout">
        <section className="panel org-side">
          <div className="panel-head"><h2>부서</h2></div>
          <div className="panel-body">
            <DeptTree idx={idx} selected={q ? null : selected} onSelect={(id) => { setDept(id); setSearch('') }} />
          </div>
        </section>
        <section className="panel">
          <div className="panel-head">
            <div>
              <h2>{q ? `'${search.trim()}' 검색 결과` : selected != null ? idx.depts.get(selected)?.name : '전체'}</h2>
              <p>{people.length}명 · 사람을 누르면 맡은 프로젝트를 봅니다</p>
            </div>
            <input className="search-input" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="이름·부서·직위·메일 검색" aria-label="직원 검색" />
          </div>
          <div className="panel-body">
            {groups.length === 0 && <div className="empty" style={{ padding: 30 }}>해당하는 직원이 없습니다.</div>}
            {groups.map((g) => (
              <div key={g.id} className="org-group">
                <h3>{idx.path(g.id) || idx.depts.get(g.id)?.name}</h3>
                <div className="org-cards">
                  {g.list.map((e) => (
                    <button key={e.id} className={`org-card${person?.id === e.id ? ' on' : ''}`} onClick={() => setPerson(e)}>
                      <span className="avatar">{e.name.slice(0, 1)}</span>
                      <span>
                        <b>{e.name}</b> <small>{jobTitle(e.position, e.duty)}</small>
                        {e.mobile && <small className="org-phone">☎ {phoneText(e.mobile)}{e.directTel ? ` · 직통 ${e.directTel}` : ''}</small>}
                        <small className="org-mail">{e.email ?? ''}</small>
                        {(e.pmCount > 0 || e.memberCount > 0) && (
                          <span className="badge-row" style={{ marginTop: 6 }}>
                            {e.pmCount > 0 && <span className="badge blue">PM {e.pmCount}</span>}
                            {e.memberCount > 0 && <span className="badge">투입 {e.memberCount}</span>}
                          </span>
                        )}
                      </span>
                    </button>
                  ))}
                </div>
              </div>
            ))}
          </div>
        </section>
      </div>
      {person && <PersonProjects person={person} path={idx.path(person.departmentId)} onClose={() => setPerson(null)} />}
    </>
  )
}

function PersonProjects({ person, path, onClose }: { person: OrgEmployee; path: string; onClose(): void }) {
  const navigate = useNavigate()
  const { data, isLoading, error } = useQuery({
    queryKey: ['org-person-projects', person.id],
    queryFn: () => api<OrgEmployeeProject[]>(`/api/org/employees/${person.id}/projects`),
  })
  return (
    <aside className="org-person panel" aria-label={`${person.name} 정보`}>
      <div className="panel-head">
        <div>
          <h2>{person.name} <small style={{ color: 'var(--muted)', fontWeight: 400 }}>{jobTitle(person.position, person.duty)}</small></h2>
          <p>{path}{person.email ? ` · ${person.email}` : ''}</p>
          <p className="org-person-phone">
            <Phone value={person.mobile} label="휴대폰 " />
            {person.directTel && <> · <Phone value={person.directTel} label="직통 " /></>}
          </p>
        </div>
        <button className="icon-btn" onClick={onClose} aria-label="닫기">×</button>
      </div>
      <div className="panel-body">
        <h3 style={{ fontSize: 14, margin: '0 0 8px' }}>맡은 프로젝트</h3>
        {isLoading ? <Loading /> : error ? <ErrorBox error={error} /> : !data?.length ? (
          <div className="empty" style={{ padding: 20 }}>배정된 프로젝트가 없습니다.</div>
        ) : (
          <ul className="org-projects">
            {data.map((p) => (
              <li key={p.projectId}>
                <button className="row-title" onClick={() => navigate(`/projects/${p.projectId}`)}>{p.name}</button>
                <small>
                  <span className="code">{p.code}</span> · <span className={`badge ${p.role === 'pm' ? 'blue' : ''}`}>{p.role === 'pm' ? 'PM' : '투입'}</span>
                  {p.task ? ` · ${p.task}` : ''}
                </small>
              </li>
            ))}
          </ul>
        )}
      </div>
    </aside>
  )
}
