import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Fragment, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../../api/client'
import type { Code, JournalItem, Share } from '../../api/types'
import { useAuth } from '../../auth/AuthContext'
import { Icon } from '../../components/icons'
import { ErrorBox, Loading } from '../../components/ui'
import '../../styles/project-journal.css'
import '../../styles/d-journal.css'

/**
 * 프로젝트 진행 일지 — 프로젝트 탭(이 프로젝트만)과 메뉴 '진행 일지'(여러 프로젝트)에서 같이 쓴다.
 * - 공유 범위: 개인(나만) · 부서(같은 부서) · 전체(모든 부서, 진행 단계 탭에도 보임). 기본은 개인, 작성자가 바꾼다.
 * - 일지 목록에는 내 것 + 같은 부서가 공유한 것만 보인다(다른 부서 일지는 진행 단계 탭에서 전체 공유분만).
 * - 상태(진행·완료)는 작성자와 같은 부서 사람이 바꾼다. 남기면 '진행'으로 시작한다.
 * 모양: D안(2026-11-02) — 쓰기 칸 위, 목록은 날짜별로 묶은 촘촘한 표(d-journal.css).
 */
const SHARE_LABEL: Record<Share, string> = { me: '개인', dept: '부서', project: '전체' }
const SHARE_HELP: Record<Share, string> = { me: '나만 봅니다', dept: '우리 부서만 봅니다', project: '이 프로젝트의 모든 부서가 보고, 진행 단계 목록에도 나옵니다' }
const STATUS_LABEL = { run: '진행', done: '완료' } as const
/** 단계 점 색(D안) — 프로젝트 목록과 같은 색 */
const STAGE_COLOR: Record<string, string> = { '영업': '#2563EB', '수주': '#15803D', '설계·검토': '#6D28D9', '모듈라': '#0F766E', '현장': '#B45309', '라인 대응·종료': '#475467' }

/** 부서별로 일지를 쓸 때 기본 단계(코드). 없으면 프로젝트의 지금 단계 */
function deptStage(dept?: string): string | undefined {
  const d = dept ?? ''
  if (/영업|선행검토/.test(d)) return 'opportunity'
  if (/설계|소프트웨어|공정검토|SW/.test(d)) return 'design'
  if (/모듈라|도장공정/.test(d)) return 'modula'
  if (/어플리케이션/.test(d)) return 'install'
  if (/^CS/.test(d)) return 'line'
  return undefined
}

const updated = (v: string) => {
  const d = new Date(v)
  return `${d.getMonth() + 1}/${d.getDate()} ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
}
const hm = (v: string) => {
  const d = new Date(v)
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
}
/** 날짜 묶음 이름: 10월 30일 금요일 */
const dayLabel = (v: string) => new Date(v).toLocaleDateString('ko-KR', { month: 'long', day: 'numeric', weekday: 'long' })
const dayKey = (v: string) => new Date(v).toDateString()

const useStages = () => useQuery({ queryKey: ['codes', 'project_stage'], queryFn: () => api<Code[]>('/api/codes/project_stage') })

/** 한 일: 첫 줄만, 길면 펼치기 */
function BodyCell({ text }: { text: string }) {
  const [open, setOpen] = useState(false)
  const first = text.split('\n')[0]
  const long = text.includes('\n') || first.length > 70
  if (!long) return <>{text}</>
  return open
    ? <><div style={{ whiteSpace: 'pre-wrap' }}>{text}</div><button className="text-button jr-more" onClick={() => setOpen(false)}>접기 ▴</button></>
    : <>{first.length > 70 ? `${first.slice(0, 70)}…` : first} <button className="text-button jr-more" onClick={() => setOpen(true)}>펼치기 ▾</button></>
}

function StageCell({ stage }: { stage?: string }) {
  if (!stage) return <span className="dj-dim">-</span>
  return <span className="dj-stage"><i style={{ background: STAGE_COLOR[stage] ?? '#475467' }} />{stage}</span>
}

/** 쓰기 칸. projectId가 있으면 그 프로젝트로, 없으면 내 프로젝트 중에서 고른다. */
function JournalWrite({ projectId, currentStageCode, canWrite }: { projectId?: number; currentStageCode?: string; canWrite: boolean }) {
  const qc = useQueryClient()
  const { user } = useAuth()
  const stages = useStages()
  const myProjects = useQuery({
    queryKey: ['journal-projects'],
    queryFn: () => api<{ id: number; code: string; name: string }[]>('/api/journal/projects'),
    enabled: projectId === undefined,
  })
  const [pid, setPid] = useState<number | ''>('')
  const [share, setShare] = useState<Share>('me')
  const [stageId, setStageId] = useState<number | ''>('')
  const [text, setText] = useState('')
  const [expand, setExpand] = useState(false)
  const [saved, setSaved] = useState<{ id: number; text: string } | null>(null)

  const list = stages.data ?? []
  const defCode = deptStage(user?.department) ?? currentStageCode
  const sid = stageId === '' ? list.find((s) => s.code === defCode)?.id ?? list[0]?.id : stageId
  const target = projectId ?? (pid === '' ? myProjects.data?.[0]?.id : pid)

  const refresh = () => {
    qc.invalidateQueries({ queryKey: ['journal'] })
  }
  const add = useMutation({
    mutationFn: () => api<{ id: number }>(`/api/projects/${target}/journal`, { method: 'POST', json: { body: text.trim(), stageId: sid ?? null, share } }),
    onSuccess: (r) => { setSaved(share === 'me' ? { id: r.id, text: text.trim().split('\n')[0] } : null); setText(''); setExpand(false); refresh() },
  })
  const upShare = useMutation({
    mutationFn: (id: number) => api(`/api/journal/${id}`, { method: 'PUT', json: { share: 'project' } }),
    onSuccess: () => { setSaved(null); refresh() },
  })

  if (!canWrite || (projectId === undefined && myProjects.data?.length === 0)) {
    return <div className="dj-write"><div className="dj-ro">{user?.name}님은 {projectId === undefined ? '배정된 프로젝트가 없어' : '이 프로젝트 인원이 아니라'} 일지를 쓸 수 없습니다. PM에게 프로젝트 인원 추가를 요청하세요.</div></div>
  }
  const submit = () => { if (text.trim() && target) add.mutate() }
  return (
    <>
      <div className="dj-write">
        <div className="dj-write-top">
          {projectId === undefined && (
            <label className="dj-project">
              <Icon name="projects" size={16} />
              <select aria-label="프로젝트" value={target ?? ''} onChange={(e) => setPid(Number(e.target.value))}>
                {(myProjects.data ?? []).map((p) => <option key={p.id} value={p.id}>{p.code} {p.name}</option>)}
              </select>
            </label>
          )}
          <span className="dj-pills" role="group" aria-label={`단계 (기본값: ${user?.department ?? ''} 담당 단계)`}>
            {list.map((s, i) => (
              <button key={s.id} type="button" className={sid === s.id ? 'on' : ''} aria-pressed={sid === s.id} onClick={() => setStageId(s.id)}>{i + 1}. {s.name}</button>
            ))}
          </span>
        </div>
        {expand
          ? <textarea className="dj-text" rows={5} value={text} onChange={(e) => setText(e.target.value)} autoFocus aria-label="일지 내용"
              placeholder="자세히 쓰기 — 첫 줄이 목록에 제목처럼 보이고, 나머지는 펼쳐서 봅니다. (Ctrl+Enter로 남기기)"
              onKeyDown={(e) => { if (e.key === 'Enter' && e.ctrlKey) { e.preventDefault(); submit() } }} />
          : <input className="dj-text" value={text} onChange={(e) => setText(e.target.value)} aria-label="일지 내용" placeholder="오늘 한 일, 결정한 것, 다음에 할 일을 한 줄로 (Enter로 남기기)"
              onKeyDown={(e) => { if (e.key === 'Enter' && !e.nativeEvent.isComposing) { e.preventDefault(); submit() } }} />}
        <div className="dj-write-foot">
          <span className="dj-share">
            공유
            <span className="dj-seg" role="group" aria-label="공유 범위">
              {(Object.keys(SHARE_LABEL) as Share[]).map((k) => <button key={k} type="button" className={share === k ? 'on' : ''} aria-pressed={share === k} onClick={() => setShare(k)}>{SHARE_LABEL[k]}</button>)}
            </span>
            <small>{SHARE_LABEL[share]}: {SHARE_HELP[share]} · 남기면 '진행'으로 시작합니다</small>
          </span>
          <span className="dj-actions">
            <button type="button" className="dj-btn" onClick={() => setExpand(!expand)}>{expand ? '한 줄로 쓰기' : '크게 펼쳐 쓰기'}</button>
            <button type="button" className="dj-btn primary" disabled={!text.trim() || add.isPending || !target} onClick={submit}>남기기</button>
          </span>
        </div>
      </div>
      {add.error && <ErrorBox error={add.error} />}
      {saved && (
        <div className="jr-saved">
          <span>'{saved.text}'을(를) <b>개인</b>으로 남겼습니다. 다른 사람에게는 보이지 않습니다.</span>
          <button className="secondary" disabled={upShare.isPending} onClick={() => upShare.mutate(saved.id)}>전체 공유로 올리기</button>
        </div>
      )}
    </>
  )
}

/** 일지 목록(날짜별 묶음 · 공유 범위·상태 바꾸기 포함) */
function JournalTable({ items, showProject }: { items: JournalItem[]; showProject: boolean }) {
  const qc = useQueryClient()
  const put = useMutation({
    mutationFn: (v: { id: number; share?: Share; status?: 'run' | 'done' }) => api(`/api/journal/${v.id}`, { method: 'PUT', json: { share: v.share ?? null, status: v.status ?? null } }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['journal'] }),
  })
  const sorted = [...items].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))
  const days = [...new Map(sorted.map((x) => [dayKey(x.updatedAt), dayLabel(x.updatedAt)])).entries()]
  const cls = `dj-row${showProject ? ' with-project' : ''}`
  return (
    <>
      <div className="dj-table">
        <div className={`${cls} dj-head`}>
          <span>시각</span>{showProject && <span>프로젝트</span>}<span>한 일</span><span>단계</span><span>부서 · 작성자</span><span>공유</span><span>상태</span>
        </div>
        {sorted.length === 0 && <div className="dj-none">보이는 일지가 없습니다.</div>}
        {days.map(([key, label]) => (
          <Fragment key={key}>
            <div className="dj-day">{label}</div>
            {sorted.filter((x) => dayKey(x.updatedAt) === key).map((x) => (
              <div key={x.id} className={cls}>
                <span className="dj-num">{hm(x.updatedAt)}</span>
                {showProject && (
                  <Link className="dj-project-cell" to={`/projects/${x.projectId}`}>
                    <small>{x.projectCode}</small>
                    <span>{x.projectName}</span>
                  </Link>
                )}
                <span className="dj-body"><BodyCell text={x.body} /></span>
                <StageCell stage={x.stage} />
                <span className="dj-who">{x.department ?? '-'}<small>{x.authorName}{x.mine ? ' (나)' : ''}</small></span>
                <span>
                  {x.mine
                    ? <select className="dj-select" aria-label="공유 범위" value={x.share} disabled={put.isPending} onChange={(e) => put.mutate({ id: x.id, share: e.target.value as Share })}>
                        {(Object.keys(SHARE_LABEL) as Share[]).map((k) => <option key={k} value={k}>{SHARE_LABEL[k]}</option>)}
                      </select>
                    : SHARE_LABEL[x.share]}
                </span>
                <span>
                  {x.canStatus
                    ? (
                      <span className="dj-status" role="group" aria-label="상태">
                        {(['run', 'done'] as const).map((k) => (
                          <button key={k} type="button" className={`${k}${x.status === k ? ' on' : ''}`} aria-pressed={x.status === k} disabled={put.isPending}
                            onClick={() => x.status !== k && put.mutate({ id: x.id, status: k })}>{STATUS_LABEL[k]}</button>
                        ))}
                      </span>
                    )
                    : <span className={`dj-status-text ${x.status}`}>{STATUS_LABEL[x.status]}</span>}
                </span>
              </div>
            ))}
          </Fragment>
        ))}
      </div>
      {put.error && <ErrorBox error={put.error} />}
    </>
  )
}

/** 일지 탭·메뉴에 보이는 것: 내 것 + 같은 부서가 공유한 것 */
const inJournal = (x: JournalItem, dept?: string) => x.mine || (x.share !== 'me' && !!dept && x.department === dept)

/** 프로젝트 안 '프로젝트 진행 일지' 탭 */
export function ProjectJournalTab({ projectId, currentStageCode, isMember }: { projectId: number; currentStageCode?: string; isMember: boolean }) {
  const { user } = useAuth()
  const q = useQuery({ queryKey: ['journal', projectId], queryFn: () => api<JournalItem[]>(`/api/projects/${projectId}/journal`) })
  const items = (q.data ?? []).filter((x) => inJournal(x, user?.department))
  return (
    <div className="dj">
      <JournalWrite projectId={projectId} currentStageCode={currentStageCode} canWrite={isMember} />
      <div className="dj-list-head">
        <b>진행 일지 — 이 프로젝트, 나와 {user?.department}</b>
        <small>내가 쓴 것 전부 + 같은 부서의 부서·전체 공유. 공유 범위는 작성자만, 상태는 작성자와 같은 부서 사람이 바꿉니다.</small>
      </div>
      {q.isLoading ? <Loading /> : q.error ? <ErrorBox error={q.error} /> : <JournalTable items={items} showProject={false} />}
    </div>
  )
}

/** 메뉴 '진행 일지' — 여러 프로젝트를 한 화면에서 보고 쓴다 */
export function JournalPage() {
  const { user } = useAuth()
  const [filter, setFilter] = useState<number | 'all'>('all')
  const [status, setStatus] = useState<'all' | 'run' | 'done'>('all')
  const q = useQuery({ queryKey: ['journal', 'all'], queryFn: () => api<JournalItem[]>('/api/journal') })
  const all = (q.data ?? []).filter((x) => inJournal(x, user?.department))
  const projects = [...new Map(all.map((x) => [x.projectId, `${x.projectCode} ${x.projectName}`])).entries()]
  const items = all.filter((x) => (filter === 'all' || x.projectId === filter) && (status === 'all' || x.status === status))
  return (
    <>
      <div className="page-head">
        <div>
          <p className="eyebrow">PROJECT JOURNAL</p>
          <h1>진행 일지</h1>
          <p>여러 프로젝트의 일지를 한 화면에서 쓰고 봅니다. 프로젝트 하나만 볼 때는 프로젝트 화면의 '프로젝트 진행 일지' 탭을 쓰세요.</p>
        </div>
      </div>
      <div className="dj">
        <JournalWrite canWrite />
        <section className="dj-card">
          <div className="dj-toolbar">
            <span className="dj-toolbar-left">
              <b>일지</b>
              <span className="dj-scope">나와 {user?.department}</span>
              <span className="dj-seg" role="group" aria-label="상태">
                {([['all', '전체'], ['run', '진행'], ['done', '완료']] as const).map(([k, l]) => (
                  <button key={k} type="button" className={status === k ? 'on' : ''} aria-pressed={status === k} onClick={() => setStatus(k)}>{l}</button>
                ))}
              </span>
            </span>
            <label className="dj-filter">
              프로젝트
              <select value={filter} onChange={(e) => setFilter(e.target.value === 'all' ? 'all' : Number(e.target.value))}>
                <option value="all">모든 프로젝트</option>
                {projects.map(([id, name]) => <option key={id} value={id}>{name}</option>)}
              </select>
            </label>
          </div>
          {q.isLoading ? <Loading /> : q.error ? <ErrorBox error={q.error} /> : <JournalTable items={items} showProject />}
        </section>
      </div>
    </>
  )
}

/** 진행 단계 탭 아래: 전체 공유된 일지(모든 부서, 보기만) + 단계 변경 줄 */
export function ProgressJournalList({ projectId, changes, onWrite }: {
  projectId: number
  /** 단계 변경 이력(시각·내용) — 목록에 '단계 변경' 줄로 함께 보인다 */
  changes: { at: string; text: string; stage?: string }[]
  onWrite?: () => void
}) {
  const [byDept, setByDept] = useState(true)
  const q = useQuery({ queryKey: ['journal', projectId], queryFn: () => api<JournalItem[]>(`/api/projects/${projectId}/journal`) })
  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  const work = (q.data ?? []).filter((x) => x.share === 'project')
  const row = (x: JournalItem) => (
    <div key={x.id} className="dj-row progress">
      <span className="dj-num">{updated(x.updatedAt)}</span>
      <span className="dj-who">{x.department ?? '-'}<small>{x.authorName}{x.mine ? ' (나)' : ''}</small></span>
      <span className="dj-body"><BodyCell text={x.body} /></span>
      <StageCell stage={x.stage} />
      <span className={`dj-status-text ${x.status}`}>{STATUS_LABEL[x.status]}</span>
    </div>
  )
  const sysRows = changes.map((c, i) => (
    <div key={`s${i}`} className="dj-row progress sys">
      <span className="dj-num">{updated(c.at)}</span>
      <span className="dj-who">단계 변경</span>
      <span className="dj-body">{c.text}</span>
      <StageCell stage={c.stage} />
      <span className="dj-dim">—</span>
    </div>
  ))
  let body: React.ReactNode
  if (byDept) {
    const depts = [...new Set(work.map((x) => x.department ?? '부서 없음'))]
    body = (
      <>
        {depts.map((d) => {
          const its = work.filter((x) => (x.department ?? '부서 없음') === d)
          return (
            <Fragment key={`g${d}`}>
              <div className="dj-day">{d} — 진행 {its.filter((x) => x.status === 'run').length} · 완료 {its.filter((x) => x.status === 'done').length}</div>
              {its.map(row)}
            </Fragment>
          )
        })}
        {sysRows.length > 0 && <div className="dj-day">단계 변경</div>}
        {sysRows}
      </>
    )
  } else {
    body = <>{work.map(row)}{sysRows}</>
  }
  return (
    <div className="dj">
      <div className="dj-list-head">
        <b>진행 목록 — 전체 공유된 일지(모든 부서)</b>
        <label className="dj-check"><input type="checkbox" checked={byDept} onChange={(e) => setByDept(e.target.checked)} /> 부서별로 묶기</label>
        <small>이 목록에서는 상태를 바꾸지 않습니다. 작성자가 '프로젝트 진행 일지'에서 바꿉니다.</small>
      </div>
      <div className="dj-table">
        <div className="dj-row progress dj-head"><span>업데이트</span><span>부서 · 작성자</span><span>한 일</span><span>단계</span><span>상태</span></div>
        {work.length === 0 && changes.length === 0 ? <div className="dj-none">전체 공유된 일지가 없습니다.</div> : body}
      </div>
      {onWrite && (
        <div className="dj-write-link">
          <button className="dj-btn" onClick={onWrite}><Icon name="journal" size={16} />프로젝트 진행 일지 쓰러 가기</button>
        </div>
      )}
    </div>
  )
}
