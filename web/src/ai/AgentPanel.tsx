import { useMutation } from '@tanstack/react-query'
import { useEffect, useRef, useState, type FormEvent, type PointerEvent as ReactPointerEvent } from 'react'
import { agentChat, useAiStatus, type AgentTurn } from './aiClient'
import '../styles/sales-agent.css'

/**
 * 화면마다 붙이는 AI 에이전트 창(떠 있는 창 — 머리를 끌어 옮기고, 접으면 작은 버튼).
 * 쓰는 법: <AgentPanel agent="project-docs" title="자료함 도우미" context={() => '지금 화면 상황'} actions={[…]} />
 * 에이전트 역할(프롬프트)은 서버 AiAgentModule.Agents에서 정한다. 대화는 화면을 닫으면 사라진다(저장하지 않음).
 */
type Pos = { x: number; y: number }
const W = 380
const H = 600
const clamp = (p: Pos, w: number, h: number): Pos => ({
  x: Math.min(Math.max(8, p.x), Math.max(8, window.innerWidth - w - 8)),
  y: Math.min(Math.max(8, p.y), Math.max(8, window.innerHeight - h - 8)),
})
const load = <T,>(key: string, fallback: T): T => {
  try { const v = localStorage.getItem(key); return v == null ? fallback : (JSON.parse(v) as T) } catch { return fallback }
}
const store = (key: string, v: unknown) => { try { localStorage.setItem(key, JSON.stringify(v)) } catch { /* 저장 못 해도 동작 */ } }

export function AgentPanel({ agent, title, context, actions = [], examples = [], placeholder }: {
  agent: string
  title: string
  /** 물을 때마다 부른다 — 지금 화면 상황을 글자로 */
  context?: () => string
  /** 한 번에 누르는 질문 */
  actions?: { label: string; prompt: string }[]
  examples?: string[]
  placeholder?: string
}) {
  const status = useAiStatus()
  const posKey = `dyone.agent.${agent}.pos`
  const openKey = `dyone.agent.${agent}.open`
  const [pos, setPos] = useState<Pos>(() => clamp(load(posKey, { x: window.innerWidth - W - 24, y: 96 }), 160, 48))
  const [open, setOpenState] = useState(() => load(openKey, false))
  const win = useRef<HTMLElement | null>(null)
  const box = useRef<HTMLDivElement>(null)
  const [turns, setTurns] = useState<AgentTurn[]>([])

  const setOpen = (v: boolean) => {
    setOpenState(v); store(openKey, v)
    if (v) { const next = clamp(pos, W, Math.min(H, window.innerHeight - 16)); setPos(next); store(posKey, next) }
  }
  useEffect(() => { box.current?.scrollTo({ top: box.current.scrollHeight }) }, [turns.length])
  useEffect(() => {
    const fit = () => setPos((p) => clamp(p, win.current?.offsetWidth ?? W, win.current?.offsetHeight ?? H))
    window.addEventListener('resize', fit)
    return () => window.removeEventListener('resize', fit)
  }, [])

  // 끌어 옮기기(4px 넘게 움직이지 않으면 클릭)
  const drag = (onClick?: () => void) => (e: ReactPointerEvent<HTMLElement>) => {
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
      last = clamp({ x: origin.x + dx, y: origin.y + dy }, win.current?.offsetWidth ?? W, win.current?.offsetHeight ?? H)
      setPos(last)
    }
    const up = () => {
      window.removeEventListener('pointermove', move)
      window.removeEventListener('pointerup', up)
      if (moved) store(posKey, last)
      else onClick?.()
    }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', up)
  }

  const ask = useMutation({
    mutationFn: (next: AgentTurn[]) => agentChat(agent, next, context?.()),
    onSuccess: (r) => setTurns((t) => [...t, { role: 'assistant', content: r.reply }]),
    onError: (e) => setTurns((t) => [...t, { role: 'assistant', content: `⚠ ${e instanceof Error ? e.message : '답하지 못했습니다.'}` }]),
  })
  const send = (text: string) => {
    const q = text.trim()
    if (!q || ask.isPending) return
    const next: AgentTurn[] = [...turns.filter((t) => !t.content.startsWith('⚠')), { role: 'user', content: q }]
    setTurns((t) => [...t, { role: 'user', content: q }])
    ask.mutate(next)
  }
  const submit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = e.currentTarget
    send(String(new FormData(form).get('q') ?? ''))
    form.reset()
  }
  const ready = !!status.data?.configured

  if (!open) {
    return (
      <button ref={(el) => { win.current = el }} className="sa-fab" style={{ left: pos.x, top: pos.y }}
        onPointerDown={drag(() => setOpen(true))} onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setOpen(true) } }}
        title={`${title} 펼치기 (끌어서 옮길 수 있음)`}>
        <span className="sa-icon">✦</span>{title}
      </button>
    )
  }
  return (
    <aside ref={(el) => { win.current = el }} className="sa-panel" style={{ left: pos.x, top: pos.y }} aria-label={title}>
      <header className="sa-head" onPointerDown={drag()} title="끌어서 옮길 수 있습니다">
        <div>
          <b><span className="sa-icon">✦</span>{title}</b>
          <span className={`badge mini ${ready ? 'green' : 'amber'}`}>{status.isLoading ? '확인 중' : ready ? `사내 AI · ${status.data?.model}` : '사내 AI 연결 전'}</span>
        </div>
        <span>
          {turns.length > 0 && <button className="text-button" onClick={() => setTurns([])} title="대화 지우기">새 대화</button>}
          <button className="text-button" onClick={() => setOpen(false)} title="접기">접기 —</button>
        </span>
      </header>
      {actions.length > 0 && (
        <div className="sa-actions">
          {actions.map((a) => <button key={a.label} className="sa-action" disabled={!ready || ask.isPending} onClick={() => send(a.prompt)}>{a.label}</button>)}
        </div>
      )}
      <div className="sa-messages" ref={box}>
        {turns.length === 0 ? (
          <div className="sa-start">
            <p>{ready ? '지금 화면 내용을 보고 답합니다. 답은 참고용이고, 반영은 사람이 합니다.' : '사내 AI가 연결되지 않아 지금은 쓸 수 없습니다(서버 설정 Llm:ApiKey).'}</p>
            {examples.length > 0 && <small>이렇게 물어볼 수 있습니다</small>}
            {examples.map((e) => <button key={e} className="sa-example" disabled={!ready} onClick={() => send(e)}>“{e}”</button>)}
          </div>
        ) : turns.map((m, i) => (
          <div key={i} className={`sa-msg ${m.role === 'user' ? 'user' : 'agent'}`} style={{ whiteSpace: 'pre-wrap' }}>{m.content}</div>
        ))}
        {ask.isPending && <div className="sa-msg agent">… 생각하는 중</div>}
      </div>
      <form className="sa-form" onSubmit={submit}>
        <input name="q" placeholder={placeholder ?? '물어보세요'} autoComplete="off" disabled={!ready} />
        <button className="primary" type="submit" disabled={!ready || ask.isPending}>보내기</button>
      </form>
    </aside>
  )
}
