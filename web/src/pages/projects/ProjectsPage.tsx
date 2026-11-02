import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { Fragment, useEffect, useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { api } from '../../api/client'
import { useAuth, useIsPartner } from '../../auth/AuthContext'
import type { AiSource, Code, LabTest, PagedResult, Project, ProjectDetail, Section } from '../../api/types'
import { NotConnected } from '../../components/NotConnected'
import { Badge, DataTable, ErrorBox, GwEndBadge, Loading, PageHead, StageBadge } from '../../components/ui'
import { Icon } from '../../components/icons'
import { DesktopTools } from './DesktopTools'
import { ModularSection } from './ModularSection'
import { WorksAppSection } from './WorksAppSection'
import { AssignmentList, LabDetailDialog, ProjectDetailDialog } from './DetailDialogs'
import { ProjectAI } from './ProjectAI'
import { isSection, sections } from './sections'
import '../../styles/project-detail.css'

type DialogState = { kind: 'project'; id: number } | { kind: 'lab'; lab: LabTest } | null
const PAGE_SIZE = 50
const date = (v?: string) => (v ? new Date(v).toLocaleDateString('ko-KR') : '-')
const md = (v?: string) => (v ? `${new Date(v).getMonth() + 1}.${String(new Date(v).getDate()).padStart(2, '0')}` : '')
/** 진행 단계 점 색(D안) — 단계 이름 기준 */
const STAGE_COLOR: Record<string, string> = { '영업': '#2563EB', '수주': '#15803D', '설계·검토': '#6D28D9', '모듈라': '#0F766E', '현장': '#B45309', '라인 대응·종료': '#475467' }
const CUSTOMERS = ['전체', '현대', '기아', 'GM', '르노', '쌍용', '기타']

export function ProjectsPage() {
  const [params, setParams] = useSearchParams()
  const navigate = useNavigate()
  const scope = params.get('project') ? Number(params.get('project')) : null
  const partner = useIsPartner() // 협력사: 배정된 프로젝트 목록만(다른 탭·AI·설치형 앱 안내 숨김)
  const section: Section = !partner && isSection(params.get('tab')) ? (params.get('tab') as Section) : 'progress'
  const statusId = params.get('status') ?? ''
  const stageCode = params.get('stage') ?? '' // 진행 단계(project_stage 코드) — 서버가 대표 단계로 거른다
  const customer = params.get('customer') ?? ''
  const [search, setSearch] = useState(params.get('search') ?? '')
  useEffect(() => { setSearch(params.get('search') ?? '') }, [params])
  const [page, setPage] = useState(1)
  const [dialog, setDialog] = useState<DialogState>(null)
  const [aiOpen, setAiOpen] = useState(false) // 프로젝트 AI: 떠 있는 버튼 → 오른쪽 서랍
  const { user } = useAuth()

  const statuses = useQuery({ queryKey: ['codes', 'project_status'], queryFn: () => api<Code[]>('/api/codes/project_status') })
  const stageCodes = useQuery({ queryKey: ['codes', 'project_stage'], queryFn: () => api<Code[]>('/api/codes/project_stage') })
  const detail = useQuery({
    queryKey: ['project', scope],
    queryFn: () => api<ProjectDetail>(`/api/projects/${scope}`),
    enabled: scope !== null,
  })
  const projects = useQuery({
    queryKey: ['projects', search, statusId, customer, stageCode, page],
    queryFn: () => {
      const q = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) })
      if (search) q.set('search', search)
      if (statusId) q.set('statusId', statusId)
      if (customer) q.set('customer', customer)
      if (stageCode) q.set('stage', stageCode)
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
    enabled: !partner, // 시험 데이터는 직원 전용
    placeholderData: keepPreviousData,
  })
  const project = detail.data?.project ?? null
  const spec = sections.find((s) => s.key === section)!

  const update = (next: { project?: number | null; tab?: Section; status?: string; stage?: string }) => {
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
    if (next.stage !== undefined) {
      if (next.stage) p.set('stage', next.stage)
      else p.delete('stage')
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
  } else if (section === 'modular') {
    body = <ModularSection projectCode={project?.code ?? null} />
  } else if (section === 'es') {
    body = <WorksAppSection appletId={spec.applet} projectCode={project?.code ?? null} />
  } else if (section === 'progress' && scope !== null) {
    body = detail.isLoading ? <Loading /> : detail.data ? <div className="panel-body"><AssignmentList assignments={detail.data.assignments} /></div> : <ErrorBox error={detail.error} />
  } else if (section === 'progress') {
    body = projects.isLoading ? (
      <Loading />
    ) : projects.error ? (
      <ErrorBox error={projects.error} />
    ) : (
      <>
        <ProjectTable items={projects.data?.items ?? []} me={user?.name} onOpen={(id) => navigate(`/projects/${id}`)} />
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

  const listing = section === 'progress' && !scope

  return (
    <>
      <PageHead
        eyebrow="PROJECT WORKSPACE"
        title="프로젝트"
        sub={listing ? '그룹웨어 WORKS · dyone DB 미러(읽기 전용) · 매일 동기화' : '그룹웨어 프로젝트 · 공정 배당 · SmartLab 랩 테스트 (dyone DB)'}
      />
      <div className="project-solo">
        {scope && (
          <div className="project-toolbar">
            <button className="secondary" onClick={() => update({ project: null })}>
              전체 프로젝트로
            </button>
          </div>
        )}
        {!scope && (
          <div className="pl-filters">
            <label className="pl-search">
              <Icon name="srs" size={18} />
              <input
                key={search}
                placeholder="프로젝트 코드·이름·발주처 검색 (Enter)"
                defaultValue={search}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    setSearch(e.currentTarget.value.trim())
                    setPage(1)
                  }
                }}
                aria-label="프로젝트 검색"
              />
            </label>
            <div className="pl-customers" role="tablist" aria-label="고객사">
              {CUSTOMERS.map((c) => {
                const active = c === '전체' ? !customer : customer === c
                return (
                  <button
                    key={c}
                    role="tab"
                    aria-selected={active}
                    className={active ? 'active' : ''}
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
          </div>
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
            {project.effectiveStage ? (
              <span className="badge-row"><StageBadge name={project.effectiveStage} status={project.effectiveStatus} /><GwEndBadge status={project.status} /></span>
            ) : project.status ? <Badge status={project.status} /> : <span className="badge">상태 미지정</span>}
          </div>
        )}
        <section className="panel pl-panel">
          {listing ? (
            <div className="pl-toolbar">
              <div className="pl-seg" role="group" aria-label="진행 단계">
                <button className={!stageCode ? 'on' : ''} onClick={() => update({ stage: '' })}>전체</button>
                {(stageCodes.data ?? []).map((st) => (
                  <button key={st.id} className={stageCode === st.code ? 'on' : ''} onClick={() => update({ stage: stageCode === st.code ? '' : st.code })}>{st.name}</button>
                ))}
              </div>
              <span className="pl-right">
                <label>
                  그룹웨어 상태
                  <select value={statusId} onChange={(e) => update({ status: e.target.value })} aria-label="그룹웨어 상태">
                    <option value="">진행 중(기본)</option>
                    {statuses.data?.map((st) => (
                      <option key={st.id} value={st.id}>
                        {st.name}
                      </option>
                    ))}
                    <option value="0">미지정</option>
                  </select>
                </label>
                <span><b>{(projects.data?.total ?? 0).toLocaleString()}</b>건 · 최근 작업순</span>
              </span>
            </div>
          ) : (
            <div className="panel-head">
              <div>
                <h2>{section === 'progress' && scope ? '공정 배당' : spec.name}</h2>
                <p>
                  {section === 'lab' && labs.data && `${labs.data.total.toLocaleString()}개 테스트`}
                  {section === 'progress' && scope && '프로젝트에 배당된 고객사 › 공장 › 부스 › 존'}
                  {section === 'modular' && '그룹웨어 모듈라빌드 프로젝트 정보(applet 376)와 같은 목록 · 간트 · 상세'}
                  {section === 'es' && '그룹웨어 ES 이슈사항 보고(applet 281)와 같은 목록 · 상세'}
                </p>
              </div>
            </div>
          )}
          {body}
        </section>
        {section === 'progress' && !partner && <DesktopTools project={project} />}
      </div>

      {!partner && (
        <>
          <button className="pl-ai-fab" onClick={() => setAiOpen(!aiOpen)} aria-expanded={aiOpen}>
            <span><Icon name="spark" size={16} /></span>프로젝트 AI
          </button>
          {aiOpen && (
            <div className="pl-ai-drawer" role="dialog" aria-label="프로젝트 AI">
              <button className="dy-icon-btn pl-ai-close" aria-label="프로젝트 AI 닫기" onClick={() => setAiOpen(false)}><Icon name="close" /></button>
              <ProjectAI project={project} onOpenSource={openSource} />
            </div>
          )}
        </>
      )}

      {dialog?.kind === 'project' && <ProjectDetailDialog projectId={dialog.id} onClose={() => setDialog(null)} />}
      {dialog?.kind === 'lab' && <LabDetailDialog lab={dialog.lab} onClose={() => setDialog(null)} />}
    </>
  )
}

/** 프로젝트 목록 표(D안): 한 줄 40px. 코드 · 프로젝트 · 발주처 · 진행 단계(색 점) · PM · ERP 등록 · 최근 작업 */
function ProjectTable({ items, me, onOpen }: { items: Project[]; me?: string; onOpen(id: number): void }) {
  if (items.length === 0) return <div className="pl-empty">검색 조건에 맞는 프로젝트가 없습니다.</div>
  return (
    <div className="pl-table">
      <div className="pl-row pl-head">
        <span>코드</span><span>프로젝트</span><span>발주처</span><span>진행 단계</span><span>PM</span><span>ERP 등록</span><span>최근 작업</span>
      </div>
      {items.map((p) => {
        const mine = !!me && p.lastChangedBy === me
        const stage = p.effectiveStage
        return (
          <button key={p.id} className="pl-row" onClick={() => onOpen(p.id)}>
            <span className="pl-code">{p.code}</span>
            <span className="pl-name">
              <b>{p.name}</b>
              {mine && <span className="pl-mine">내 작업</span>}
              <GwEndBadge status={p.status} />
            </span>
            <span className="pl-muted ell">{p.partnerName ?? '-'}</span>
            <span className="pl-stage">
              {stage ? (
                <><i style={{ background: STAGE_COLOR[stage] ?? '#475467' }} />{stage}{p.effectiveStatus === 'done' && <small>완료</small>}</>
              ) : <span className="pl-dim">{p.status ?? '미지정'}</span>}
            </span>
            <span className={p.pmName ? '' : 'pl-dim'}>{p.pmName ?? '미지정'}</span>
            <span className="pl-num pl-muted">{p.erpRegDate ?? '—'}</span>
            <span className="pl-muted ell">
              {p.lastChangedAt ? [md(p.lastChangedAt), p.lastChangedSource, p.lastChangedBy].filter(Boolean).join(' · ') : '—'}
            </span>
          </button>
        )
      })}
    </div>
  )
}

function Pager({ total, page, onPage }: { total: number; page: number; onPage(p: number): void }) {
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE))
  if (pages <= 1) return null
  const from = (page - 1) * PAGE_SIZE + 1
  const to = Math.min(total, page * PAGE_SIZE)
  // 처음 · 지금 앞뒤 · 끝만 보이고 사이는 …
  const nums = [...new Set([1, page - 1, page, page + 1, pages].filter((n) => n >= 1 && n <= pages))].sort((x, y) => x - y)
  return (
    <div className="pl-pager">
      <span><b>{from.toLocaleString()}–{to.toLocaleString()}</b> / {total.toLocaleString()}</span>
      <span className="pl-pages">
        <button aria-label="이전 페이지" disabled={page <= 1} onClick={() => onPage(page - 1)}>‹</button>
        {nums.map((n, i) => (
          <Fragment key={n}>
            {i > 0 && n - nums[i - 1] > 1 && <span className="pl-gap">…</span>}
            <button className={n === page ? 'on' : ''} aria-current={n === page ? 'page' : undefined} onClick={() => onPage(n)}>{n}</button>
          </Fragment>
        ))}
        <button aria-label="다음 페이지" disabled={page >= pages} onClick={() => onPage(page + 1)}>›</button>
      </span>
    </div>
  )
}
