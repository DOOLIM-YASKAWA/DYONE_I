import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { api } from '../../api/client'
import type {
  ModularBuild, ModularDetail, ModularFile, ModularGanttItem, ModularHistoryItem, ModularList, ModularListKey, ModularMeta, ModularRole, ModularStatus,
} from '../../api/types'
import { DataTable, ErrorBox, Loading, Modal } from '../../components/ui'
import { useToast } from '../../components/Toast'

/**
 * 모듈라빌드 프로젝트 정보 — 그룹웨어 WORKS applet 376 화면을 그대로 옮긴 것.
 * 목록(그룹웨어 기본 보기 20열) · 간트(설치 시작 ~ 출하 예정일) · 상세(입력 폼 3구역 + 상태 전환 + 활동 기록).
 * 두 벌 관리: 화면 값은 DY ONE 값이다. 그룹웨어에서 바뀐 칸은 동기화 때 DY ONE 값에 반영되고,
 * DY ONE에서 고친 값은 그룹웨어로 보내지 않는다(그룹웨어가 그 칸을 바꾸면 그룹웨어 값으로 덮인다).
 * 'DY ONE 관리 전환'한 건은 그룹웨어 변경을 더 이상 받지 않는다.
 */

const PAGE_SIZE = 50
const MONTH_PX = 56
const day = (v?: string) => (v ? v.slice(0, 10) : '-')
const list = (v: string[]) => (v.length ? v.join(', ') : '-')
const when = (v?: string) => (v ? new Date(v).toLocaleString('ko-KR', { dateStyle: 'short', timeStyle: 'short' }) : '')
const bytes = (n?: number) =>
  n === undefined ? '' : n >= 1 << 20 ? `${(n / (1 << 20)).toFixed(1)} MB` : n >= 1024 ? `${Math.round(n / 1024)} KB` : `${n} B`

/** 상태 이름으로 배지 색을 정한다(그룹웨어 색 코드는 번호라 의미가 없다). */
function statusTone(name?: string) {
  if (!name) return ''
  if (/완료/.test(name)) return 'green'
  if (/지연/.test(name)) return 'red'
  if (/홀딩|잔여|준비/.test(name)) return 'amber'
  if (/문서/.test(name)) return ''
  return 'blue'
}

const StatusBadge = ({ status }: { status?: ModularStatus | null }) =>
  status ? <span className={`badge ${statusTone(status.name)}`}>{status.name}</span> : <span className="badge">상태 없음</span>

const useMeta = () => useQuery({ queryKey: ['modular-meta'], queryFn: () => api<ModularMeta>('/api/modular/meta'), staleTime: 5 * 60_000 })

export function ModularSection({ projectCode, openDocId = null, onDocClosed }: { projectCode: string | null; openDocId?: number | null; onDocClosed?(): void }) {
  const meta = useMeta()
  const [view, setView] = useState<'list' | 'gantt'>('list')
  const [year, setYear] = useState('')
  const [statusId, setStatusId] = useState('')
  const [category, setCategory] = useState('')
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(1)
  const [openId, setOpenId] = useState<number | null>(openDocId)
  // 알림을 눌러 같은 화면에서 다른 문서로 오면(주소의 doc만 바뀜) 그 문서를 연다
  const [seenDoc, setSeenDoc] = useState(openDocId)
  if (openDocId !== seenDoc) {
    setSeenDoc(openDocId)
    if (openDocId !== null) setOpenId(openDocId)
  }
  const closeDoc = () => {
    setOpenId(null)
    onDocClosed?.()
  }
  const [creating, setCreating] = useState(false)

  const params = new URLSearchParams()
  if (year) params.set('year', year)
  if (statusId) params.set('statusId', statusId)
  if (category) params.set('category', category)
  if (search) params.set('q', search)
  if (projectCode) params.set('projectCode', projectCode)

  const builds = useQuery({
    queryKey: ['modular-builds', params.toString(), page],
    queryFn: () => api<ModularList>(`/api/modular/builds?${params}&page=${page}&pageSize=${PAGE_SIZE}`),
    enabled: view === 'list',
    placeholderData: keepPreviousData,
  })
  const gantt = useQuery({
    queryKey: ['modular-gantt', params.toString()],
    queryFn: () => api<ModularGanttItem[]>(`/api/modular/gantt?${params}`),
    enabled: view === 'gantt',
  })

  const reset = (fn: () => void) => {
    fn()
    setPage(1)
  }
  const counts = new Map((builds.data?.statusCounts ?? []).map((c) => [c.statusId ?? 0, c.count]))
  const allCount = [...counts.values()].reduce((a, b) => a + b, 0)

  return (
    <div className="modular">
      <div className="modular-toolbar">
        <div className="segmented" role="tablist" aria-label="보기">
          <button role="tab" aria-selected={view === 'list'} className={view === 'list' ? 'active' : ''} onClick={() => setView('list')}>목록</button>
          <button role="tab" aria-selected={view === 'gantt'} className={view === 'gantt' ? 'active' : ''} onClick={() => setView('gantt')}>간트</button>
        </div>
        <input
          className="search-input"
          placeholder="코드·프로젝트명·고객사·공장·공정"
          defaultValue={search}
          aria-label="모듈라 검색"
          onKeyDown={(e) => e.key === 'Enter' && reset(() => setSearch(e.currentTarget.value.trim()))}
        />
        <select aria-label="연도" value={year} onChange={(e) => reset(() => setYear(e.target.value))}>
          <option value="">전체 연도</option>
          {builds.data?.years.map((y) => <option key={y} value={y}>{y}</option>)}
        </select>
        <select aria-label="구분" value={category} onChange={(e) => reset(() => setCategory(e.target.value))}>
          <option value="">전체 구분</option>
          {meta.data?.options.category?.map((o) => <option key={o.code} value={o.code}>{o.label}</option>)}
        </select>
        {meta.data?.editEnabled && (
          <button className="primary" onClick={() => setCreating(true)}>+ 모듈라 등록</button>
        )}
      </div>

      {meta.data && view === 'list' && (
        <div className="status-chips" role="group" aria-label="상태">
          <button className={statusId === '' ? 'active' : ''} onClick={() => reset(() => setStatusId(''))}>
            전체 <b>{allCount || ''}</b>
          </button>
          {meta.data.statuses.map((s) => (
            <button key={s.id} className={statusId === String(s.id) ? 'active' : ''} onClick={() => reset(() => setStatusId(String(s.id)))}>
              <i className={`dot ${statusTone(s.name)}`} />
              {s.name} <b>{counts.get(s.id) ?? 0}</b>
            </button>
          ))}
        </div>
      )}

      {view === 'list' ? (
        builds.isLoading ? <Loading /> : builds.error ? <ErrorBox error={builds.error} /> : (
          <>
            <BuildTable rows={builds.data?.items ?? []} onOpen={setOpenId} />
            <Pager total={builds.data?.total ?? 0} page={page} onPage={setPage} />
          </>
        )
      ) : gantt.isLoading ? <Loading /> : gantt.error ? <ErrorBox error={gantt.error} /> : (
        <Gantt items={gantt.data ?? []} onOpen={setOpenId} />
      )}

      <p className="modular-sync">
        {meta.data?.syncedAt ? `그룹웨어 376 마지막 반영 ${new Date(meta.data.syncedAt).toLocaleString('ko-KR')}` : '그룹웨어 376 미러'}
        {meta.data && !meta.data.editEnabled && ' · 지금은 그룹웨어가 원본이라 DY ONE에서는 조회만 합니다.'}
      </p>

      {openId !== null && <BuildDialog id={openId} onClose={closeDoc} />}
      {creating && meta.data && (
        <BuildForm meta={meta.data} initial={null} projectCode={projectCode} onClose={() => setCreating(false)} onSaved={(id) => { setCreating(false); setOpenId(id) }} />
      )}
    </div>
  )
}

/** 그룹웨어 기본 보기(view 459)와 같은 20열. 제목 열 = 프로젝트명. */
function BuildTable({ rows, onOpen }: { rows: ModularBuild[]; onOpen(id: number): void }) {
  return (
    <DataTable
      headers={['연도', '상태', '설치위치', '프로젝트 코드', '프로젝트명', '출하 월', '설치 시작', '시운전 시작', '구분', '출하 예정일',
        '국내/해외', '로봇 수', '로봇기종', '도장기 사양', '통신 사양', '공장', '고객사', '공정', '공정세부', '외부검수']}
      rows={rows.map((b) => ({
        key: String(b.id),
        cells: [
          b.year ?? '-',
          <StatusBadge status={b.status} />,
          list(b.installLocations),
          <span className="code">{b.projectCode}</span>,
          <button className="row-title" onClick={() => onOpen(b.id)}>
            {b.projectName}
            {!b.fromGroupware && <span className="badge blue mini">DY ONE</span>}
            {b.managedInDyone && <span className="badge blue mini">DY ONE 관리</span>}
            {b.gwDeleted && <span className="badge red mini">그룹웨어 삭제</span>}
          </button>,
          b.shipMonth ?? '-',
          day(b.installStart),
          day(b.commissioningStart),
          b.category ?? '-',
          day(b.shipDue),
          b.region ?? '-',
          b.robotCount ?? '-',
          list(b.robotModels),
          list(b.applicators),
          list(b.comms),
          b.plant ?? '-',
          b.customer ?? '-',
          b.process ?? '-',
          b.processDetail ?? '-',
          b.externalInspection ?? '-',
        ],
      }))}
      empty="조건에 맞는 모듈라 건이 없습니다."
    />
  )
}

/** 간트: 제목 = 프로젝트명, 시작 = 모듈라 설치 시작, 종료 = 출하 예정일(없으면 시운전 시작) */
function Gantt({ items, onOpen }: { items: ModularGanttItem[]; onOpen(id: number): void }) {
  const [now] = useState(() => Date.now())
  const box = useRef<HTMLDivElement>(null)
  const range = useMemo(() => {
    if (items.length === 0) return null
    const t = (s: string) => new Date(s + 'T00:00:00').getTime()
    const min = Math.min(...items.map((i) => t(i.start)))
    const max = Math.max(...items.map((i) => t(i.end)))
    const from = new Date(new Date(min).getFullYear(), new Date(min).getMonth(), 1).getTime()
    const toD = new Date(max)
    const to = new Date(toD.getFullYear(), toD.getMonth() + 1, 1).getTime()
    const months: { label: string; left: number }[] = []
    for (let d = new Date(from); d.getTime() < to; d.setMonth(d.getMonth() + 1))
      months.push({ label: `${String(d.getFullYear()).slice(2)}.${d.getMonth() + 1}`, left: ((d.getTime() - from) / (to - from)) * 100 })
    const todayLeft = now >= from && now <= to ? ((now - from) / (to - from)) * 100 : null
    return { from, to, months, t, todayLeft }
  }, [items, now])

  // 한 달에 고정 폭을 주고 가로로 스크롤한다. 처음 열면 오늘 위치로 이동.
  const trackWidth = (range?.months.length ?? 0) * MONTH_PX
  useEffect(() => {
    const el = box.current
    if (!el || range?.todayLeft == null) return
    el.scrollLeft = Math.max(0, (range.todayLeft / 100) * trackWidth - el.clientWidth / 2)
    // 세로로는 오늘 이후에 끝나는(아직 진행 중인) 첫 건으로 이동
    const first = items.findIndex((i) => range.t(i.end) >= now)
    const row = first >= 0 ? el.querySelectorAll<HTMLElement>('.gantt-row')[first + 1] : null
    if (row) el.scrollTop = row.offsetTop - (el.querySelector<HTMLElement>('.gantt-head')?.offsetHeight ?? 0)
  }, [range, trackWidth, items, now])

  if (!range) return <div className="empty">설치 시작일이 있는 모듈라 건이 없습니다.</div>
  const pos = (s: string) => ((range.t(s) - range.from) / (range.to - range.from)) * 100
  const { todayLeft } = range
  const rowStyle = { gridTemplateColumns: `var(--gantt-title) ${trackWidth}px` }

  return (
    <div className="gantt" role="table" aria-label="모듈라 간트" ref={box}>
      <div className="gantt-row gantt-head" role="row" style={rowStyle}>
        <div className="gantt-title" role="columnheader">프로젝트</div>
        <div className="gantt-track" role="columnheader">
          {range.months.map((m) => <span key={m.label} className="gantt-month" style={{ left: `${m.left}%` }}>{m.label}</span>)}
        </div>
      </div>
      {items.map((i) => {
        const left = pos(i.start)
        const width = Math.max(pos(i.end) - left, 0.6)
        return (
          <div key={i.id} className="gantt-row" role="row" style={rowStyle}>
            <button className="gantt-title row-title" onClick={() => onOpen(i.id)} title={`${i.projectCode} ${i.projectName}`}>
              <small className="code">{i.projectCode}</small>
              {i.projectName}
            </button>
            <div className="gantt-track">
              {range.months.map((m) => <i key={m.label} className="gantt-grid" style={{ left: `${m.left}%` }} />)}
              {todayLeft !== null && <i className="gantt-today" style={{ left: `${todayLeft}%` }} />}
              <button
                className={`gantt-bar ${statusTone(i.status?.name)}${i.endIsEstimate ? ' estimate' : ''}`}
                style={{ left: `${left}%`, width: `${width}%` }}
                onClick={() => onOpen(i.id)}
                title={`${i.status?.name ?? ''} · ${i.start} ~ ${i.end}${i.endIsEstimate ? ' (출하 예정일 없음)' : ''}`}
              >
                <span>{i.status?.name}</span>
              </button>
            </div>
          </div>
        )
      })}
    </div>
  )
}

/** 상세: 그룹웨어 입력 폼과 같은 3구역 배치 + 상태 전환 + 활동 기록 */
function BuildDialog({ id, onClose }: { id: number; onClose(): void }) {
  const meta = useMeta()
  const toast = useToast()
  const queryClient = useQueryClient()
  const [editing, setEditing] = useState(false)
  const [tab, setTab] = useState<'form' | 'activity' | 'history' | 'files'>('form')
  const [historyKind, setHistoryKind] = useState<'all' | 'status' | 'edit'>('all')
  const detail = useQuery({ queryKey: ['modular-build', id], queryFn: () => api<ModularDetail>(`/api/modular/builds/${id}`) })
  const manage = useMutation({
    mutationFn: () => api(`/api/modular/builds/${id}/manage`, { method: 'POST' }),
    onSuccess: () => {
      toast('DY ONE 관리로 전환했습니다. 이제 그룹웨어 변경은 반영하지 않습니다.')
      void queryClient.invalidateQueries({ queryKey: ['modular-build', id] })
      void queryClient.invalidateQueries({ queryKey: ['modular-builds'] })
    },
    onError: (e) => toast(e instanceof Error ? e.message : '전환하지 못했습니다.'),
  })
  const transition = useMutation({
    mutationFn: (transitionId: number) => api(`/api/modular/builds/${id}/transition`, { method: 'POST', json: { transitionId } }),
    onSuccess: () => {
      toast('상태를 바꿨습니다.')
      void queryClient.invalidateQueries({ queryKey: ['modular-build', id] })
      void queryClient.invalidateQueries({ queryKey: ['modular-builds'] })
      void queryClient.invalidateQueries({ queryKey: ['modular-gantt'] })
    },
    onError: (e) => toast(e instanceof Error ? e.message : '상태를 바꾸지 못했습니다.'),
  })

  if (!detail.data || !meta.data) return <Modal title="모듈라" onClose={onClose}>{detail.error ? <ErrorBox error={detail.error} /> : <Loading />}</Modal>
  const d = detail.data
  const f = d.fields
  const statusName = (sid: number) => meta.data.statuses.find((s) => s.id === sid)?.name ?? String(sid)
  const opts = (key: ModularListKey) => {
    const picked = d.options[key] ?? []
    const labels = meta.data.options[key] ?? []
    return picked.map((c) => labels.find((o) => o.code === c)?.label ?? String(c)).join(', ') || null
  }
  const single = (key: ModularListKey, code?: number) => (code === undefined ? null : meta.data.options[key]?.find((o) => o.code === code)?.label ?? String(code))
  const member = (role: ModularRole) => d.members.find((m) => m.role === role)?.name || null

  if (editing)
    return (
      <BuildForm
        meta={meta.data}
        initial={d}
        projectCode={f.projectCode}
        onClose={() => setEditing(false)}
        onSaved={() => {
          setEditing(false)
          void queryClient.invalidateQueries({ queryKey: ['modular-build', id] })
          void queryClient.invalidateQueries({ queryKey: ['modular-builds'] })
        }}
      />
    )

  return (
    <Modal
      title={f.projectName}
      onClose={onClose}
      footer={d.editable ? <button className="primary" onClick={() => setEditing(true)}>수정</button> : undefined}
    >
      <div className="modular-head">
        <span className="code">{f.projectCode}</span>
        <StatusBadge status={d.item.status} />
        <span className={`badge ${d.managedInDyone || !d.fromGroupware ? 'blue' : ''}`}>
          {!d.fromGroupware ? 'DY ONE 입력' : d.managedInDyone ? 'DY ONE 관리' : '그룹웨어 연동'}
        </span>
        {d.gwDeletedAt && <span className="badge red">그룹웨어에서 삭제됨</span>}
        {d.project && <small>ERP 프로젝트: {d.project.name}{d.project.partnerName ? ` · ${d.project.partnerName}` : ''}</small>}
      </div>

      <SyncNote detail={d} onManage={() => manage.mutate()} pending={manage.isPending} />

      <div className="modular-flow">
        <span>현재 상태에서 할 수 있는 전환</span>
        {d.transitions.length === 0 && <em>없음</em>}
        {d.transitions.map((t) => (
          <button
            key={t.id}
            className="secondary"
            disabled={!d.editable || transition.isPending}
            title={d.editable ? `${t.action} → ${statusName(t.to)}` : '입력 기능이 꺼져 있습니다.'}
            onClick={() => window.confirm(`'${t.action}' → ${statusName(t.to)}(으)로 바꿀까요?`) && transition.mutate(t.id)}
          >
            {t.action} <small>→ {statusName(t.to)}</small>
          </button>
        ))}
      </div>

      <div className="project-tabs modular-tabs" role="tablist">
        <button role="tab" aria-selected={tab === 'form'} className={tab === 'form' ? 'active' : ''} onClick={() => setTab('form')}>정보</button>
        <button role="tab" aria-selected={tab === 'activity'} className={tab === 'activity' ? 'active' : ''} onClick={() => setTab('activity')}>
          활동 기록 <small>{d.activities.length}</small>
        </button>
        <button role="tab" aria-selected={tab === 'history'} className={tab === 'history' ? 'active' : ''} onClick={() => setTab('history')}>
          변경 이력 <small>{d.history.length}</small>
        </button>
        <button role="tab" aria-selected={tab === 'files'} className={tab === 'files' ? 'active' : ''} onClick={() => setTab('files')}>
          첨부 <small>{d.files.length}</small>
        </button>
      </div>

      {tab === 'form' && (
        <>
          <FormSection title="기본 정보" cols={2}>
            <Field label="연도" value={f.year} />
            <Field label="모듈라 설치 시작" value={f.installStart ?? f.installStartRaw} />
            <Field label="프로젝트 코드" value={f.projectCode} />
            <Field label="모듈라 시운전 시작" value={f.commissioningStart ?? f.commissioningStartRaw} />
            <Field label="프로젝트명" value={f.projectName} />
            <Field label="외부검수" value={f.externalInspection} />
            <Field label="참고자료" value={d.files.filter((x) => x.source === 'doc').map((x) => x.name).join(', ') || null} />
            <Field label="출하 예정일" value={f.shipDue} />
            <Field label="원점툴 입고 내역" value={f.originToolReceipt} />
            <Field label="출하 월" value={f.shipMonth} />
            <Field label="모듈라 설치위치" value={opts('install_location')} />
            <Field label="비고" value={f.note} />
          </FormSection>
          <FormSection title="장비 사양" cols={2}>
            <Field label="로봇기종" value={opts('robot_model')} />
            <Field label="통신 사양" value={opts('comm')} />
            <Field label="로봇 케이블 길이 (로봇~BOX)" value={opts('cable_robot_box')} />
            <Field label="도장기 사양" value={opts('applicator')} />
            <Field label="로봇 케이블 길이 (BOX~RC)" value={opts('cable_box_rc')} />
          </FormSection>
          <FormSection title="고객 · 담당자" cols={3}>
            <Field label="고객사" value={f.customer} />
            <Field label="공장" value={f.plant} />
            <Field label="구분" value={single('category', f.categoryCode)} />
            <Field label="공정 (내판/외판/하도/자동화)" value={f.process} />
            <Field label="공정세부" value={f.processDetail} />
            <Field label="국내/해외" value={single('region', f.regionCode)} />
            <Field label="로봇 수" value={f.robotCount ?? f.robotCountRaw} />
            <Field label="PL" value={member('pl')} />
            <Field label="설계" value={member('design')} />
            <Field label="PM" value={member('pm')} />
            <Field label="제어" value={member('control')} />
            <Field label="티쳐" value={member('teach')} />
          </FormSection>
          <FormSection title="주요이슈" cols={1}>
            <Field label="" value={f.mainIssue} />
          </FormSection>
          <p className="modular-sync">
            등록 {f.createdByName ?? '-'} · {f.createdAt ? new Date(f.createdAt).toLocaleString('ko-KR') : '-'} / 수정 {f.updatedByName ?? '-'} ·{' '}
            {f.updatedAt ? new Date(f.updatedAt).toLocaleString('ko-KR') : '-'}
            {d.gwUrl && (
              <>
                {' · '}
                <a href={d.gwUrl} target="_blank" rel="noopener noreferrer">그룹웨어 376 열기 ↗</a>
              </>
            )}
          </p>
        </>
      )}

      {tab === 'activity' && (
        <div className="modular-activities">
          {d.activities.length === 0 && <div className="empty">활동 기록이 없습니다.</div>}
          {d.activities.map((a) => (
            <article key={a.id}>
              <header>
                <b>{a.by ?? '작성자 미상'}</b>
                <small>{when(a.at)}</small>
              </header>
              <p>{a.body ?? '(내용 없음)'}</p>
              {a.files.length > 0 && <FileList files={a.files} />}
              {a.comments.length > 0 && (
                <ul className="modular-comments">
                  {a.comments.map((c) => (
                    <li key={c.id}>
                      <b>{c.by ?? '작성자 미상'}</b> <small>{when(c.at)}</small>
                      <p>{c.body ?? '(내용 없음)'}</p>
                    </li>
                  ))}
                </ul>
              )}
            </article>
          ))}
        </div>
      )}

      {tab === 'history' && <HistoryList items={d.history} kind={historyKind} onKind={setHistoryKind} />}

      {tab === 'files' && (
        <div className="modular-activities">
          {d.files.length === 0 ? <div className="empty">첨부 파일이 없습니다.</div> : <FileList files={d.files} showSource />}
          {d.files.some((x) => !x.stored) && (
            <p className="modular-sync">
              파일 목록은 DY ONE에 옮겼고, 파일 본체는 아직 그룹웨어 서버에 있습니다. 내려받기는 그룹웨어에서 하세요.
              {d.gwUrl && (
                <>
                  {' '}
                  <a href={d.gwUrl} target="_blank" rel="noopener noreferrer">그룹웨어 376 열기 ↗</a>
                </>
              )}
            </p>
          )}
        </div>
      )}
    </Modal>
  )
}

/** 그룹웨어 연동 상태와 DY ONE에서 바꾼 칸(그룹웨어 값과 다른 칸) */
function SyncNote({ detail: d, onManage, pending }: { detail: ModularDetail; onManage(): void; pending: boolean }) {
  if (!d.fromGroupware) return null
  return (
    <div className={`modular-sync-note${d.managedInDyone ? ' is-managed' : ''}`}>
      <p>
        {d.managedInDyone
          ? `DY ONE에서 관리하는 건입니다${d.managedInDyoneBy ? ` (${d.managedInDyoneBy} 전환)` : ''}. 그룹웨어 변경은 반영하지 않습니다.`
          : '그룹웨어에서 바뀐 칸은 DY ONE에도 반영됩니다. DY ONE에서 고친 값은 그룹웨어로 보내지 않으며, 그룹웨어에서 같은 칸을 바꾸면 그룹웨어 값으로 덮입니다.'}
      </p>
      {d.differences.length > 0 && (
        <>
          <b>DY ONE에서 바꾼 칸 {d.differences.length}개 (그룹웨어 값과 다름)</b>
          <ul>
            {d.differences.map((x) => (
              <li key={x.key}>
                <span>{x.label}</span>
                <em>DY ONE {x.dyOne}</em>
                <small>그룹웨어 {x.groupware}</small>
              </li>
            ))}
          </ul>
        </>
      )}
      {d.editable && !d.managedInDyone && (
        <button
          className="secondary"
          disabled={pending}
          onClick={() =>
            window.confirm('이 건을 DY ONE 관리로 전환할까요?\n이후 그룹웨어에서 바뀐 값은 DY ONE에 반영되지 않습니다. 되돌릴 수 없습니다.') && onManage()
          }
        >
          DY ONE 관리 전환
        </button>
      )}
    </div>
  )
}

/** 변경 이력: 그룹웨어 변경 이력 + DY ONE에서 한 상태 전환. 상태 변경만 / 정보 변경만 거를 수 있다. */
function HistoryList({ items, kind, onKind }: { items: ModularHistoryItem[]; kind: 'all' | 'status' | 'edit'; onKind(k: 'all' | 'status' | 'edit'): void }) {
  const shown = kind === 'all' ? items : items.filter((h) => h.kind === kind || (kind === 'edit' && h.kind === 'overwrite'))
  return (
    <div className="modular-activities">
      <div className="segmented" role="group" aria-label="이력 종류">
        {([['all', '전체'], ['status', '상태 변경'], ['edit', '정보 변경']] as const).map(([k, label]) => (
          <button key={k} className={kind === k ? 'active' : ''} onClick={() => onKind(k)}>{label}</button>
        ))}
      </div>
      {shown.length === 0 && <div className="empty">이력이 없습니다.</div>}
      {shown.map((h, i) => (
        <article key={`${h.source}-${i}`} className={h.kind === 'status' ? 'is-status' : h.kind === 'overwrite' ? 'is-overwrite' : h.source === 'dyone' ? 'is-dyone' : undefined}>
          <header>
            <b>{h.title ?? '변경'}</b>
            {h.kind === 'status' && h.to && (
              <span className="status-move">
                <span className={`badge ${statusTone(h.from)}`}>{h.from ?? '-'}</span> → <span className={`badge ${statusTone(h.to)}`}>{h.to}</span>
              </span>
            )}
            <small>{h.by ?? ''} · {when(h.at)} · {h.source === 'dyone' ? 'DY ONE' : '그룹웨어'}</small>
          </header>
          {h.kind !== 'status' && h.detail && <p>{h.detail}</p>}
        </article>
      ))}
    </div>
  )
}

function FileList({ files, showSource }: { files: ModularFile[]; showSource?: boolean }) {
  return (
    <ul className="modular-files">
      {files.map((f) => (
        <li key={f.id}>
          <span className="file-ext">{(f.ext ?? '').toUpperCase() || 'FILE'}</span>
          <span className="file-name">{f.name ?? '(이름 없음)'}</span>
          <small>{bytes(f.size)}{showSource ? ` · ${f.source === 'doc' ? '참고자료' : '활동 기록'}` : ''}{f.stored ? '' : ' · 그룹웨어 보관'}</small>
        </li>
      ))}
    </ul>
  )
}

function FormSection({ title, cols, children }: { title: string; cols: 1 | 2 | 3; children: ReactNode }) {
  return (
    <section className="modular-section">
      <h3>{title}</h3>
      <dl className={`modular-grid cols-${cols}`}>{children}</dl>
    </section>
  )
}

function Field({ label, value }: { label: string; value: string | number | null | undefined }) {
  return (
    <div>
      {label && <dt>{label}</dt>}
      <dd>{value === null || value === undefined || value === '' ? '-' : String(value)}</dd>
    </div>
  )
}

// ── 입력 폼 (DY ONE에서 만든 건, 입력 기능이 켜져 있을 때만) ──────────────────
type FormState = {
  year: string; projectCode: string; projectName: string; installStart: string; commissioningStart: string; externalInspection: string
  shipDue: string; shipMonth: string; originToolReceipt: string; note: string; customer: string; plant: string; process: string
  processDetail: string; robotCount: string; categoryCode: string; regionCode: string; mainIssue: string
  options: Record<string, number[]>; members: Record<string, string>
}

const MULTI: { key: ModularListKey; label: string }[] = [
  { key: 'install_location', label: '모듈라 설치위치' },
  { key: 'robot_model', label: '로봇기종' },
  { key: 'cable_robot_box', label: '로봇 케이블 길이 (로봇~BOX)' },
  { key: 'cable_box_rc', label: '로봇 케이블 길이 (BOX~RC)' },
  { key: 'comm', label: '통신 사양' },
  { key: 'applicator', label: '도장기 사양' },
]

function initialForm(meta: ModularMeta, d: ModularDetail | null, projectCode: string | null): FormState {
  const def = (key: ModularListKey) => meta.options[key]?.find((o) => o.isDefault)?.code
  const f = d?.fields
  const s = (v?: string | number | null) => (v === undefined || v === null ? '' : String(v))
  return {
    year: s(f?.year ?? new Date().getFullYear()), projectCode: f?.projectCode ?? projectCode ?? '', projectName: s(f?.projectName),
    installStart: s(f?.installStart), commissioningStart: s(f?.commissioningStart), externalInspection: s(f?.externalInspection),
    shipDue: s(f?.shipDue), shipMonth: s(f?.shipMonth), originToolReceipt: s(f?.originToolReceipt), note: s(f?.note),
    customer: s(f?.customer), plant: s(f?.plant), process: s(f?.process), processDetail: s(f?.processDetail),
    robotCount: s(f?.robotCountRaw ?? f?.robotCount), categoryCode: s(d ? f?.categoryCode : def('category')), regionCode: s(d ? f?.regionCode : def('region')),
    mainIssue: s(f?.mainIssue),
    options: d ? { ...d.options } : {},
    members: Object.fromEntries((d?.members ?? []).map((m) => [m.role, m.name])),
  }
}

function BuildForm({ meta, initial, projectCode, onClose, onSaved }: {
  meta: ModularMeta; initial: ModularDetail | null; projectCode: string | null; onClose(): void; onSaved(id: number): void
}) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [form, setForm] = useState(() => initialForm(meta, initial, projectCode))
  const set = (key: keyof FormState) => (e: { target: { value: string } }) => setForm((f) => ({ ...f, [key]: e.target.value }))
  const toggle = (key: string, code: number) =>
    setForm((f) => {
      const cur = f.options[key] ?? []
      return { ...f, options: { ...f.options, [key]: cur.includes(code) ? cur.filter((c) => c !== code) : [...cur, code] } }
    })

  const save = useMutation({
    mutationFn: () => {
      const n = (v: string) => (v.trim() === '' ? null : v.trim())
      const i = (v: string) => (v.trim() === '' ? null : Number(v))
      const body = {
        year: i(form.year), projectCode: form.projectCode.trim(), projectName: form.projectName.trim(),
        installStart: n(form.installStart), commissioningStart: n(form.commissioningStart), externalInspection: n(form.externalInspection),
        shipDue: n(form.shipDue), shipMonth: n(form.shipMonth), originToolReceipt: n(form.originToolReceipt), note: n(form.note),
        customer: n(form.customer), plant: n(form.plant), process: n(form.process), processDetail: n(form.processDetail),
        robotCount: n(form.robotCount), categoryCode: i(form.categoryCode), regionCode: i(form.regionCode), mainIssue: n(form.mainIssue),
        options: form.options, members: form.members,
      }
      return initial
        ? api<{ id: number }>(`/api/modular/builds/${initial.item.id}`, { method: 'PUT', json: body })
        : api<{ id: number }>('/api/modular/builds', { method: 'POST', json: body })
    },
    onSuccess: (r) => {
      toast(initial ? '저장했습니다.' : '등록했습니다.')
      void queryClient.invalidateQueries({ queryKey: ['modular-builds'] })
      void queryClient.invalidateQueries({ queryKey: ['modular-gantt'] })
      onSaved(r.id)
    },
    onError: (e) => toast(e instanceof Error ? e.message : '저장하지 못했습니다.'),
  })

  const text = (key: keyof FormState, label: string, required = false, type = 'text') => (
    <label className="field">
      {label}{required && ' *'}
      <input type={type} value={form[key] as string} onChange={set(key)} required={required} />
    </label>
  )
  const select = (key: 'categoryCode' | 'regionCode', label: string, list: ModularListKey) => (
    <label className="field">
      {label}
      <select value={form[key]} onChange={set(key)}>
        <option value="">-</option>
        {meta.options[list]?.map((o) => <option key={o.code} value={o.code}>{o.label}</option>)}
      </select>
    </label>
  )
  const member = (role: ModularRole) => (
    <label className="field">
      {meta.roles[role]}
      <input value={form.members[role] ?? ''} onChange={(e) => setForm((f) => ({ ...f, members: { ...f.members, [role]: e.target.value } }))} />
    </label>
  )

  return (
    <Modal
      title={initial ? '모듈라 수정' : '모듈라 등록'}
      onClose={onClose}
      footer={<button className="primary" disabled={save.isPending || !form.projectCode.trim() || !form.projectName.trim() || !form.year} onClick={() => save.mutate()}>저장</button>}
    >
      <section className="modular-section">
        <h3>기본 정보</h3>
        <div className="modular-form cols-2">
          {text('year', '연도', true, 'number')}
          {text('installStart', '모듈라 설치 시작', false, 'date')}
          {text('projectCode', '프로젝트 코드', true)}
          {text('commissioningStart', '모듈라 시운전 시작', false, 'date')}
          {text('projectName', '프로젝트명', true)}
          {text('externalInspection', '외부검수')}
          {text('originToolReceipt', '원점툴 입고 내역')}
          {text('shipDue', '출하 예정일', false, 'date')}
          {text('note', '비고')}
          {text('shipMonth', '출하 월')}
        </div>
      </section>
      <section className="modular-section">
        <h3>장비 사양 · 설치위치</h3>
        {MULTI.map((m) => (
          <fieldset key={m.key} className="modular-checks">
            <legend>{m.label}</legend>
            {meta.options[m.key]?.map((o) => (
              <label key={o.code}>
                <input type="checkbox" checked={(form.options[m.key] ?? []).includes(o.code)} onChange={() => toggle(m.key, o.code)} />
                {o.label}
              </label>
            ))}
          </fieldset>
        ))}
      </section>
      <section className="modular-section">
        <h3>고객 · 담당자</h3>
        <div className="modular-form cols-3">
          {text('customer', '고객사')}
          {text('plant', '공장')}
          {select('categoryCode', '구분', 'category')}
          {text('process', '공정 (내판/외판/하도/자동화)')}
          {text('processDetail', '공정세부')}
          {select('regionCode', '국내/해외', 'region')}
          {text('robotCount', '로봇 수')}
          {member('pl')}
          {member('design')}
          {member('pm')}
          {member('control')}
          {member('teach')}
        </div>
      </section>
      <section className="modular-section">
        <h3>주요이슈</h3>
        <textarea rows={4} value={form.mainIssue} onChange={set('mainIssue')} aria-label="주요이슈" />
      </section>
    </Modal>
  )
}

function Pager({ total, page, onPage }: { total: number; page: number; onPage(p: number): void }) {
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE))
  if (pages <= 1) return null
  return (
    <div className="pager">
      <button className="secondary" disabled={page <= 1} onClick={() => onPage(page - 1)}>← 이전</button>
      <span>{page} / {pages}</span>
      <button className="secondary" disabled={page >= pages} onClick={() => onPage(page + 1)}>다음 →</button>
    </div>
  )
}
