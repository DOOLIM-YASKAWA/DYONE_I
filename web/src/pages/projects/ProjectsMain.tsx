import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { api } from '../../api/client'
import type { PagedResult, Project } from '../../api/types'
import type { Brief, MainData, MainList, MainRow, ScheduleItem, ScorePart } from '../../api/projectMainTypes'
import { useAuth } from '../../auth/AuthContext'
import { ErrorBox, Loading } from '../../components/ui'
import { useToast } from '../../components/Toast'
import { Pager } from './Pager'
import '../../styles/project-main.css'

// 프로젝트 메인(로그인 첫 화면, 11-04 J안): 위에서부터 검색 → 오늘의 프로젝트 브리핑 → [내 관심 | 회사 주요] → 전체 프로젝트.
// 브리핑·후보 점수·이유 문장은 Worker(project-main 잡, 매일 06:30)가 만든 것을 읽기만 한다. 이 화면이 쓰는 것은 ★와 회사 주요 선정뿐.
// 설계: docs/roadmap/project-main-design.md · 시안: docs/시안/main-variants/j-band.html

const PAGE_SIZE = 50
const CUSTOMERS = ['전체', '현대', '기아', 'GM', '르노', '쌍용', '기타']
const ERP_FILTERS: [string, string][] = [['', '진행 중'], ['sales', 'ERP 영업중'], ['ordered', 'ERP 수주'], ['closed', '마감·중단']]
const STAGE_COLOR: Record<string, string> = { '영업': '#2563EB', '수주': '#15803D', '설계·검토': '#6D28D9', '모듈라': '#0F766E', '현장': '#B45309', '라인 대응·종료': '#475467' }
const ERP_CLASS: Record<string, string> = { '수주': 'order', '영업중': 'sales', '마감': 'off', '중단': 'off' }
const md = (v?: string) => (v ? `${new Date(v).getMonth() + 1}/${new Date(v).getDate()}` : '')
const hm = (v?: string) => (v ? new Date(v).toLocaleTimeString('ko-KR', { hour: '2-digit', minute: '2-digit', hour12: false }) : '')

type Drawer = { kind: 'fav' | 'feat' | 'cand' | 'sched' } | { kind: 'evidence'; brief: Brief } | { kind: 'why'; row: MainRow } | null

/** 3줄씩 묶기(카드·브리핑 넘김 단위). 비어 있어도 한 쪽은 있다 */
const chunk = <T,>(xs: T[], n = 3): T[][] => (xs.length ? Array.from({ length: Math.ceil(xs.length / n) }, (_, i) => xs.slice(i * n, i * n + n)) : [[]])

/**
 * 자동 넘김(11-04): ms마다 다음 쪽(ms가 0이면 손으로만 넘긴다). 마우스를 올리거나 안에 포커스가 있으면 멈추고, 손으로 넘기면 그때부터 다시 센다.
 * 창이 안 보이거나 '동작 줄이기' 설정이면 넘기지 않는다.
 */
function useAutoPage(count: number, ms: number) {
  const [page, setPage] = useState(0)
  const [hold, setHold] = useState(false)
  const [tick, setTick] = useState(0)
  useEffect(() => {
    if (ms <= 0 || count <= 1 || hold || window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) return
    const t = window.setInterval(() => { if (!document.hidden) setPage((p) => (p + 1) % count) }, ms)
    return () => window.clearInterval(t)
  }, [count, ms, hold, tick])
  const go = (p: number) => { setPage(count > 0 ? ((p % count) + count) % count : 0); setTick((x) => x + 1) }
  const step = (d: number) => { setPage((p) => (count > 0 ? (((p + d) % count) + count) % count : 0)); setTick((x) => x + 1) }
  const holdProps = { onMouseEnter: () => setHold(true), onMouseLeave: () => setHold(false), onFocus: () => setHold(true), onBlur: () => setHold(false) }
  return { page: Math.min(page, Math.max(0, count - 1)), go, step, holdProps }
}

/** 쪽 점(누르면 그 쪽으로) */
function PageDots({ count, page, onGo, label }: { count: number; page: number; onGo(p: number): void; label: string }) {
  if (count <= 1) return null
  return (
    <span className="pmj-pdots" role="tablist" aria-label={`${label} 쪽`}>
      {Array.from({ length: count }, (_, i) => (
        <button key={i} role="tab" aria-selected={i === page} aria-label={`${i + 1}쪽`} className={i === page ? 'on' : ''} onClick={() => onGo(i)} />
      ))}
    </span>
  )
}

export function ProjectsMain() {
  const [params, setParams] = useSearchParams()
  const navigate = useNavigate()
  const { user } = useAuth()
  const toast = useToast()
  const qc = useQueryClient()
  const search = params.get('search') ?? ''
  const customer = params.get('customer') ?? ''
  const erp = params.get('erp') ?? ''
  const [page, setPage] = useState(1)
  const [drawer, setDrawer] = useState<Drawer>(null)
  const searchRef = useRef<HTMLInputElement>(null)

  const main = useQuery({ queryKey: ['projects-main'], queryFn: () => api<MainData>('/api/projects/main') })
  const favIds = useQuery({ queryKey: ['favorite-ids'], queryFn: () => api<number[]>('/api/projects/favorites/ids') })
  const list = useQuery({
    queryKey: ['projects', 'main', search, customer, erp, page],
    queryFn: () => {
      const q = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) })
      if (search) q.set('search', search)
      if (customer) q.set('customer', customer)
      q.set('erp', erp || 'live') // 기본 '진행 중' = ERP 영업중·수주 + DY ONE에서 단계를 진행한 것
      return api<PagedResult<Project>>(`/api/projects?${q}`)
    },
    placeholderData: keepPreviousData,
  })

  const fav = useMutation({
    mutationFn: ({ id, on }: { id: number; on: boolean }) => api<void>(`/api/projects/${id}/favorite`, { method: on ? 'PUT' : 'DELETE' }),
    onMutate: ({ id, on }) => {
      qc.setQueryData<number[]>(['favorite-ids'], (old = []) => (on ? [...new Set([...old, id])] : old.filter((x) => x !== id)))
    },
    onSuccess: (_, { on }) => toast(on ? '내 관심에 넣었습니다. 내일 아침 브리핑부터 지금 상태가 나옵니다.' : '내 관심에서 뺐습니다.'),
    onError: () => toast('★ 저장에 실패했습니다.'),
    onSettled: () => {
      qc.invalidateQueries({ queryKey: ['favorite-ids'] })
      qc.invalidateQueries({ queryKey: ['projects-main'] })
      qc.invalidateQueries({ queryKey: ['projects-main-list'] })
    },
  })
  const favSet = new Set(favIds.data ?? [])
  const toggleFav = (id?: number) => id && fav.mutate({ id, on: !favSet.has(id) })

  const setParam = (key: string, value: string) => {
    const p = new URLSearchParams(params)
    if (value) p.set(key, value)
    else p.delete(key)
    setPage(1)
    setParams(p)
  }
  const open = (row: { projectId?: number; code: string }) => {
    if (row.projectId) navigate(`/projects/${row.projectId}`)
    else toast(`${row.code}는 ERP에만 있는 코드입니다. 그룹웨어 프로젝트로 들어오면 상세 화면이 열립니다.`)
  }

  // '/'로 검색, Esc로 서랍 닫기(←·→ 브리핑 넘기기는 BriefPanel)
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setDrawer(null)
      const t = e.target as HTMLElement
      if (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable) return
      if (e.key === '/') { e.preventDefault(); document.getElementById('pmj-all')?.scrollIntoView({ behavior: 'smooth', block: 'start' }); searchRef.current?.focus({ preventScroll: true }) }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  const m = main.data
  const noFav = m ? m.favorites.total === 0 : false
  const favPages = chunk(m?.favorites.items ?? [])
  const featPages = chunk(m?.featured.items ?? [])
  const favPager = useAutoPage(favPages.length, 7000)
  const featPager = useAutoPage(featPages.length, 7000)
  const schedPages = chunk(m?.schedule ?? [])
  const schedPager = useAutoPage(schedPages.length, 7000)
  const yday = m ? [...m.favBriefs, ...m.featBriefs].reduce((s, b) => s + b.changedYesterday, 0) : 0
  const hour = new Date().getHours()
  const hello = hour < 11 ? '좋은 아침입니다' : hour < 18 ? '안녕하세요' : '수고 많으셨습니다'

  return (
    <div className="pmj">
      <section className="pmj-hero">
        <div className="pmj-wrap">
          <div className="pmj-greet">
            <div>
              <h1>프로젝트</h1>
              <p>
                {hello}, {user?.name} 님.{' '}
                {m && (yday > 0 ? <>관심·주요 프로젝트에 어제 기록이 <b>{yday.toLocaleString()}건</b> 있었습니다.</> : '오늘의 프로젝트 브리핑을 확인하세요.')}
              </p>
            </div>
            <button className="pmj-find" onClick={() => { searchRef.current?.focus({ preventScroll: true }); document.getElementById('pmj-all')?.scrollIntoView({ behavior: 'smooth', block: 'start' }) }}>
              <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" aria-hidden="true"><circle cx="11" cy="11" r="7" /><path d="m20 20-3.5-3.5" /></svg>
              프로젝트 찾기 <kbd>/</kbd>
            </button>
          </div>
          <BriefPanel data={m} onOpen={open} onEvidence={(b) => setDrawer({ kind: 'evidence', brief: b })} />
        </div>
      </section>

      <div className="pmj-wrap">
        <section className="pmj-pair">
          <div className="pmj-card">
            <div className="pmj-card-h">
              <h2>내 관심 프로젝트</h2>
              <span className="n">{m?.favorites.total ?? 0}</span>
              <small>★한 것만 · 최근 활동 순</small>
            </div>
            {!m ? <div className="pmj-empty"><Loading /></div> : noFav ? (
              <div className="pmj-empty">
                <b>★로 고른 프로젝트가 없습니다</b>
                <span>아래 전체 프로젝트에서 ☆을 누르면 여기에 보입니다.<br />PM·투입 프로젝트를 자동으로 넣지 않습니다.</span>
              </div>
            ) : <div className="pmj-pages" {...favPager.holdProps}>{favPages[favPager.page].map((r) => <FavRow key={r.code} r={r} onOpen={open} />)}</div>}
            {m && !noFav && (
              <div className="pmj-foot">
                <button onClick={() => setDrawer({ kind: 'fav' })}>모두 보기 →</button>
                <PageDots count={favPages.length} page={favPager.page} onGo={favPager.go} label="내 관심" />
              </div>
            )}
          </div>

          <div className="pmj-card">
            <div className="pmj-card-h">
              <h2>회사 주요 프로젝트</h2>
              <span className="n">{m?.featured.selected ?? 0}</span>
              {m && m.featured.selected === 0 && <em className="pmj-rec" title="아직 선정한 것이 없어 규칙 점수 상위(추천안)를 보여 줍니다">추천안</em>}
              <small>{m && m.featured.selected > 0 ? `선정 ${m.featured.selected}건 · 추천안 ${m.featured.total - m.featured.selected}건` : '선정 전 · 규칙 점수 상위'} · 다음 일정</small>
            </div>
            {!m ? <div className="pmj-empty"><Loading /></div> : m.featured.items.length === 0 ? (
              <div className="pmj-empty"><b>아직 후보가 없습니다</b><span>매일 06:30에 ERP·발주·NAS·모듈라 흔적으로 계산합니다.</span></div>
            ) : (
              <div className="pmj-pages" {...featPager.holdProps}>
                {featPages[featPager.page].map((r) => <FeatRow key={r.code} r={r} onOpen={open} onWhy={(row) => setDrawer({ kind: 'why', row })} />)}
              </div>
            )}
            {m && (
              <div className="pmj-foot">
                <button onClick={() => setDrawer({ kind: 'feat' })}>모두 보기 →</button>
                {m.canSelect && <button className="pmj-sel" onClick={() => setDrawer({ kind: 'cand' })}>✦ 새 후보 {m.candidates}건 검토</button>}
                <PageDots count={featPages.length} page={featPager.page} onGo={featPager.go} label="회사 주요" />
              </div>
            )}
          </div>

          <div className="pmj-card pmj-sched-card">
            <div className="pmj-card-h">
              <h2>일정 주의</h2>
              <span className="n late">{m?.scheduleTotal ?? 0}</span>
              <small>관심·주요 중 · 문서 일정 기준</small>
            </div>
            {!m ? <div className="pmj-empty"><Loading /></div> : (m.schedule ?? []).length === 0 ? (
              <div className="pmj-empty"><b>일정이 어긋난 프로젝트가 없습니다</b><span>사양정리·수행계획서·사양서의 일정을 매일 06:30에<br />모듈라·발주·도면 작업과 비교합니다.</span></div>
            ) : (
              <div className="pmj-pages" {...schedPager.holdProps}>
                {schedPages[schedPager.page].map((x) => <SchedRow key={`${x.code}-${x.kind}-${x.stage}`} x={x} onOpen={open} />)}
              </div>
            )}
            {m && (
              <div className="pmj-foot">
                <button onClick={() => setDrawer({ kind: 'sched' })}>모두 보기 →</button>
                <PageDots count={schedPages.length} page={schedPager.page} onGo={schedPager.go} label="일정 주의" />
              </div>
            )}
          </div>
        </section>

        <section className="pmj-sec" id="pmj-all">
          <div className="pmj-sec-h">
            <h2>전체 프로젝트</h2>
            <small>
              {(list.data?.total ?? 0).toLocaleString()}건 · 최근 활동 순 · 상태는 ERP 기준{search && <> · <b>‘{search}’</b> 검색</>}
            </small>
          </div>
          <form
            className="pmj-search pmj-search-in"
            role="search"
            onSubmit={(e) => {
              e.preventDefault()
              setParam('search', searchRef.current?.value.trim() ?? '')
            }}
          >
            <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" aria-hidden="true"><circle cx="11" cy="11" r="7" /><path d="m20 20-3.5-3.5" /></svg>
            <input ref={searchRef} key={search} defaultValue={search} placeholder="프로젝트 코드·이름·발주처 검색" aria-label="프로젝트 검색" />
            {search && <button type="button" className="pmj-clear" aria-label="검색어 지우기" onClick={() => setParam('search', '')}>×</button>}
            <kbd aria-hidden="true">/</kbd>
            <button className="pmj-submit" type="submit">검색</button>
          </form>
          <div className="pmj-tools">
            {CUSTOMERS.map((c) => {
              const on = c === '전체' ? !customer : customer === c
              return <button key={c} className={`pmj-chip${on ? ' on' : ''}`} aria-pressed={on} onClick={() => setParam('customer', c === '전체' ? '' : c)}>{c}</button>
            })}
            <span className="pmj-gap" />
            {ERP_FILTERS.map(([k, n]) => (
              <button key={k || 'live'} className={`pmj-chip${erp === k ? ' on' : ''}`} aria-pressed={erp === k} onClick={() => setParam('erp', k)}>{n}</button>
            ))}
          </div>
          {list.isLoading ? <Loading /> : list.error ? <ErrorBox error={list.error} /> : (
            <>
              <AllTable items={list.data?.items ?? []} fav={favSet} onFav={toggleFav} onOpen={(id) => navigate(`/projects/${id}`)} />
              <Pager total={list.data?.total ?? 0} page={page} onPage={setPage} pageSize={PAGE_SIZE} />
            </>
          )}
        </section>
      </div>

      {drawer && <MainDrawer drawer={drawer} canSelect={!!m?.canSelect} onClose={() => setDrawer(null)} onOpen={open} onFav={toggleFav} onWhy={(row) => setDrawer({ kind: 'why', row })} />}
      {main.error && <div className="pmj-wrap"><ErrorBox error={main.error} /></div>}
    </div>
  )
}

type BriefSlide = { group: 'fav' | 'feat'; items: Brief[]; empty?: string }

/**
 * 오늘의 프로젝트 브리핑 — '내 관심'·'회사 주요'를 3줄씩 한 장으로 묶어 넘긴다.
 * 자동으로 넘기지 않는다(11-04 사용자). 탭·화살표·밀기·←→로 넘긴다. (카드 두 개는 7초마다 자동으로 넘긴다)
 */
function BriefPanel({ data, onOpen, onEvidence }: { data?: MainData; onOpen(b: Brief): void; onEvidence(b: Brief): void }) {
  const favSlides: BriefSlide[] = !data ? [{ group: 'fav', items: [], empty: '불러오는 중…' }]
    : data.favorites.total === 0 ? [{ group: 'fav', items: [], empty: '★로 고른 관심 프로젝트가 없습니다. 아래 전체 프로젝트에서 ☆을 누르면 다음 날 아침부터 여기에 지금 상태가 나옵니다.' }]
    : data.favBriefs.length === 0 ? [{ group: 'fav', items: [], empty: '관심 프로젝트의 지금 상태는 내일 06:30에 처음 만들어집니다.' }]
    : chunk(data.favBriefs).map((items) => ({ group: 'fav' as const, items }))
  const featSlides: BriefSlide[] = !data ? []
    : data.featBriefs.length === 0 ? [{ group: 'feat', items: [], empty: '회사 주요 프로젝트의 지금 상태가 아직 없습니다(매일 06:30).' }]
    : chunk(data.featBriefs).map((items) => ({ group: 'feat' as const, items }))
  const slides = [...favSlides, ...featSlides]
  const pager = useAutoPage(slides.length, 0) // 브리핑은 자동으로 넘기지 않는다(11-04 사용자) — 탭·화살표·밀기·←→로만
  const noFav = !!data && data.favorites.total === 0
  const startedAtFeat = useRef(false)
  useEffect(() => { // ★가 없으면 '회사 주요' 장부터
    if (noFav && !startedAtFeat.current && featSlides.length) { startedAtFeat.current = true; pager.go(favSlides.length) }
  })
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement
      if (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable || document.querySelector('.pmj-drawer')) return
      if (e.key === 'ArrowLeft') pager.step(-1)
      if (e.key === 'ArrowRight') pager.step(1)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  })
  const start = useRef<number | null>(null)
  const moved = useRef(false)
  const cur = slides[pager.page]?.group ?? 'fav'
  const li = (b: Brief) => (
    <li key={b.code}>
      <button className="pmj-bf-name" onClick={() => !moved.current && onOpen(b)}>{b.name}</button>
      <span className="pmj-txt">{b.sentence}</span>
      {b.changedYesterday > 0 && <span className="pmj-ch" title="어제 생긴 모듈라·ES 기록과 NAS 수정 파일">어제 +{b.changedYesterday.toLocaleString()}</span>}
      <button className="pmj-ev" onClick={() => !moved.current && onEvidence(b)}>근거</button>
    </li>
  )
  return (
    <div className="pmj-brief" {...pager.holdProps}>
      <div className="pmj-bf-h">
        <h2><em>AI</em>오늘의 프로젝트 브리핑</h2>
        <small>{data?.briefAt ? `${md(data.briefAt)} ${hm(data.briefAt)} · 지금 상태` : '매일 06:30 · 지금 상태'}</small>
        <div className="pmj-seg" role="tablist" aria-label="브리핑 종류">
          <button role="tab" aria-selected={cur === 'fav'} className={cur === 'fav' ? 'on' : ''} onClick={() => pager.go(0)}>내 관심</button>
          <button role="tab" aria-selected={cur === 'feat'} className={cur === 'feat' ? 'on' : ''} onClick={() => pager.go(favSlides.length)}>회사 주요</button>
        </div>
        <div className="pmj-bf-nav">
          <button className="pmj-arrow" aria-label="이전 장" onClick={() => pager.step(-1)}>‹</button>
          <span className="pmj-dots" aria-hidden="true">{slides.map((_, i) => <i key={i} className={i === pager.page ? 'on' : ''} />)}</span>
          <button className="pmj-arrow" aria-label="다음 장" onClick={() => pager.step(1)}>›</button>
        </div>
      </div>
      <div
        className="pmj-bf-view"
        onPointerDown={(e) => { start.current = e.clientX; moved.current = false }}
        onPointerMove={(e) => { if (start.current !== null && Math.abs(e.clientX - start.current) > 10) moved.current = true }}
        onPointerUp={(e) => {
          if (start.current === null) return
          const dx = e.clientX - start.current
          start.current = null
          if (Math.abs(dx) > 40) pager.step(dx < 0 ? 1 : -1)
          window.setTimeout(() => { moved.current = false }, 0)
        }}
      >
        <div className="pmj-bf-track" style={{ transform: `translateX(-${pager.page * 100}%)` }}>
          {slides.map((sl, i) => (
            <ul key={i} aria-hidden={i !== pager.page}>
              {sl.empty ? <li className="pmj-bf-empty">{sl.empty}</li> : sl.items.map(li)}
            </ul>
          ))}
        </div>
      </div>
    </div>
  )
}

const KIND_LABEL: Record<string, string> = { late: '지연 의심', soon: '일정 임박', after_sop: '양산 후 작업 중', erp: 'ERP 기간 갱신 필요' }
const KIND_CLASS: Record<string, string> = { late: 'late', soon: 'soon', after_sop: 'run', erp: 'run' }
/** +35일(지남) · D-27(남음) · D-day */
const dday = (days: number) => (days > 0 ? `+${days}일` : days < 0 ? `D${days}` : 'D-day')

/** 일정 주의 한 줄(B안): D-day 칸 + 이름 + 한 줄 이유 */
function SchedRow({ x, onOpen }: { x: ScheduleItem; onOpen(r: { projectId?: number; code: string }): void }) {
  return (
    <button className="pmj-srow" onClick={() => onOpen(x)} title={`${KIND_LABEL[x.kind]} · ${x.reason}`}>
      <span className={`pmj-dday ${KIND_CLASS[x.kind]}`}>{dday(x.days)}</span>
      <span className="pmj-smain"><b>{x.name}</b><small>{x.reason}</small></span>
    </button>
  )
}

function ErpStatus({ s }: { s?: string }) {
  if (!s) return <span className="pmj-dim">ERP 없음</span>
  return <span className={`pmj-erp pmj-erp-${ERP_CLASS[s] ?? 'sales'}`}>{s}</span>
}

function Stage({ name }: { name?: string }) {
  if (!name) return null
  return <span className="pmj-stage"><i style={{ background: STAGE_COLOR[name] ?? '#475467' }} />{name}</span>
}

function FavRow({ r, onOpen }: { r: MainRow; onOpen(r: MainRow): void }) {
  return (
    <button className="pmj-row" onClick={() => onOpen(r)}>
      <span className="pmj-ico pmj-star" aria-hidden="true">★</span>
      <span className="pmj-main">
        <span className="pmj-name">{r.name}</span>
        <span className="pmj-code">{r.codeLabel}</span>
        <span className="pmj-sub">
          {[r.lastActivityAt ? `최근 ${md(r.lastActivityAt)} ${r.lastActivityLabel ?? ''}`.trim() : '최근 활동 없음', r.pmName && `PM ${r.pmName}`].filter(Boolean).join(' · ')}
        </span>
      </span>
      <span className="pmj-meta">{r.stage ? <Stage name={r.stage} /> : <ErpStatus s={r.erpStatus} />}</span>
      <span className="pmj-go" aria-hidden="true">›</span>
    </button>
  )
}

/** 회사 주요 한 줄. 선정된 것은 순위, 추천안(선정 전 후보)은 '추천' 표시. [근거]를 누르면 왜 주요인지 쉬운 말로 */
function FeatRow({ r, onOpen, onWhy }: { r: MainRow; onOpen(r: MainRow): void; onWhy?(r: MainRow): void }) {
  const rec = r.decision !== 'selected'
  return (
    <div className={`pmj-row${rec ? ' pmj-rec-row' : ''}`} role="button" tabIndex={0} onClick={() => onOpen(r)}
      onKeyDown={(e) => { if (e.key === 'Enter' && e.target === e.currentTarget) onOpen(r) }}>
      <span className="pmj-ico">{rec ? <span className="pmj-rank rec" title="추천안 — 아직 선정하지 않은 후보">추천</span> : <span className="pmj-rank">{r.rank}</span>}</span>
      <span className="pmj-main">
        <span className="pmj-name">{r.name}</span>
        <span className="pmj-code">{r.codeLabel}</span>
        <span className="pmj-sub">{[r.category, r.next ? `다음 ${r.next}` : null, r.pmName && `PM ${r.pmName}`].filter(Boolean).join(' · ')}</span>
      </span>
      <span className="pmj-meta">
        <ErpStatus s={r.erpStatus} />
        {onWhy && (r.parts?.length ?? 0) > 0 && (
          <button className="pmj-why" aria-label={`${r.name} — 주요 프로젝트인 이유`} onClick={(e) => { e.stopPropagation(); onWhy(r) }}>근거</button>
        )}
      </span>
      <span className="pmj-go" aria-hidden="true">›</span>
    </div>
  )
}

/** 점수 근거를 쉬운 말로(금액은 적지 않음 — 11-04). 쉬운 말이 없는 예전(v2) 근거는 금액 대신 규모 말로 바꾼다 */
const plainOf = (p: ScorePart) =>
  p.plain || (p.key === 'amount' ? (p.pts >= 5 ? '회사에서 가장 큰 규모의 계약' : p.pts >= 4 ? '대형 계약' : '중형 계약') : p.label)
const isSize = (p: ScorePart) => (p.group ? p.group === 'size' : ['amount', 'robots', 'new'].includes(p.key))

/** 근거 보기: 왜 회사 주요 프로젝트인지 — 선정/추천 상태, AI 한 줄, '규모가 큰 일'·'지금 움직이는 중' 두 묶음 */
function WhyBody({ r, onOpen }: { r: MainRow; onOpen(r: MainRow): void }) {
  const parts = (r.parts ?? []).filter((p) => p.pts > 0)
  const size = parts.filter(isSize)
  const now = parts.filter((p) => !isSize(p))
  const sizePts = size.reduce((a, p) => a + p.pts, 0)
  const nowPts = now.reduce((a, p) => a + p.pts, 0)
  return (
    <div className="pmj-why-body">
      <p className="pmj-why-state">
        {r.decision === 'selected' ? <b className="sel">선정됨</b> : <b className="rec">추천안</b>}
        {r.decision === 'selected' ? '선정 담당이 회사 주요 프로젝트로 정했습니다.' : '규칙 점수가 높아 추천된 후보입니다(아직 선정 전).'}
      </p>
      {r.reason && <p className="pmj-why-ai"><em>AI</em>{r.reason}</p>}
      <div className="pmj-why-grid">
        <section>
          <h4>규모가 큰 일 <span className="pmj-meter" title={`규모 ${sizePts}/10`}><i style={{ width: `${Math.min(100, sizePts * 10)}%` }} /></span></h4>
          <ul>{size.length ? size.map((p) => <li key={p.key}>{plainOf(p)}</li>) : <li className="none">규모 근거 없음</li>}</ul>
        </section>
        <section>
          <h4>지금 움직이는 중 <span className="pmj-meter now" title={`진행 ${nowPts}/9`}><i style={{ width: `${Math.min(100, (nowPts / 9) * 100)}%` }} /></span></h4>
          <ul>{now.length ? now.map((p) => <li key={p.key}>{plainOf(p)}</li>) : <li className="none">최근 진행 흔적 없음</li>}</ul>
        </section>
      </div>
      {(r.signals?.length ?? 0) > 0 && <p className="pmj-why-sig">살펴볼 점 {r.signals!.map((x) => <span key={x}>⚑ {x}</span>)}</p>}
      <p className="pmj-why-note">매일 아침 ERP 발주·NAS 작업·모듈라 상태로 다시 계산합니다.</p>
      {r.projectId && <button className="pmj-btn pmj-ok" onClick={() => onOpen(r)}>프로젝트 상세 열기 →</button>}
    </div>
  )
}

/** 전체 프로젝트 표: ★ · 프로젝트(이름·코드) · 고객사 · ERP 상태 · 진행 단계 · PM · 최근 활동 */
function AllTable({ items, fav, onFav, onOpen }: { items: Project[]; fav: Set<number>; onFav(id: number): void; onOpen(id: number): void }) {
  if (items.length === 0) return <div className="pmj-empty">검색 조건에 맞는 프로젝트가 없습니다.</div>
  return (
    <div className="pmj-table-wrap">
      <table className="pmj-table">
        <thead><tr><th aria-label="관심" /><th>프로젝트</th><th>고객사</th><th>ERP 상태</th><th>진행 단계</th><th>PM</th><th>최근 활동</th></tr></thead>
        <tbody>
          {items.map((p) => {
            const on = fav.has(p.id)
            return (
              <tr key={p.id} onClick={() => onOpen(p.id)}>
                <td className="pmj-tstar-cell">
                  <button className={`pmj-tstar${on ? ' on' : ''}`} aria-pressed={on} aria-label={on ? '내 관심에서 빼기' : '내 관심에 넣기'}
                    onClick={(e) => { e.stopPropagation(); onFav(p.id) }}>{on ? '★' : '☆'}</button>
                </td>
                <td><a className="pmj-tname" href={`/projects/${p.id}`} onClick={(e) => { e.preventDefault() }}>{p.name}</a><span className="pmj-tcode">{p.code}</span></td>
                <td className="pmj-muted">{p.partnerName ?? '-'}</td>
                <td><ErpStatus s={p.erpStatus} /></td>
                <td>{p.effectiveStage ? <><Stage name={p.effectiveStage} />{p.effectiveStatus === 'done' && <small className="pmj-done">완료</small>}</> : <span className="pmj-dim">–</span>}</td>
                <td className={p.pmName ? '' : 'pmj-dim'}>{p.pmName ?? '미지정'}</td>
                <td className="pmj-muted pmj-last">{p.lastChangedAt ? [md(p.lastChangedAt), p.lastChangedSource, p.lastChangedBy].filter(Boolean).join(' · ') : '—'}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

/** 오른쪽 서랍(휴대폰은 아래에서 올라옴): 내 관심 전체 · 회사 주요 전체 · 새 후보 검토 · 브리핑 근거 */
function MainDrawer({ drawer, canSelect, onClose, onOpen, onFav, onWhy }: {
  drawer: NonNullable<Drawer>; canSelect: boolean; onClose(): void; onOpen(r: { projectId?: number; code: string }): void; onFav(id?: number): void; onWhy(r: MainRow): void
}) {
  const kind = drawer.kind
  const favs = useQuery({ queryKey: ['projects-main-list', 'fav'], queryFn: () => api<MainList>('/api/projects/favorites'), enabled: kind === 'fav' })
  const feats = useQuery({ queryKey: ['projects-main-list', 'feat'], queryFn: () => api<MainList>('/api/projects/featured'), enabled: kind === 'feat' })
  const [schedScope, setSchedScope] = useState<'mine' | 'all'>('mine')
  const [schedKind, setSchedKind] = useState('')
  const scheds = useQuery({
    queryKey: ['projects-main-list', 'sched', schedScope],
    queryFn: () => api<{ items: ScheduleItem[]; isAdmin: boolean }>(`/api/projects/schedule-alerts?scope=${schedScope}`),
    enabled: kind === 'sched',
  })
  const hide = useMutation({
    mutationFn: (b: { code: string; kind: string; all: boolean }) => api<void>('/api/projects/schedule-alerts/hide', { method: 'POST', json: b }),
    onSuccess: (_, b) => {
      toast(b.all ? '회사 전체에서 숨겼습니다.' : '나에게서 숨겼습니다.')
      qc.invalidateQueries({ queryKey: ['projects-main-list'] })
      qc.invalidateQueries({ queryKey: ['projects-main'] })
    },
    onError: (e) => toast(e instanceof Error ? e.message : '숨기지 못했습니다.'),
  })
  const cands = useQuery({ queryKey: ['projects-main-list', 'cand'], queryFn: () => api<{ canSelect: boolean; items: MainRow[] }>('/api/projects/featured/candidates'), enabled: kind === 'cand' })
  const qc = useQueryClient()
  const toast = useToast()
  const decide = useMutation({
    mutationFn: (b: { code: string; action: 'select' | 'exclude' | 'clear' }) => api<void>('/api/projects/featured', { method: 'POST', json: b }),
    onSuccess: (_, b) => {
      toast(b.action === 'select' ? '회사 주요 프로젝트로 정했습니다. 전원에게 보입니다.' : b.action === 'exclude' ? '후보에서 뺐습니다.' : '선정을 해제했습니다.')
      qc.invalidateQueries({ queryKey: ['projects-main-list'] })
      qc.invalidateQueries({ queryKey: ['projects-main'] })
    },
    onError: (e) => toast(e instanceof Error ? e.message : '저장하지 못했습니다.'),
  })

  let title = '', note = '', body: ReactNode = null
  if (kind === 'fav') {
    title = '내 관심 프로젝트'
    note = '★한 프로젝트 전체 · 최근 활동 순 · ★를 다시 누르면 뺍니다'
    body = favs.isLoading ? <Loading /> : (favs.data?.items ?? []).map((r) => (
      <div className="pmj-drow" key={r.code}>
        <button className="pmj-tstar on" aria-label="내 관심에서 빼기" onClick={() => onFav(r.projectId)}>★</button>
        <FavRow r={r} onOpen={onOpen} />
      </div>
    ))
  } else if (kind === 'feat') {
    title = '회사 주요 프로젝트'
    note = '선정 담당이 고른 것을 먼저, 나머지는 규칙 점수 상위 추천안으로 보여 줍니다 · 지금 상태 문장은 위 브리핑에'
    const items = feats.data?.items ?? []
    const sel = items.filter((r) => r.decision === 'selected')
    const rec = items.filter((r) => r.decision !== 'selected')
    body = feats.isLoading ? <Loading /> : (
      <>
        <h4 className="pmj-group">선정 <span>{sel.length}</span></h4>
        {sel.length === 0 ? <p className="pmj-group-empty">아직 선정한 프로젝트가 없습니다.</p> : sel.map((r) => <FeatRow key={r.code} r={r} onOpen={onOpen} onWhy={onWhy} />)}
        <h4 className="pmj-group">추천안 <span>{rec.length}</span><small>규칙 점수 상위 · 선정·제외는 '새 후보 검토'에서</small></h4>
        {rec.map((r) => <FeatRow key={r.code} r={r} onOpen={onOpen} onWhy={onWhy} />)}
      </>
    )
  } else if (kind === 'sched') {
    title = '일정 주의'
    note = '사양정리 › 수행계획서 › 사양서 일정(사람이 올린 문서)을 모듈라·발주·도면 작업과 매일 비교 · 처음 나온 날부터 2주 뒤에 보임(일정 임박은 바로)'
    const all = scheds.data?.items ?? []
    const counts = (k: string) => all.filter((x) => x.kind === k).length
    const items = schedKind ? all.filter((x) => x.kind === schedKind) : all
    body = (
      <>
        <div className="pmj-sched-tools">
          <span className="pmj-seg2">
            <button className={schedScope === 'mine' ? 'on' : ''} onClick={() => setSchedScope('mine')}>관심·주요만</button>
            <button className={schedScope === 'all' ? 'on' : ''} onClick={() => setSchedScope('all')}>회사 전체</button>
          </span>
          {['', 'late', 'soon', 'after_sop', ...(scheds.data?.isAdmin ? ['erp'] : [])].map((k) => (
            <button key={k || 'all'} className={`pmj-chip${schedKind === k ? ' on' : ''}`} onClick={() => setSchedKind(k)}>
              {k ? KIND_LABEL[k] : '전체'} {k ? counts(k) : all.length}
            </button>
          ))}
        </div>
        {scheds.isLoading ? <Loading /> : items.length === 0 ? <p className="pmj-group-empty">해당하는 일정 주의가 없습니다.</p> : items.map((x) => (
          <div className="pmj-sitem" key={`${x.code}-${x.kind}-${x.stage}`}>
            <span className={`pmj-dday ${KIND_CLASS[x.kind]}`}>{dday(x.days)}</span>
            <div className="pmj-sitem-main">
              <span className="pmj-stag"><i className={KIND_CLASS[x.kind]}>{KIND_LABEL[x.kind]}</i>{x.scope === 'fav' ? '내 관심' : x.scope === 'feat' ? '회사 주요' : ''}</span>
              <button className="pmj-cand-name" onClick={() => onOpen(x)}>{x.name}</button>
              <span className="pmj-code">{x.codeLabel} · 기준 {x.stageName} {x.dueDate}</span>
              <p className="pmj-sreason">{x.reason}</p>
              {x.evidence?.line && <p className="pmj-sline">“{x.evidence.line}” <span>— {x.evidence.doc}{x.evidence.page ? ` ${x.evidence.page}` : ''}</span></p>}
            </div>
            <div className="pmj-cand-act">
              <button className="pmj-btn" disabled={hide.isPending} onClick={() => hide.mutate({ code: x.code, kind: x.kind, all: false })}>나만 숨기기</button>
              {scheds.data?.isAdmin && <button className="pmj-btn" disabled={hide.isPending} onClick={() => hide.mutate({ code: x.code, kind: x.kind, all: true })}>전체 숨기기</button>}
            </div>
          </div>
        ))}
      </>
    )
  } else if (kind === 'cand') {
    title = '새 후보 검토'
    note = '매일 06:30 규칙 점수(규모 + 최근 흔적) · 점수 근거는 DB 값, 이유 문장은 사내 AI · [선정]해야 전원에게 보입니다'
    body = cands.isLoading ? <Loading /> : (cands.data?.items ?? []).map((c) => (
      <div className="pmj-cand" key={c.code}>
        <span className="pmj-score" title="점수">{c.score}</span>
        <div className="pmj-cand-main">
          <button className="pmj-cand-name" onClick={() => onOpen(c)}>{c.name}</button>
          <span className="pmj-code">{c.codeLabel} · {c.category}{c.erpStatus ? ` · ERP ${c.erpStatus}` : ''}</span>
          {c.reason && <p className="pmj-reason"><b>AI</b>{c.reason}</p>}
          <span className="pmj-parts">
            {(c.parts ?? []).map((p) => <span key={p.key} className={p.pts < 0 ? 'neg' : ''}>{p.label} <i>{p.pts > 0 ? `+${p.pts}` : p.pts}</i></span>)}
            {(c.signals ?? []).map((s) => <span key={s} className="sig">⚑ {s}</span>)}
          </span>
        </div>
        {(canSelect || cands.data?.canSelect) && (
          <div className="pmj-cand-act">
            {c.decision === 'selected' ? <button className="pmj-btn done" onClick={() => decide.mutate({ code: c.code, action: 'clear' })}>선정됨 ✓</button>
              : <button className="pmj-btn pmj-ok" disabled={decide.isPending} onClick={() => decide.mutate({ code: c.code, action: 'select' })}>선정</button>}
            {c.decision === 'excluded' ? <button className="pmj-btn" onClick={() => decide.mutate({ code: c.code, action: 'clear' })}>제외 취소</button>
              : c.decision !== 'selected' && <button className="pmj-btn" disabled={decide.isPending} onClick={() => decide.mutate({ code: c.code, action: 'exclude' })}>제외</button>}
          </div>
        )}
      </div>
    ))
  } else if (drawer.kind === 'why') {
    const r = drawer.row
    title = r.name
    note = `${r.codeLabel}${r.category ? ` · ${r.category}` : ''} · 왜 회사 주요 프로젝트인가`
    body = <WhyBody r={r} onOpen={onOpen} />
  } else if (drawer.kind === 'evidence') {
    const b = drawer.brief
    title = b.name
    note = `${b.codeLabel} · ${b.source === 'ai' ? '사내 AI 문장(숫자는 넘겨준 사실에 있는 것만 통과)' : '규칙 문장(사내 AI 문장이 검사에 걸리거나 없을 때)'} · ${md(b.madeAt)} ${hm(b.madeAt)} 만듦`
    body = (
      <div className="pmj-evid">
        <p className="pmj-evid-s">{b.sentence}</p>
        <h4>근거</h4>
        <ul>
          {(b.evidence ?? []).length === 0 && <li>근거 기록이 없습니다.</li>}
          {(b.evidence ?? []).map((e, i) => (
            <li key={i}><span className={`pmj-ek pmj-ek-${e.kind}`}>{{ modular: '모듈라', nas: 'NAS', po: 'ERP 발주', post: '최근 글' }[e.kind]}</span>{e.label}{e.path && <code>{e.path}</code>}</li>
          ))}
        </ul>
        {b.projectId && <button className="pmj-btn pmj-ok" onClick={() => onOpen(b)}>프로젝트 상세 열기 →</button>}
      </div>
    )
  }
  // 본문(main)은 isolation으로 따로 쌓이므로, 위쪽 메뉴 막대보다 위에 뜨도록 body에 붙인다
  return createPortal(
    <div className="pmj">
      <div className="pmj-backdrop" onClick={onClose} />
      <aside className="pmj-drawer" role="dialog" aria-modal="true" aria-label={title}>
        <header><h3>{title}</h3><button aria-label="닫기" onClick={onClose}>×</button></header>
        <p className="pmj-dnote">{note}</p>
        <div className="pmj-dbody">{body}</div>
      </aside>
    </div>,
    document.body,
  )
}
