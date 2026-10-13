import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { api } from '../../api/client'
import type { AiSource, Code, LabTest, PagedResult, Project, ProjectDetail, Section } from '../../api/types'
import { NotConnected } from '../../components/NotConnected'
import { Badge, DataTable, ErrorBox, Loading, PageHead } from '../../components/ui'
import { platform } from '../../platform/bridge'
import { DesktopTools } from './DesktopTools'
import { AssignmentList, LabDetailDialog, ProjectDetailDialog } from './DetailDialogs'
import { ProjectAI } from './ProjectAI'
import { isSection, sections } from './sections'

type DialogState = { kind: 'project'; id: number } | { kind: 'lab'; lab: LabTest } | null
const PAGE_SIZE = 50
const date = (v?: string) => (v ? new Date(v).toLocaleDateString('ko-KR') : '-')

export function ProjectsPage() {
  const [params, setParams] = useSearchParams()
  const navigate = useNavigate()
  const scope = params.get('project') ? Number(params.get('project')) : null
  const section: Section = isSection(params.get('tab')) ? (params.get('tab') as Section) : 'progress'
  const statusId = params.get('status') ?? ''
  const customer = params.get('customer') ?? ''
  const [search, setSearch] = useState(params.get('search') ?? '')
  useEffect(() => { setSearch(params.get('search') ?? '') }, [params])
  const [page, setPage] = useState(1)
  const [dialog, setDialog] = useState<DialogState>(null)

  const statuses = useQuery({ queryKey: ['codes', 'project_status'], queryFn: () => api<Code[]>('/api/codes/project_status') })
  const detail = useQuery({
    queryKey: ['project', scope],
    queryFn: () => api<ProjectDetail>(`/api/projects/${scope}`),
    enabled: scope !== null,
  })
  const projects = useQuery({
    queryKey: ['projects', search, statusId, customer, page],
    queryFn: () => {
      const q = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) })
      if (search) q.set('search', search)
      if (statusId) q.set('statusId', statusId)
      if (customer) q.set('customer', customer)
      return api<PagedResult<Project>>(`/api/projects?${q}`)
    },
    enabled: section === 'progress' && scope === null,
    placeholderData: keepPreviousData,
  })
  const labs = useQuery({
    queryKey: ['lab-tests', scope, page],
    queryFn: () => {
      const q = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) })
      if (scope) q.set('projectId', String(scope))
      return api<PagedResult<LabTest>>(`/api/lab-tests?${q}`)
    },
    placeholderData: keepPreviousData,
  })
  const project = detail.data?.project ?? null
  const spec = sections.find((s) => s.key === section)!

  const update = (next: { project?: number | null; tab?: Section; status?: string }) => {
    const p = new URLSearchParams(params)
    if (next.project !== undefined) {
      if (next.project) p.set('project', String(next.project))
      else p.delete('project')
    }
    if (next.tab) p.set('tab', next.tab)
    if (next.status !== undefined) {
      if (next.status) p.set('status', next.status)
      else p.delete('status')
    }
    setPage(1)
    setParams(p)
  }

  const openSource = (s: AiSource) => {
    if (s.kind === 'project') update({ project: s.id, tab: 'progress' })
    else update({ tab: 'lab' })
  }

  let body
  if (!spec.connected) {
    body = <NotConnected what={spec.name} source={`그룹웨어 WORKS applet ${spec.applet}`} />
  } else if (section === 'progress' && scope !== null) {
    body = detail.isLoading ? <Loading /> : detail.data ? <div className="panel-body"><AssignmentList assignments={detail.data.assignments} /></div> : <ErrorBox error={detail.error} />
  } else if (section === 'progress') {
    body = projects.isLoading ? (
      <Loading />
    ) : projects.error ? (
      <ErrorBox error={projects.error} />
    ) : (
      <>
        <DataTable
          headers={['프로젝트 코드·명', '발주처', '진행 단계', '종류', 'ERP 등록날짜', '최근 변경']}
          rows={(projects.data?.items ?? []).map((p) => ({
            key: String(p.id),
            cells: [
              <>
                <span className="code">{p.code}</span>
                <br />
                <button className="row-title" onClick={() => navigate(`/projects/${p.id}`)}>
                  {p.name}
                </button>
              </>,
              p.partnerName ?? '-',
              p.effectiveStage ? <Badge status={p.effectiveStage} /> : (p.status ?? '미지정'),
              p.type ?? '-',
              p.erpRegDate ?? '-',
              date(p.updatedAt),
            ],
          }))}
          empty="검색 조건에 맞는 프로젝트가 없습니다."
        />
        <Pager total={projects.data?.total ?? 0} page={page} onPage={setPage} />
      </>
    )
  } else {
    body = labs.isLoading ? (
      <Loading />
    ) : (
      <>
        <DataTable
          headers={['테스트 명·프로젝트', '종류', '담당', '테스트 시작', '테스트 종료']}
          rows={(labs.data?.items ?? []).map((l) => ({
            key: String(l.id),
            cells: [
              <>
                <button className="row-title" onClick={() => setDialog({ kind: 'lab', lab: l })}>
                  {l.name}
                </button>
                <small>{l.projectCode ? `${l.projectCode} · ${l.projectName}` : '프로젝트 미연결'}</small>
              </>,
              l.type ?? '-',
              l.operator ?? '-',
              date(l.startedAt),
              date(l.endedAt),
            ],
          }))}
          empty="연결된 랩 테스트가 없습니다."
        />
        <Pager total={labs.data?.total ?? 0} page={page} onPage={setPage} />
      </>
    )
  }

  return (
    <>
      <PageHead eyebrow="PROJECT WORKSPACE" title="프로젝트" sub="그룹웨어 프로젝트 · 공정 배당 · SmartLab 랩 테스트 (dyone DB)" />
      <div className="project-with-ai">
        <div className="project-work">
          {scope && (
            <div className="project-toolbar">
              <button className="secondary" onClick={() => update({ project: null })}>
                전체 프로젝트로
              </button>
            </div>
          )}
          {!scope && (
            <>
              <div className="customer-chips">
                {['전체', '현대', '기아', 'GM', '르노', '쌍용', '기타'].map((c) => {
                  const active = c === '전체' ? !customer : customer === c
                  return (
                    <button
                      key={c}
                      className={active ? 'chip active' : 'chip'}
                      onClick={() => {
                        const pr = new URLSearchParams(params)
                        if (c === '전체') pr.delete('customer')
                        else pr.set('customer', c)
                        setPage(1)
                        setParams(pr)
                      }}
                    >
                      {c}
                    </button>
                  )
                })}
              </div>
              {/* 검색·상태 필터: 고객사 칩 아래로 이동(기존 상단 ProjectPicker 대체). 검색하면 해당 프로젝트만 목록에 표시 */}
              <div className="sub-filters project-search">
                <input
                  key={search}
                  className="search-input"
                  placeholder="프로젝트 코드·이름·발주처 검색"
                  defaultValue={search}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') {
                      setSearch(e.currentTarget.value.trim())
                      setPage(1)
                    }
                  }}
                  aria-label="프로젝트 검색"
                />
                <label htmlFor="statusFilter">상태</label>
                <select id="statusFilter" value={statusId} onChange={(e) => update({ status: e.target.value })}>
                  <option value="">전체</option>
                  {statuses.data?.map((s) => (
                    <option key={s.id} value={s.id}>
                      {s.name}
                    </option>
                  ))}
                  <option value="0">미지정</option>
                </select>
              </div>
            </>
          )}
          {project && (
            <div className="scope-card">
              <div>
                <small>
                  {project.code} · {project.partnerName ?? '발주처 미지정'} · {project.type ?? '종류 미지정'}
                </small>
                <h2>{project.name}</h2>
                <p>
                  ERP {project.erpRegDate ?? '-'} · 공정 배당 {detail.data?.assignments.length ?? 0}건 · 랩 테스트 {detail.data?.labTestCount ?? 0}건
                  {detail.data?.apps.length ? ` · 앱: ${detail.data.apps.join(', ')}` : ''}
                </p>
              </div>
              <button className="secondary" onClick={() => navigate(`/projects/${project.id}`)}>
                상세
              </button>
              <span />
              {project.status ? <Badge status={project.status} /> : <span className="badge">상태 미지정</span>}
            </div>
          )}
          <div className="source-note">
            <span>원본: 그룹웨어 WORKS · dyone DB 미러(읽기 전용)</span>
            <a
              href={`https://gw.doolim-yaskawa.com/app/works/applet/${spec.applet}/home`}
              target="_blank"
              rel="noopener noreferrer"
              onClick={(e) => {
                if (platform.kind !== 'web') {
                  e.preventDefault()
                  void platform.openExternal(e.currentTarget.href)
                }
              }}
            >
              원본 WORKS ↗
            </a>
          </div>
          <section className="panel">
            <div className="panel-head">
              <div>
                <h2>{section === 'progress' && scope ? '공정 배당' : spec.name}</h2>
                <p>
                  {section === 'progress' && !scope && projects.data && `${projects.data.total.toLocaleString()}개 프로젝트`}
                  {section === 'lab' && labs.data && `${labs.data.total.toLocaleString()}개 테스트`}
                  {section === 'progress' && scope && '프로젝트에 배당된 고객사 › 공장 › 부스 › 존'}
                </p>
              </div>
            </div>
            {body}
          </section>
          {section === 'progress' && <DesktopTools project={project} />}
        </div>
        <ProjectAI project={project} onOpenSource={openSource} />
      </div>

      {dialog?.kind === 'project' && <ProjectDetailDialog projectId={dialog.id} onClose={() => setDialog(null)} />}
      {dialog?.kind === 'lab' && <LabDetailDialog lab={dialog.lab} onClose={() => setDialog(null)} />}
    </>
  )
}

function Pager({ total, page, onPage }: { total: number; page: number; onPage(p: number): void }) {
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE))
  if (pages <= 1) return null
  return (
    <div className="pager">
      <button className="secondary" disabled={page <= 1} onClick={() => onPage(page - 1)}>
        ← 이전
      </button>
      <span>
        {page} / {pages}
      </span>
      <button className="secondary" disabled={page >= pages} onClick={() => onPage(page + 1)}>
        다음 →
      </button>
    </div>
  )
}
