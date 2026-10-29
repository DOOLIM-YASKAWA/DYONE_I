import { useQuery } from '@tanstack/react-query'
import { useEffect, useRef, useState, type FormEvent, type PointerEvent as ReactPointerEvent } from 'react'
import { api } from '../../api/client'
import type { ProjectFile } from '../../api/types'
import '../../styles/sales-agent.css'

/**
 * 영업 단계 '영업 에이전트' — 화면에 떠 있는 창(끌어서 옮기기·접기) — [화면만] 사내 AI 서버가 아직 연결되지 않아 답은 예시 안내만 보여 준다.
 * 연결 후: 고른 사양서(쪽별 본문)와 영업 일지를 근거로 답하고, 답 아래 버튼으로 초안·질의서·영업 일지에 옮긴다(사람이 눌러야 반영).
 */
type Tab = '영업 일지' | '사양서 분석'
type Msg = { role: 'user' | 'agent'; text: string; pending?: boolean }

const ACTIONS: Record<Tab, { label: string; prompt: string }[]> = {
  '사양서 분석': [
    { label: '우리 범위만 요약', prompt: '이 사양서에서 두림 범위만 요약해줘' },
    { label: '이전 판과 비교', prompt: '이전 판 사양서와 바뀐 점을 정리해줘' },
    { label: '부서별 확인 사항', prompt: '설계·전장·모듈라·시운전 부서별로 확인할 사항을 정리해줘' },
    { label: '고객 질의서 초안', prompt: '애매한 조항을 고객 질의서 초안으로 만들어줘' },
    { label: '영문 사양서 번역', prompt: '이 사양서의 두림 관련 조항을 한국어로 옮겨줘' },
  ],
  '영업 일지': [
    { label: '최근 미팅 요약', prompt: '이 프로젝트의 최근 미팅 내용을 요약해줘' },
    { label: '다음 할 일 모아보기', prompt: '기한이 남은 다음 할 일을 모아줘' },
    { label: '고객사 이력 요약', prompt: '이 고객사·공장과의 지난 프로젝트와 미팅 이력을 요약해줘' },
  ],
}

const EXAMPLES: Record<Tab, string[]> = {
  '사양서 분석': ['방폭이 요구되는 기기만 뽑아줘', 'CCV 칼라 수가 몇 개야? 스페어 포함?', '비슷한 과거 프로젝트랑 다른 점 알려줘'],
  '영업 일지': ['이전에 광명2에서 만났던 고객과의 미팅 내용 요약해줘', '지난달 이 고객이 요청한 사항 뭐였지?'],
}

const NOT_CONNECTED = '사내 AI 서버가 아직 연결되지 않아 답할 수 없습니다. 연결되면 이 자리에 답과 근거(사양서 쪽 번호·영업 일지 항목)가 나오고, 아래 버튼으로 초안·질의서·영업 일지에 옮길 수 있습니다.'

const PANEL_W = 360
const PANEL_H = 620
const POS_KEY = 'dyone.salesAgent.pos'
const OPEN_KEY = 'dyone.salesAgent.open'
type Pos = { x: number; y: number }

/** 화면 밖으로 나가지 않게: 접힌 버튼·펼친 창 전체가 화면 안에 들어오도록 */
const clamp = (p: Pos, w: number, h: number): Pos => ({
  x: Math.min(Math.max(8, p.x), Math.max(8, window.innerWidth - w - 8)),
  y: Math.min(Math.max(8, p.y), Math.max(8, window.innerHeight - h - 8)),
})
const load = <T,>(key: string, fallback: T): T => {
  try { const v = localStorage.getItem(key); return v == null ? fallback : (JSON.parse(v) as T) } catch { return fallback }
}
const save = (key: string, v: unknown) => {
  try { localStorage.setItem(key, JSON.stringify(v)) } catch { /* 저장 못 해도 화면은 동작 */ }
}

/** 마우스로 끌어 옮기기. 4px 넘게 움직이지 않으면 클릭으로 본다(onClick) */
function useDrag(pos: Pos, setPos: (p: Pos) => void, size: () => { w: number; h: number }, onClick?: () => void) {
  return (e: ReactPointerEvent<HTMLElement>) => {
    if (e.button !== 0 || (e.target as HTMLElement).closest('button:not(.sa-fab), select, input')) return
    e.preventDefault()
    const start = { x: e.clientX, y: e.clientY }
    const origin = pos
    let moved = false
    let last = pos
    const move = (ev: PointerEvent) => {
      const dx = ev.clientX - start.x, dy = ev.clientY - start.y
      if (!moved && Math.hypot(dx, dy) < 4) return
      moved = true
      const { w, h } = size()
      last = clamp({ x: origin.x + dx, y: origin.y + dy }, w, h)
      setPos(last)
    }
    const up = () => {
      window.removeEventListener('pointermove', move)
      window.removeEventListener('pointerup', up)
      if (moved) save(POS_KEY, last)
      else onClick?.()
    }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', up)
  }
}

export function SalesAgentPanel({ projectId, tab }: { projectId: number; tab: Tab }) {
  // 화면에 떠 있는 창: 머리 부분을 끌어 옮기고, 접으면 작은 버튼으로 남는다(위치·접힘은 이 PC에 기억)
  const [pos, setPos] = useState<Pos>(() =>
    clamp(load(POS_KEY, { x: window.innerWidth - PANEL_W - 24, y: 96 }), 160, 48))
  const [open, setOpenState] = useState(() => load(OPEN_KEY, true))
  const setOpen = (v: boolean) => {
    setOpenState(v); save(OPEN_KEY, v)
    // 펼칠 때 창이 잘리지 않게 안쪽으로 당긴다(접힌 버튼을 화면 끝에 두었을 때)
    if (v) {
      const next = clamp(pos, PANEL_W, Math.min(PANEL_H, window.innerHeight - 16))
      setPos(next); save(POS_KEY, next)
    }
  }
  const win = useRef<HTMLElement | null>(null)
  const size = () => ({ w: win.current?.offsetWidth ?? PANEL_W, h: win.current?.offsetHeight ?? PANEL_H })
  useEffect(() => {
    const fit = () => setPos((p) => clamp(p, win.current?.offsetWidth ?? PANEL_W, win.current?.offsetHeight ?? PANEL_H))
    window.addEventListener('resize', fit)
    return () => window.removeEventListener('resize', fit)
  }, [])
  const dragWin = useDrag(pos, setPos, size)
  const dragFab = useDrag(pos, setPos, size, () => setOpen(true))

  const files = useQuery({ queryKey: ['project-files', projectId], queryFn: () => api<ProjectFile[]>(`/api/projects/${projectId}/files`) })
  const specFiles = (files.data ?? []).filter((f) => f.categoryCode === 'spec')
  const [specId, setSpecId] = useState<number | null>(null)
  const spec = specFiles.find((f) => f.id === specId) ?? specFiles[0]

  const [chats, setChats] = useState<Record<Tab, Msg[]>>({ '영업 일지': [], '사양서 분석': [] })
  const messages = chats[tab]
  const box = useRef<HTMLDivElement>(null)
  useEffect(() => { box.current?.scrollTo({ top: box.current.scrollHeight }) }, [messages.length])

  const ask = (text: string) => {
    const q = text.trim()
    if (!q) return
    setChats((c) => ({ ...c, [tab]: [...c[tab], { role: 'user', text: q }, { role: 'agent', text: NOT_CONNECTED, pending: true }] }))
  }
  const submit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = e.currentTarget
    ask(String(new FormData(form).get('q') ?? ''))
    form.reset()
  }

  if (!open) {
    return (
      <button ref={(el) => { win.current = el }} className="sa-fab" style={{ left: pos.x, top: pos.y }}
        onPointerDown={dragFab} onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setOpen(true) } }} title="영업 에이전트 펼치기 (끌어서 옮길 수 있음)">
        <span className="sa-icon">✦</span>영업 에이전트
      </button>
    )
  }

  return (
    <aside ref={(el) => { win.current = el }} className="sa-panel" style={{ left: pos.x, top: pos.y }} aria-label="영업 에이전트">
      <header className="sa-head" onPointerDown={dragWin} title="끌어서 옮길 수 있습니다">
        <div>
          <b><span className="sa-icon">✦</span>영업 에이전트</b>
          <span className="badge mini amber">사내 AI 연결 전</span>
        </div>
        <button className="text-button" onClick={() => setOpen(false)} title="접기">접기 —</button>
      </header>

      <div className="sa-context">
        {tab === '사양서 분석' ? (
          specFiles.length > 0 ? (
            <label>
              <em>대상 사양서</em>
              <select value={spec?.id ?? ''} onChange={(e) => setSpecId(Number(e.target.value))}>
                {specFiles.map((f) => <option key={f.id} value={f.id}>{f.fileName}</option>)}
              </select>
            </label>
          ) : <span className="sa-muted">왼쪽에 사양서를 올리면 그 사양서를 읽고 답합니다.</span>
        ) : <span className="sa-muted">이 프로젝트의 영업 일지와 같은 고객사·공장 기록을 보고 답합니다.</span>}
      </div>

      <div className="sa-actions">
        {ACTIONS[tab].map((a) => (
          <button key={a.label} className="sa-action" onClick={() => ask(a.prompt)}>{a.label}</button>
        ))}
      </div>

      <div className="sa-messages" ref={box}>
        {messages.length === 0 ? (
          <div className="sa-start">
            <p>이 프로젝트 범위에서 묻고 답합니다. 답에는 근거가 함께 나오고, 사람이 확인하고 눌러야 초안·일지에 반영됩니다.</p>
            <small>이렇게 물어볼 수 있습니다</small>
            {EXAMPLES[tab].map((e) => (
              <button key={e} className="sa-example" onClick={() => ask(e)}>“{e}”</button>
            ))}
          </div>
        ) : messages.map((m, i) => (
          <div key={i} className={`sa-msg ${m.role}`}>
            {m.text}
            {m.pending && (
              <div className="sa-msg-actions">
                {tab === '사양서 분석' ? (
                  <>
                    <button disabled>초안에 반영</button>
                    <button disabled>질의서에 추가</button>
                  </>
                ) : null}
                <button disabled>영업 일지에 저장</button>
              </div>
            )}
          </div>
        ))}
      </div>

      <form className="sa-form" onSubmit={submit}>
        <input name="q" placeholder={tab === '사양서 분석' ? '이 사양서에 대해 물어보세요' : '영업 기록에 대해 물어보세요'} autoComplete="off" />
        <button className="primary" type="submit">보내기</button>
      </form>
    </aside>
  )
}
