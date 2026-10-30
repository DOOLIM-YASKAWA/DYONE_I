import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api } from '../../api/client'
import { useIsPartner } from '../../auth/AuthContext'
import type { Code, ProgressStatus, ProjectDetail, StageHistoryItem } from '../../api/types'
import { ErrorBox, GwEndBadge, Loading } from '../../components/ui'
import { ProjectFilesTab } from './ProjectFiles'
import { ProjectMembersTab } from './ProjectMembers'
import { STAGE_FEATURE, StageFeature } from './StageFeature'
import '../../styles/project-detail.css'

const dateTime = (v?: string) => (v ? new Date(v).toLocaleString('ko-KR', { dateStyle: 'medium', timeStyle: 'short' }) : '-')
const STATUS_LABEL: Record<ProgressStatus, string> = { wait: '대기', run: '진행', done: '완료' }
const TABS = ['프로젝트 인원', '프로젝트 진행 상황 이력', '프로젝트 파일'] as const
type Tab = (typeof TABS)[number]

/**
 * 프로젝트 전용 풀페이지 상세. 상단 진행 단계바(S1~S9) + 단계 상세 + 섹션 탭.
 * - 단계 클릭 = 그 단계의 관련 문서·업무 보기(단계는 안 바뀜)
 * - 단계 변경 = 우측 '진행 상황 변경' 버튼으로만
 */
export function ProjectDetailPage() {
  const { id } = useParams()
  const pid = Number(id)
  const qc = useQueryClient()
  const partner = useIsPartner() // 협력사: 보기만(진행 상황 변경·단계 업무 기능 숨김)
  const [tab, setTab] = useState<Tab>('프로젝트 인원')
  // 프로젝트 섹션(인원·진행 상황 이력·파일, 진행 상황보다 위)은 접어 두고, 관련 버튼을 누르면 펼치기만 한다(스크롤은 하지 않음)
  const [sectionOpen, setSectionOpen] = useState(false)
  const openTab = (t: Tab) => {
    setTab(t)
    setSectionOpen(true)
  }
  const [viewIdx, setViewIdx] = useState<number | null>(null) // 보고 있는 단계(null=현재 단계)
  const [menuIdx, setMenuIdx] = useState<number | null>(null) // 상태 메뉴를 연 단계
  const [menuPos, setMenuPos] = useState<{ top: number; left: number } | null>(null) // 메뉴 위치(화면 기준) — 단계바가 가로 스크롤돼도 잘리지 않게
  const [note, setNote] = useState('')
  const [last, setLast] = useState<{ stageId: number; label: string; from: ProgressStatus; to: ProgressStatus } | null>(null) // 방금 바꾼 것(되돌리기용)

  const { data, isLoading, error } = useQuery({
    queryKey: ['project', pid],
    queryFn: () => api<ProjectDetail>(`/api/projects/${pid}`),
    enabled: !Number.isNaN(pid),
  })
  const history = useQuery({
    queryKey: ['project-stage-history', pid],
    queryFn: () => api<StageHistoryItem[]>(`/api/projects/${pid}/stage-history`),
    enabled: !Number.isNaN(pid),
  })
  const save = useMutation({
    mutationFn: (v: { stageId: number; label: string; from: ProgressStatus; to: ProgressStatus; note?: string; undo?: boolean }) =>
      api(`/api/projects/${pid}/progress`, { method: 'PUT', json: { changes: [{ stageId: v.stageId, status: v.to }], note: v.note || null } }),
    onSuccess: (_r, v) => {
      setMenuIdx(null)
      setNote('')
      setLast(v.undo ? null : { stageId: v.stageId, label: v.label, from: v.from, to: v.to })
      qc.invalidateQueries({ queryKey: ['project-stage-history', pid] })
      qc.invalidateQueries({ queryKey: ['project', pid] })
      qc.invalidateQueries({ queryKey: ['home'] })
      qc.invalidateQueries({ queryKey: ['projects'] })
    },
  })

  // 상태 메뉴: 바깥을 누르거나 Esc면 닫는다 · 방금 바꾼 안내(되돌리기)는 8초 뒤 사라진다
  useEffect(() => {
    if (menuIdx === null) return
    const close = (e: MouseEvent | KeyboardEvent) => {
      if (e instanceof KeyboardEvent ? e.key === 'Escape' : !(e.target as Element).closest('.s-menu, .s-state-btn')) setMenuIdx(null)
    }
    // 스크롤·창 크기 변경 시 위치가 어긋나므로 닫는다(연 직후 버튼에 포커스가 가며 생기는 작은 스크롤은 무시)
    const openedAt = Date.now()
    const hide = () => { if (Date.now() - openedAt > 400) setMenuIdx(null) }
    document.addEventListener('mousedown', close)
    document.addEventListener('keydown', close)
    window.addEventListener('scroll', hide, true)
    window.addEventListener('resize', hide)
    return () => {
      document.removeEventListener('mousedown', close)
      document.removeEventListener('keydown', close)
      window.removeEventListener('scroll', hide, true)
      window.removeEventListener('resize', hide)
    }
  }, [menuIdx])
  useEffect(() => {
    if (!last) return
    const t = setTimeout(() => setLast(null), 8000)
    return () => clearTimeout(t)
  }, [last])

  if (isLoading) return <Loading />
  if (error || !data) return <ErrorBox error={error} />
  const p = data.project
  // 단계마다 대기·진행·완료를 따로 가진다(여러 단계 동시 진행 가능). 대표 단계 = 진행 중 가장 뒤, 없으면 완료 중 가장 뒤
  const progress = data.progress ?? []
  const list: Code[] = progress.map((s) => ({ id: s.stageId, code: s.code, name: s.name }))
  const statusOf = (i: number): ProgressStatus => progress[i]?.status ?? 'wait'
  // 대표(현재) 단계: 서버가 목록과 같은 규칙으로 계산한 값
  const curIdx = progress.findIndex((s) => s.stageId === data.currentStageId)
  const shownIdx = viewIdx ?? (curIdx >= 0 ? curIdx : 0)
  const viewed = list[shownIdx]
  const relation = { run: '진행 중인 단계', done: '완료한 단계', wait: '대기 중인 단계' }[statusOf(shownIdx)]

  return (
    <>
      <Link to="/projects" className="text-button" style={{ display: 'inline-flex', gap: 6, marginBottom: 10, padding: 0 }}>← 프로젝트 목록</Link>
      <div className="scope-card">
        <div>
          <small>{p.code} · {p.partnerName ?? '발주처 미지정'} · {p.type ?? '종류 미지정'}</small>
          <h2>{p.name}</h2>
          <button className="scope-pm" onClick={() => openTab('프로젝트 인원')} title="프로젝트 인원 보기·변경">
            PM <b>{p.pmName ?? '미지정'}</b> · 인원 보기 →
          </button>
        </div>
      </div>
      {(p.status === '마감' || p.status === '중단') && (
        <div className={`gw-end-note ${p.status === '중단' ? 'stop' : ''}`}>
          <GwEndBadge status={p.status} /> 그룹웨어에서 <b>{p.status}</b>된 프로젝트입니다.{data.dyoneStage && ' 아래 진행 상황은 DY ONE에서 관리한 값이라 그대로 남아 있습니다.'}
        </div>
      )}

      {/* 프로젝트 인원·진행 상황 이력·파일 — 맨 위 프로젝트 카드 바로 아래, 진행 상황보다 위에 둔다 */}
      <div className={`project-sections${sectionOpen ? ' open' : ''}`}>
        <div className="project-tabs" role="tablist" aria-label="프로젝트 섹션">
          {TABS.map((t) => (
            <button key={t} role="tab" aria-selected={sectionOpen && t === tab} className={sectionOpen && t === tab ? 'active' : ''}
              onClick={() => (sectionOpen && t === tab ? setSectionOpen(false) : openTab(t))}>{t}</button>
          ))}
          <button className="section-toggle" aria-expanded={sectionOpen} onClick={() => (sectionOpen ? setSectionOpen(false) : openTab(tab))}>
            {sectionOpen ? '접기 ▴' : '펼치기 ▾'}
          </button>
        </div>

        {sectionOpen && (
          <section className="panel">
            <div className="panel-body" style={{ paddingTop: 18 }}>
              {tab === '프로젝트 인원' && <ProjectMembersTab projectId={pid} />}
              {tab === '프로젝트 진행 상황 이력' && <StageHistoryList query={history} stages={list} />}
              {/* 전체 문서함: 올리면 '현재 단계'로 기록 */}
              {tab === '프로젝트 파일' && <ProjectFilesTab projectId={pid} stage={list[curIdx]} />}
            </div>
          </section>
        )}
      </div>

      <section className="panel stage-panel" style={{ marginBottom: 16 }}>
        <div className="panel-head">
          <div>
            <h2>예상 현재 진행 상황</h2>
            <p>
              단계를 누르면 관련 문서·업무를, 아래 상태를 누르면 진행 상황을 바꿉니다(영업·수주는 그룹웨어 상태를 따릅니다).
              {data.dyoneStage ? (
                <>
                  {' · 진행 상황 변경: '}
                  <b>{data.dyoneStage.updatedByName ?? '알 수 없음'}</b>
                  {data.dyoneStage.updatedByDept && ` (${data.dyoneStage.updatedByDept})`} · {dateTime(data.dyoneStage.updatedAt)}{' '}
                  <button className="text-button" style={{ padding: 0 }} onClick={() => openTab('프로젝트 진행 상황 이력')}>변경 이력 보기 →</button>
                </>
              ) : (
                ' · 아직 DY ONE에서 바꾼 적 없음(그룹웨어 상태 기준)'
              )}
            </p>
          </div>
        </div>
        <div className="panel-body" style={{ paddingTop: 6 }}>
          <div className="stage-flow">
            {list.map((s, i) => {
              const st = statusOf(i)
              const label = `S${i + 1} ${s.name}`
              return (
                <div key={s.id} className={`stage-step ${st === 'run' ? 'current' : st === 'done' ? 'done' : 'todo'}${i === shownIdx ? ' selected' : ''}`}>
                  {/* 단계(이름·점)를 누르면 그 단계 문서·업무 보기, 아래 상태 글자를 누르면 상태 바꾸기 */}
                  <button className="s-view" onClick={() => setViewIdx(i)} aria-pressed={i === shownIdx}>
                    <div className="s-num">S{i + 1}</div>
                    <div className="s-name">{s.name}</div>
                    <div className="s-marker"><span className="dot" /></div>
                  </button>
                  {progress[i]?.fromGroupware || partner ? (
                    // 영업·수주: 그룹웨어 상태로 자동 표시(바꾸지 않음) · 협력사는 보기만
                    <span className="s-state s-state-gw" title={partner ? undefined : '그룹웨어 프로젝트 상태를 따릅니다'}>
                      {STATUS_LABEL[st]}
                      {progress[i]?.fromGroupware && <small>그룹웨어</small>}
                    </span>
                  ) : (
                  <button
                    className="s-state s-state-btn"
                    aria-haspopup="menu"
                    aria-expanded={menuIdx === i}
                    aria-label={`${label} 진행 상황: ${STATUS_LABEL[st]} — 눌러서 변경`}
                    title="눌러서 진행 상황 변경"
                    onClick={(e) => {
                      const r = e.currentTarget.getBoundingClientRect()
                      setMenuPos({ top: r.bottom + 4, left: r.left + r.width / 2 })
                      setMenuIdx(menuIdx === i ? null : i)
                      setNote('')
                    }}
                  >
                    {STATUS_LABEL[st]}
                  </button>
                  )}
                </div>
              )
            })}
          </div>
          {menuIdx !== null && menuPos && list[menuIdx] && (() => {
            const i = menuIdx
            const st = statusOf(i)
            const label = `S${i + 1} ${list[i].name}`
            return (
              <div className="s-menu" role="menu" aria-label={`${label} 진행 상황`} style={{ position: 'fixed', top: menuPos.top, left: menuPos.left }}>
                {(['wait', 'run', 'done'] as ProgressStatus[]).map((k) => (
                  <button
                    key={k}
                    role="menuitemradio"
                    aria-checked={k === st}
                    className={k === st ? 'on' : ''}
                    disabled={save.isPending}
                    onClick={() => (k === st ? setMenuIdx(null) : save.mutate({ stageId: list[i].id, label, from: st, to: k, note: note.trim() }))}
                  >
                    {k === st ? '✓ ' : ''}{STATUS_LABEL[k]}
                    {st === 'done' && k === 'run' && <small> 재작업</small>}
                  </button>
                ))}
                <input value={note} onChange={(e) => setNote(e.target.value)} maxLength={500} placeholder="사유(선택)" aria-label="변경 사유" />
              </div>
            )
          })()}
          {last && (
            <p className="stage-last">
              {last.label}: {STATUS_LABEL[last.from]} → {STATUS_LABEL[last.to]}{last.from === 'done' && last.to === 'run' ? ' (재작업)' : ''} 저장했습니다.{' '}
              <button className="text-button" style={{ padding: 0 }} disabled={save.isPending}
                onClick={() => save.mutate({ stageId: last.stageId, label: last.label, from: last.to, to: last.from, note: '되돌리기', undo: true })}>
                되돌리기
              </button>
            </p>
          )}
          {save.error && <ErrorBox error={save.error} />}
        </div>
      </section>

      {/* 선택한 단계의 업무 기능(문서 업로드는 위 '프로젝트 파일' 탭에서) */}
      {viewed && !partner && (
        <section className="panel stage-feature-panel" style={{ marginBottom: 16 }}>
          <div className="panel-head">
            <div>
              <h2>S{shownIdx + 1} {viewed.name}</h2>
              <p>{relation} · {STAGE_FEATURE[viewed.code]?.title ?? '이 단계의 업무'}</p>
            </div>
            {shownIdx !== curIdx && curIdx >= 0 && (
              <button className="text-button" onClick={() => setViewIdx(null)}>현재 단계로 →</button>
            )}
          </div>
          <div className="panel-body">
            <StageFeature key={viewed.id} stageCode={viewed.code} stage={viewed} projectId={pid} projectCode={p.code} onOpenFiles={() => openTab('프로젝트 파일')} />
          </div>
        </section>
      )}
    </>
  )
}

const statusText = (s?: string) => (s && s in STATUS_LABEL ? STATUS_LABEL[s as ProgressStatus] : '-')
const statusBadge = (s?: string) => (s === 'run' ? 'blue' : s === 'done' ? 'green' : '')

/** 진행 상황 이력: 누가(부서·직위) · 언제 · 어떤 단계가 어떤 상태로(또는 예전 방식의 단계 이동) · 사유 */
function StageHistoryList({ query, stages }: { query: { data?: StageHistoryItem[]; isLoading: boolean; error: unknown }; stages: Code[] }) {
  if (query.isLoading) return <Loading />
  if (query.error) return <ErrorBox error={query.error} />
  const items = query.data ?? []
  if (items.length === 0) return <div className="empty" style={{ padding: 40 }}>아직 DY ONE에서 단계를 바꾼 기록이 없습니다. 지금 단계는 그룹웨어 상태(영업중·수주)를 기준으로 보여 줍니다.</div>
  const label = (id?: number, name?: string) => {
    if (id == null || !name) return null
    const i = stages.findIndex((s) => s.id === id)
    return i >= 0 ? `S${i + 1} ${name}` : name
  }
  return (
    <ol className="stage-history">
      {items.map((h) => {
        const from = label(h.fromStageId, h.fromStage)
        const isStatus = !!h.toStatus
        return (
          <li key={h.id}>
            <span className="dot" />
            <div>
              {h.source === 'groupware' ? (
                // 그룹웨어 상태 동기화: '그룹웨어 상태  영업중 → 수주'
                <div className="sh-change">
                  <b className="sh-stage">그룹웨어 상태</b>
                  <span className="badge">{h.gwFrom ?? '없음'}</span>
                  <span className="sh-arrow">→</span>
                  <span className="badge blue">{h.gwTo ?? '없음'}</span>
                </div>
              ) : isStatus ? (
                // 단계별 진행 상황 변경: 'S4 설계·구매  완료 → 진행 · 재작업'
                <div className="sh-change">
                  <b className="sh-stage">{label(h.toStageId, h.toStage)}</b>
                  <span className={`badge ${statusBadge(h.fromStatus)}`}>{statusText(h.fromStatus)}</span>
                  <span className="sh-arrow">→</span>
                  <span className={`badge ${statusBadge(h.toStatus)}`}>{statusText(h.toStatus)}</span>
                  {h.fromStatus === 'done' && h.toStatus === 'run' && <span className="badge amber">재작업</span>}
                </div>
              ) : (
                <div className="sh-change">
                  {from ? <><span className="badge">{from}</span><span className="sh-arrow">→</span></> : <span className="sh-first">처음 지정</span>}
                  <span className="badge blue">{label(h.toStageId, h.toStage)}</span>
                </div>
              )}
              {h.note && <p className="sh-note">{h.note}</p>}
              <small>
                {h.source === 'groupware' ? '그룹웨어 동기화(매일 08:00)' : h.changedByName ?? '알 수 없음'}
                {h.changedByPosition && ` ${h.changedByPosition}`}
                {h.changedByDept && ` · ${h.changedByDept}`} · {dateTime(h.changedAt)}
              </small>
            </div>
          </li>
        )
      })}
    </ol>
  )
}
