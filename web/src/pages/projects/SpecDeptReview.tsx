import { useQuery } from '@tanstack/react-query'
import { useEffect, useMemo, useState } from 'react'
import { API_BASE, ApiError, api, tokenStore } from '../../api/client'
import { useAuth } from '../../auth/AuthContext'
import { ErrorBox, Loading, Modal } from '../../components/ui'
import '../../styles/spec-dept.css'

// ---- API 타입(서버 Modules/Spec/SpecDeptModule.cs, DyOne.Spec/Dept)
type Hit = { page: number; text: string }
type RowValue = { key: string; value: string; pages: number[] }
type Item = { id: string; title: string; incident?: string | null; askIfMissing: boolean; rows: RowValue[]; hits: Hit[]; hitPages: number }
type Role = { key: string; dept: string; group: string; does: string; budget?: string | null; items: Item[] }
type DeptResponse = { draftReady: boolean; fileName: string; roles: Role[] }

type Status = 'todo' | 'ok' | 'ask' | 'decide' | 'na'
const STATUS: { key: Status; label: string }[] = [
  { key: 'todo', label: '미확인' },
  { key: 'ok', label: '확인' },
  { key: 'ask', label: '고객 질의 필요' },
  { key: 'decide', label: '내부 결정 필요' },
  { key: 'na', label: '해당 없음' },
]
const LABEL = Object.fromEntries(STATUS.map((s) => [s.key, s.label])) as Record<Status, string>
type Mark = { status: Status; note: string; by: string; at: string }
type Marks = Record<string, Mark>

/**
 * 임시 저장: 상태·코멘트는 아직 DB에 두지 않는다(화면 먼저, 표 설계는 사용자 확인 뒤 — 2026-10-28).
 * 이 브라우저에만 남으므로 다른 사람과 공유되지 않는다.
 */
const storeKey = (projectId: number, fileId: number) => `dyone.spec-dept.${projectId}.${fileId}`
function loadMarks(key: string): Marks {
  try { return JSON.parse(localStorage.getItem(key) ?? '{}') as Marks } catch { return {} }
}
function saveMarks(key: string, marks: Marks) {
  try { localStorage.setItem(key, JSON.stringify(marks)) } catch { /* 저장 못 해도 화면은 그대로 */ }
}

/** 근거 한 줄(pptx '사양서 근거' 칸): 초안 값 → 본문에서 찾은 쪽 */
function evidenceText(it: Item) {
  const rows = it.rows.map((r) => `${r.key}: ${r.value}${r.pages.length ? ` (p${r.pages.join(',')})` : ''}`)
  const hits = it.hits.slice(0, 2).map((h) => `p${h.page} ${h.text}`)
  const all = [...rows, ...hits]
  if (all.length === 0) return it.askIfMissing ? '사양서에 없음 — 확인 필요' : '사양서에서 못 찾음'
  return all.join('\n')
}

/**
 * 사양서 분석 '부서별 확인': 사양정리 초안을 만든 뒤 역할(부서)마다 사양서에서 확인할 항목·근거 쪽을 보여 주고
 * 항목마다 상태(미확인·확인·고객 질의 필요·내부 결정 필요·해당 없음)를 정한다. pptx는 사양정리 뒤에 역할별 한 장씩 붙인다.
 * 부서 이름은 임시 연결(역할↔부서 연결표가 생기면 그쪽을 쓴다).
 */
export function SpecDeptReview({ projectId, fileId }: { projectId: number; fileId: number }) {
  const { user } = useAuth()
  const q = useQuery({
    queryKey: ['spec-dept', projectId, fileId],
    queryFn: () => api<DeptResponse>(`/api/projects/${projectId}/spec-dept?fileId=${fileId}`),
  })
  const key = storeKey(projectId, fileId)
  const [marks, setMarks] = useState<Marks>(() => loadMarks(key))
  useEffect(() => setMarks(loadMarks(key)), [key])
  const [group, setGroup] = useState<string>('전체')
  const [only, setOnly] = useState<'all' | 'open' | Status>('all')
  const [open, setOpen] = useState<Record<string, boolean>>({})
  const [page, setPage] = useState<{ page: number; mark?: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<unknown>(null)

  const roles = q.data?.roles ?? []
  const groups = useMemo(() => ['전체', ...Array.from(new Set(roles.map((r) => r.group)))], [roles])
  const statusOf = (r: Role, it: Item): Status => marks[`${r.key}.${it.id}`]?.status ?? 'todo'

  const setMark = (r: Role, it: Item, patch: Partial<Mark>) => {
    const id = `${r.key}.${it.id}`
    // 연달아 바꿔도(코멘트 blur 직후 상태 클릭 등) 앞 변경이 사라지지 않게 직전 값 기준으로 합친다
    setMarks((cur) => {
      const prev = cur[id] ?? { status: 'todo' as Status, note: '', by: '', at: '' }
      const next = { ...cur, [id]: { ...prev, ...patch, by: user?.name ?? '', at: new Date().toISOString() } }
      saveMarks(key, next)
      return next
    })
  }

  const visible = roles.filter((r) => group === '전체' || r.group === group)
  const itemShown = (r: Role, it: Item) => {
    const s = statusOf(r, it)
    return only === 'all' || (only === 'open' ? s === 'todo' || s === 'ask' || s === 'decide' : s === only)
  }

  const pptx = async () => {
    setBusy(true); setErr(null)
    try {
      const body = {
        roles: visible.map((r) => ({
          dept: r.dept, group: r.group, budget: r.budget ?? null,
          items: r.items.map((it) => {
            const m = marks[`${r.key}.${it.id}`]
            return { title: it.title, status: LABEL[statusOf(r, it)], evidence: evidenceText(it), incident: it.incident ?? null, note: m?.note ?? '', by: m?.by ?? '' }
          }),
        })),
      }
      const headers = new Headers({ 'Content-Type': 'application/json' })
      const token = tokenStore.get()
      if (token) headers.set('Authorization', `Bearer ${token}`)
      const res = await fetch(`${API_BASE}/api/projects/${projectId}/spec-dept/pptx?fileId=${fileId}`, { method: 'POST', headers, body: JSON.stringify(body) })
      if (!res.ok) {
        const j = await res.json().catch(() => null)
        throw new ApiError(res.status, j?.message ?? j?.detail ?? `pptx를 만들지 못했습니다. (${res.status})`)
      }
      const url = URL.createObjectURL(await res.blob())
      const a = document.createElement('a')
      a.href = url
      a.download = '사양정리 초안(부서별 확인).pptx'
      document.body.appendChild(a); a.click(); a.remove()
      URL.revokeObjectURL(url)
    } catch (e) { setErr(e) } finally { setBusy(false) }
  }

  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  if (!q.data) return null

  return (
    <div className="sdr">
      <div className="sdr-head">
        <div>
          <b>부서별 확인</b>
          <small>
            역할마다 이 사양서에서 확인할 항목과 근거 쪽입니다. 항목마다 상태를 정하면 아래 현황판에 모입니다.
            <br />지금은 상태·코멘트를 <b>이 브라우저에만</b> 임시로 저장합니다(공유 안 됨). 부서 이름은 임시 연결입니다.
          </small>
        </div>
        <button className="primary" disabled={busy || !q.data.draftReady} onClick={pptx}
          title={q.data.draftReady ? '사양정리 초안 pptx 뒤에 현황 1장 + 역할별 한 장' : '사양정리 초안을 먼저 만들어 주세요'}>
          {busy ? 'pptx 만드는 중…' : 'pptx 내려받기(부서별 장 포함)'}
        </button>
      </div>
      {!q.data.draftReady && <div className="notice spec-warn">사양정리 초안이 아직 없어 초안 값 없이 사양서 본문 근거만 보입니다. pptx는 초안을 만든 뒤 내려받을 수 있습니다.</div>}
      {err != null && <ErrorBox error={err} />}

      <Board roles={visible} statusOf={statusOf} onRole={(k) => {
        setOpen((o) => ({ ...o, [k]: true }))
        document.getElementById(`sdr-${k}`)?.scrollIntoView({ behavior: 'smooth', block: 'start' })
      }} />

      <div className="sdr-filters">
        <div className="sdr-seg">
          {groups.map((g) => <button key={g} className={group === g ? 'active' : ''} onClick={() => setGroup(g)}>{g}</button>)}
        </div>
        <div className="sdr-seg">
          {([['all', '전체 항목'], ['open', '남은 것']] as const).map(([k, l]) => (
            <button key={k} className={only === k ? 'active' : ''} onClick={() => setOnly(k)}>{l}</button>
          ))}
          {STATUS.filter((s) => s.key !== 'todo').map((s) => (
            <button key={s.key} className={only === s.key ? 'active' : ''} onClick={() => setOnly(s.key)}>{s.label}</button>
          ))}
        </div>
        <button className="text-button" onClick={() => setOpen(Object.fromEntries(visible.map((r) => [r.key, !visible.every((x) => open[x.key])])))}>
          {visible.every((r) => open[r.key]) ? '모두 접기' : '모두 펼치기'}
        </button>
      </div>

      {visible.map((r) => {
        const items = r.items.filter((it) => itemShown(r, it))
        const done = r.items.filter((it) => statusOf(r, it) !== 'todo').length
        const isOpen = open[r.key] ?? false
        return (
          <section key={r.key} id={`sdr-${r.key}`} className="sdr-role">
            <button className="sdr-role-head" onClick={() => setOpen((o) => ({ ...o, [r.key]: !isOpen }))}>
              <span className="sdr-caret">{isOpen ? '▾' : '▸'}</span>
              <b>{r.dept}</b>
              <small>{r.group} · {r.does}</small>
              {r.budget && <span className="badge mini" title="ERP 실행예산 항목">예산 {r.budget}</span>}
              <span className="sdr-progress" title="상태를 정한 항목 / 전체">
                <i style={{ width: `${(done / Math.max(1, r.items.length)) * 100}%` }} />
                <em>{done}/{r.items.length}</em>
              </span>
            </button>
            {isOpen && (
              <div className="sdr-items">
                {items.length === 0 && <div className="empty">이 조건에 맞는 항목이 없습니다.</div>}
                {items.map((it) => {
                  const m = marks[`${r.key}.${it.id}`]
                  const s = m?.status ?? 'todo'
                  const missing = it.rows.length === 0 && it.hits.length === 0
                  return (
                    <div key={it.id} className={`sdr-item st-${s}`}>
                      <div className="sdr-item-main">
                        <div className="sdr-item-title">
                          <b>{it.title}</b>
                          {it.incident && <span className="sdr-incident" title="같은 유형의 과거 사고">⚠ {it.incident}</span>}
                        </div>
                        {missing ? (
                          <div className={it.askIfMissing ? 'sdr-missing ask' : 'sdr-missing'}>
                            {it.askIfMissing ? '사양서에 없음 — 확인 필요(사고가 잦던 항목)' : '사양서에서 못 찾음'}
                          </div>
                        ) : (
                          <ul className="sdr-ev">
                            {it.rows.map((rv) => (
                              <li key={rv.key}>
                                <span className="sdr-rowkey">사양정리 {rv.key}</span>
                                <span className="sdr-val">{rv.value.length > 220 ? rv.value.slice(0, 220) + '…' : rv.value}</span>
                                {rv.pages.map((p) => <button key={p} className="spec-page" onClick={() => setPage({ page: p })}>p{p}</button>)}
                              </li>
                            ))}
                            {it.hits.map((h) => (
                              <li key={h.page}>
                                <button className="spec-page" onClick={() => setPage({ page: h.page, mark: h.text })}>p{h.page}</button>
                                <span>{h.text}</span>
                              </li>
                            ))}
                            {it.hitPages > it.hits.length && <li className="sdr-more">그 밖에 {it.hitPages - it.hits.length}쪽에도 나옵니다.</li>}
                          </ul>
                        )}
                      </div>
                      <div className="sdr-item-side">
                        <div className="sdr-status">
                          {STATUS.map((x) => (
                            <button key={x.key} className={`st-${x.key}${s === x.key ? ' active' : ''}`} onClick={() => setMark(r, it, { status: x.key })}>{x.label}</button>
                          ))}
                        </div>
                        <input
                          placeholder={s === 'ask' ? '고객에게 물을 내용' : s === 'decide' ? '정할 내용·정할 사람' : '코멘트'}
                          defaultValue={m?.note ?? ''}
                          onBlur={(e) => e.target.value !== (m?.note ?? '') && setMark(r, it, { note: e.target.value })}
                        />
                        {m?.by && <small>{m.by} · {new Date(m.at).toLocaleString('ko-KR', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' })}</small>}
                      </div>
                    </div>
                  )
                })}
              </div>
            )}
          </section>
        )
      })}

      {page && <PageModal projectId={projectId} fileId={fileId} page={page.page} mark={page.mark} onClose={() => setPage(null)} onPage={(p) => setPage({ page: p })} />}
    </div>
  )
}

/** 영업·PM용 현황판: 역할 × 상태 건수(누르면 그 역할 카드로) */
function Board({ roles, statusOf, onRole }: { roles: Role[]; statusOf(r: Role, it: Item): Status; onRole(key: string): void }) {
  const total = Object.fromEntries(STATUS.map((s) => [s.key, 0])) as Record<Status, number>
  for (const r of roles) for (const it of r.items) total[statusOf(r, it)]++
  return (
    <div className="sdr-board">
      <table>
        <thead>
          <tr>
            <th>부서(역할)</th>
            {STATUS.map((s) => <th key={s.key} className={`st-${s.key}`}>{s.label}<small>{total[s.key]}</small></th>)}
          </tr>
        </thead>
        <tbody>
          {roles.map((r) => (
            <tr key={r.key} onClick={() => onRole(r.key)}>
              <td><b>{r.dept}</b><small>{r.group}</small></td>
              {STATUS.map((s) => {
                const n = r.items.filter((it) => statusOf(r, it) === s.key).length
                return <td key={s.key} className={n && s.key !== 'todo' && s.key !== 'ok' && s.key !== 'na' ? `hot st-${s.key}` : ''}>{n || ''}</td>
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

/** 사양서 한 쪽(그림, PDF가 아니면 글). mark = 노랗게 칠할 근거 줄 */
function PageModal({ projectId, fileId, page, mark, onClose, onPage }: {
  projectId: number; fileId: number; page: number; mark?: string; onClose(): void; onPage(p: number): void
}) {
  const [img, setImg] = useState<string | null>(null)
  const [lines, setLines] = useState<string[] | null>(null)
  const [err, setErr] = useState<string | null>(null)
  useEffect(() => {
    let url: string | null = null
    let alive = true
    setImg(null); setLines(null); setErr(null)
    const headers = new Headers()
    const token = tokenStore.get()
    if (token) headers.set('Authorization', `Bearer ${token}`)
    // 근거 줄은 앞뒤 '…'를 빼고 앞부분만 칠한다(쪽 안에서 같은 글을 찾음)
    const m = mark ? `&mark=${encodeURIComponent(mark.replace(/^…|…$/g, '').slice(0, 60))}` : ''
    fetch(`${API_BASE}/api/projects/${projectId}/spec-draft/page?fileId=${fileId}&page=${page}&dpi=130${m}`, { headers })
      .then(async (res) => {
        if (!res.ok) throw new Error(`쪽을 불러오지 못했습니다. (${res.status})`)
        if (res.headers.get('content-type')?.includes('image')) {
          url = URL.createObjectURL(await res.blob())
          if (alive) setImg(url)
        } else {
          const j = await res.json()
          if (alive) setLines(j.lines ?? [])
        }
      })
      .catch((e: Error) => alive && setErr(e.message))
    return () => { alive = false; if (url) URL.revokeObjectURL(url) }
  }, [projectId, fileId, page, mark])
  return (
    <Modal title={`사양서 ${page}쪽`} onClose={onClose}
      footer={<>
        <button type="button" className="secondary" disabled={page <= 1} onClick={() => onPage(page - 1)}>이전 쪽</button>
        <button type="button" className="secondary" onClick={() => onPage(page + 1)}>다음 쪽</button>
      </>}>
      <div className="sdr-pageview">
        {err && <div className="notice spec-warn">{err}</div>}
        {!img && !lines && !err && <Loading />}
        {img && <img src={img} alt={`사양서 ${page}쪽`} />}
        {lines && <pre>{lines.join('\n')}</pre>}
      </div>
    </Modal>
  )
}
