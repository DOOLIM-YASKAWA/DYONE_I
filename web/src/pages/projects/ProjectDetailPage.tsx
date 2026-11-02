import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api } from '../../api/client'
import { useAuth, useIsPartner } from '../../auth/AuthContext'
import type { Code, JournalItem, ProgressStatus, ProjectDetail, ProjectFile, StageHistoryItem } from '../../api/types'
import { ErrorBox, GwEndBadge, Loading } from '../../components/ui'
import { ProjectFilesTab } from './ProjectFiles'
import { ProjectMembersTab } from './ProjectMembers'
import { ProgressJournalList, ProjectJournalTab } from './ProjectJournal'
import { useProjectMembers } from './ProjectMembers'
import { RelatedWork } from './RelatedWork'
import { AgentPanel } from '../../ai/AgentPanel'
import '../../styles/project-detail.css'

const dateTime = (v?: string) => (v ? new Date(v).toLocaleString('ko-KR', { dateStyle: 'medium', timeStyle: 'short' }) : '-')
const STATUS_LABEL: Record<ProgressStatus, string> = { wait: '대기', run: '진행', done: '완료' }
const TABS = ['프로젝트 자료함', '프로젝트 인원', '프로젝트 진행 단계', '프로젝트 진행 일지', '관련 업무'] as const
type Tab = (typeof TABS)[number]
/** 협력사에게 숨기는 탭(관련 업무 = 사내 연계 앱, 진행 일지 = 사내 일지) */
const PARTNER_HIDDEN: readonly Tab[] = ['프로젝트 진행 일지', '관련 업무']

/**
 * 프로젝트 전용 풀페이지 상세. 머리 카드(오른쪽에 지금 단계 "모듈라 진행") + 탭 5개.
 * 프로젝트 자료함 | 프로젝트 인원 | 프로젝트 진행 단계 | 프로젝트 진행 일지 | 관련 업무
 * - 진행 단계: 단계별 대기·진행·완료(영업·수주는 그룹웨어) + 변경 이력
 * - 관련 업무: 보는 사람 부서의 업무 기능만(PM은 전부)
 */
export function ProjectDetailPage() {
  const { id } = useParams()
  const pid = Number(id)
  const qc = useQueryClient()
  const partner = useIsPartner() // 협력사: 보기만(진행 상황 변경·관련 업무·진행 일지 숨김)
  const [tab, setTab] = useState<Tab>('프로젝트 자료함')
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
  const { user } = useAuth()
  const members = useProjectMembers(pid)
  const files = useQuery({ queryKey: ['project-files', pid], queryFn: () => api<ProjectFile[]>(`/api/projects/${pid}/files`), enabled: !Number.isNaN(pid) })
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
  // 단계마다 대기·진행·완료를 따로 가진다(여러 단계 동시 진행 가능). 대표 단계 = 서버가 목록과 같은 규칙으로 계산한 값
  const progress = data.progress ?? []
  const list: Code[] = progress.map((s) => ({ id: s.stageId, code: s.code, name: s.name }))
  const statusOf = (i: number): ProgressStatus => progress[i]?.status ?? 'wait'
  const curIdx = progress.findIndex((s) => s.stageId === data.currentStageId)
  // 머리 카드 오른쪽: "모듈라 진행"처럼 단계 이름 + 상태만
  const headStage = curIdx >= 0 ? `${list[curIdx].name} ${STATUS_LABEL[statusOf(curIdx)]}` : '단계 없음'
  const count: Partial<Record<Tab, string | number>> = {
    '프로젝트 자료함': files.data?.length ?? '',
    '프로젝트 인원': members.data?.length ?? '',
  }
  // 단계(③~⑥)는 PM이 바꾼다. PM이 아직 없으면 누구나(지정되면 PM만)
  const myId = Number(user?.id)
  const hasPm = (members.data ?? []).some((m) => m.role === 'pm')
  const isPm = (members.data ?? []).some((m) => m.role === 'pm' && m.employeeId === myId)
  const isMember = (members.data ?? []).some((m) => m.employeeId === myId)
  const canStage = !partner && (isPm || !hasPm)
  // 진행 목록에 같이 보이는 '단계 변경' 줄
  const changes = (history.data ?? []).map((h) => ({
    at: h.changedAt,
    stage: h.toStage ?? undefined,
    text: h.source === 'groupware'
      ? `그룹웨어 상태 ${h.gwFrom ?? '없음'} → ${h.gwTo ?? '없음'}`
      : `${h.changedByName ?? '알 수 없음'}${h.changedByDept ? `(${h.changedByDept})` : ''}: ${h.toStage ?? ''} ${h.fromStatus ? STATUS_LABEL[h.fromStatus as ProgressStatus] ?? '' : ''} → ${h.toStatus ? STATUS_LABEL[h.toStatus as ProgressStatus] ?? '' : ''}${h.note ? ` · ${h.note}` : ''}`,
  }))

  return (
    <>
      <Link to="/projects" className="text-button" style={{ display: 'inline-flex', gap: 6, marginBottom: 10, padding: 0 }}>← 프로젝트 목록</Link>
      <div className="scope-card">
        <div>
          <small>{p.code} · {p.partnerName ?? '발주처 미지정'} · {p.type ?? '종류 미지정'}</small>
          <h2>{p.name}</h2>
          <button className="scope-pm" onClick={() => setTab('프로젝트 인원')} title="프로젝트 인원 보기·변경">
            PM <b>{p.pmName ?? '미지정'}</b> · 인원 보기 →
          </button>
        </div>
        <button className="scope-stage" onClick={() => setTab('프로젝트 진행 단계')} title="진행 단계 보기·변경">{headStage}</button>
      </div>
      {(p.status === '마감' || p.status === '중단') && (
        <div className={`gw-end-note ${p.status === '중단' ? 'stop' : ''}`}>
          <GwEndBadge status={p.status} /> 그룹웨어에서 <b>{p.status}</b>된 프로젝트입니다.{data.dyoneStage && ' 아래 진행 상황은 DY ONE에서 관리한 값이라 그대로 남아 있습니다.'}
        </div>
      )}

      <section className="panel project-main">
        <div className="project-tabs" role="tablist" aria-label="프로젝트 섹션">
          {TABS.filter((t) => !partner || !PARTNER_HIDDEN.includes(t)).map((t) => (
            <button key={t} role="tab" aria-selected={t === tab} className={t === tab ? 'active' : ''} onClick={() => setTab(t)}>
              {t}{count[t] !== undefined && count[t] !== '' && <small>{count[t]}</small>}
            </button>
          ))}
        </div>
        <div className="panel-body">
          {tab === '프로젝트 자료함' && <ProjectFilesTab projectId={pid} />}
          {/* 자료함 도우미(사내 AI): 이 프로젝트 자료 목록을 보고 답한다 */}
          {tab === '프로젝트 자료함' && !partner && (
            <AgentPanel agent="project-docs" title="자료함 도우미" placeholder="자료에 대해 물어보세요"
              context={() => [
                `프로젝트: ${p.code} ${p.name} (${p.partnerName ?? '-'}) · 지금 단계: ${headStage}`,
                `자료 ${files.data?.length ?? 0}건 (종류 | 단계 | 파일 | 폴더 | 올린 사람 | 날짜)`,
                ...(files.data ?? []).slice(0, 400).map((f) => [f.category ?? '-', f.stage ?? '-', f.fileName, f.relPath ?? '', `${f.department ?? ''} ${f.uploadedByName ?? ''}`, f.uploadedAt.slice(0, 10)].join(' | ')),
              ].join('\n')}
              actions={[
                { label: '자료 현황 요약', prompt: '이 프로젝트 자료를 종류별로 요약하고 최근 올라온 것을 알려줘' },
                { label: '빠진 자료 짚기', prompt: '지금 단계에서 있어야 하는데 안 보이는 자료가 있는지 짚어줘' },
                { label: '종류가 이상한 자료', prompt: '종류가 잘못 붙은 것 같은 자료가 있으면 알려줘' },
              ]}
              examples={['IP 리스트 어디 있어?', '현장 백업 최근 거 언제 올라왔어?', '사양서 최신 판이 뭐야?']} />
          )}
          {tab === '프로젝트 인원' && <ProjectMembersTab projectId={pid} />}
          {tab === '프로젝트 진행 단계' && (
            <div className="stage-panel">
              <p className="dj-stage-note">
                ① 영업·② 수주는 그룹웨어 상태를 따르고, ③~⑥은 PM이 아래 목록을 보고 바꿉니다.
                {!hasPm && ' PM이 지정되지 않아 지금은 누구나 바꿀 수 있습니다.'}
                {data.dyoneStage
                  ? <> · 마지막 변경: <b>{data.dyoneStage.updatedByName ?? '알 수 없음'}</b>{data.dyoneStage.updatedByDept && ` (${data.dyoneStage.updatedByDept})`} · {dateTime(data.dyoneStage.updatedAt)}</>
                  : ' · 아직 DY ONE에서 바꾼 적 없음(그룹웨어 상태 기준)'}
              </p>
              {/* D안: 6단계 큰 카드 — 진행 = 노랑 테두리·번호, 완료 = 파랑, 대기 = 회색, ①② 그룹웨어 출처 표시 */}
              <div className="dj-stages">
                {list.map((s, i) => {
                  const st = statusOf(i)
                  const label = `${s.name}`
                  const gw = progress[i]?.fromGroupware
                  return (
                    <div key={s.id} className={`dj-step ${st}`}>
                      <div className="dj-step-top">
                        <span className="dj-step-num">{i + 1}</span>
                        {gw && <span className="dj-step-src">그룹웨어</span>}
                      </div>
                      <b className="dj-step-name">{s.name}</b>
                      {gw || !canStage ? (
                        // 영업·수주: 그룹웨어 상태로 자동 표시(바꾸지 않음) · 협력사는 보기만
                        <span className="dj-step-state" title={gw ? '그룹웨어 프로젝트 상태를 따릅니다' : 'PM이 바꿉니다'}>
                          {STATUS_LABEL[st]}
                        </span>
                      ) : (
                        <button
                          className="dj-step-btn"
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
                const label = list[i].name
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
              <PmDoneNote projectId={pid} show={isPm} lastChange={history.data?.find((h) => h.source !== 'groupware')?.changedAt} />
              <ProgressJournalList projectId={pid} changes={changes} onWrite={partner || !isMember ? undefined : () => setTab('프로젝트 진행 일지')} />
            </div>
          )}
          {tab === '프로젝트 진행 일지' && !partner && <ProjectJournalTab projectId={pid} currentStageCode={curIdx >= 0 ? list[curIdx].code : undefined} isMember={isMember} />}
          {tab === '관련 업무' && !partner && <RelatedWork projectId={pid} projectCode={p.code} pmName={p.pmName} stages={list} />}
        </div>
      </section>
    </>
  )
}

/** PM에게: 마지막 단계 변경 뒤로 프로젝트 공유 일지가 '완료'된 것 — 단계를 바꿀지 확인 */
function PmDoneNote({ projectId, show, lastChange }: { projectId: number; show: boolean; lastChange?: string }) {
  const q = useQuery({ queryKey: ['journal', projectId], queryFn: () => api<JournalItem[]>(`/api/projects/${projectId}/journal`), enabled: show })
  if (!show) return null
  const done = (q.data ?? []).filter((x) => x.share === 'project' && x.status === 'done' && (!lastChange || x.updatedAt > lastChange))
  if (done.length === 0) return null
  return (
    <div className="stage-pm-note">
      <span style={{ flex: 1 }}>마지막 단계 변경 뒤로 완료된 일 {done.length}건: {done.map((x) => `${x.department ?? ''} ${x.authorName ?? ''} — ${x.body.split('\n')[0]}`).join(' / ')}</span>
      <span className="small">단계를 바꿀지 확인하세요</span>
    </div>
  )
}
