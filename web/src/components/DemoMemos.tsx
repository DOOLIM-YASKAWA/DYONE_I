import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { useLocation, useNavigate } from 'react-router-dom'
import { api, ApiError } from '../api/client'
import { enabledRoutes as routes } from '../routes'
import '../styles/demo-memo.css'

/**
 * [임시 · 데모 기간] 화면 메모(포스트잇) — 화면 아무 곳에 궁금한 점을 붙여 두고, 나중에 모아서 팀원에게 묻는다.
 * 정식 기능이 아니다. 데모가 끝나면 이 파일 · styles/demo-memo.css · Layout의 <DemoMemos /> 한 줄 ·
 * API(Modules/Demo) · server/db/demo_memo.sql을 함께 지운다. API가 꺼져 있으면(DemoMemo:Enabled=false) 아무것도 보이지 않는다.
 */
interface Memo {
  id: number
  path: string
  screen?: string
  x: number // 본문 너비 대비 %
  y: number // 본문 맨 위에서 px
  body: string
  resolved: boolean
  answer?: string
  createdByName?: string
  createdAt: string
}

const when = (v: string) => new Date(v).toLocaleString('ko-KR', { dateStyle: 'short', timeStyle: 'short' })

export function DemoMemos() {
  const location = useLocation()
  const navigate = useNavigate()
  const qc = useQueryClient()
  const path = location.pathname
  const screen = routes.find((r) => path.startsWith(r.path))?.name ?? path
  const [placing, setPlacing] = useState(false)
  const [visible, setVisible] = useState(true)
  const [openId, setOpenId] = useState<number | null>(null)
  const [draft, setDraft] = useState<{ x: number; y: number } | null>(null)
  const [listOpen, setListOpen] = useState(false)
  const [main, setMain] = useState<HTMLElement | null>(null)

  useEffect(() => setMain(document.getElementById('main')), [path])
  useEffect(() => {
    setOpenId(null)
    setDraft(null)
    setPlacing(false)
  }, [path])

  const all = useQuery({
    queryKey: ['demo-memos'],
    queryFn: () => api<Memo[]>('/api/demo-memos'),
    retry: (n, e) => !(e instanceof ApiError && e.status === 404) && n < 2,
  })
  const refresh = () => qc.invalidateQueries({ queryKey: ['demo-memos'] })
  const create = useMutation({
    mutationFn: (v: { x: number; y: number; body: string }) => {
      // 목록에서 알아보기 쉽게: 메뉴 이름 + 그 화면의 제목(예: '프로젝트 · [테스트] 파일 업로드 테스트')
      const heading = document.querySelector('#main h1, #main .scope-card h2, #main h2')?.textContent?.trim()
      const name = heading && heading !== screen ? `${screen} · ${heading.slice(0, 60)}` : screen
      return api('/api/demo-memos', { method: 'POST', json: { path, screen: name, ...v } })
    },
    onSuccess: () => { setDraft(null); refresh() },
  })
  const update = useMutation({
    mutationFn: ({ id, ...v }: { id: number; body?: string; resolved?: boolean; answer?: string }) => api(`/api/demo-memos/${id}`, { method: 'PUT', json: v }),
    onSuccess: refresh,
  })
  const remove = useMutation({
    mutationFn: (id: number) => api(`/api/demo-memos/${id}`, { method: 'DELETE' }),
    onSuccess: () => { setOpenId(null); refresh() },
  })

  // API가 꺼져 있으면(404) 기능 자체를 숨긴다
  if (all.error instanceof ApiError && all.error.status === 404) return null
  const memos = all.data ?? []
  const here = memos.filter((m) => m.path === path)
  const openCount = memos.filter((m) => !m.resolved).length

  // 메모 붙이기: 본문 위 아무 곳이나 누르면 그 자리에 새 메모
  const place = (e: React.MouseEvent) => {
    if (!main) return
    const r = main.getBoundingClientRect()
    const x = Math.min(98, Math.max(0, ((e.clientX - r.left) / r.width) * 100))
    const y = Math.max(0, Math.round(e.clientY - r.top))
    setPlacing(false)
    setOpenId(null)
    setVisible(true)
    setDraft({ x, y })
  }

  const pins = main && visible
    ? createPortal(
        <>
          {here.map((m, i) => (
            <div key={m.id} className={`memo-pin${m.resolved ? ' resolved' : ''}${openId === m.id ? ' open' : ''}${m.x > 60 ? ' flip' : ''}`} style={{ left: `${m.x}%`, top: m.y }}>
              <button className="memo-dot" onClick={() => setOpenId(openId === m.id ? null : m.id)} title={m.body} aria-label={`메모 ${i + 1}: ${m.body}`}>
                {m.resolved ? '✓' : i + 1}
              </button>
              {openId === m.id && (
                <MemoCard
                  memo={m}
                  busy={update.isPending || remove.isPending}
                  onSave={(v) => update.mutate({ id: m.id, ...v })}
                  onDelete={() => window.confirm('이 메모를 지울까요?') && remove.mutate(m.id)}
                  onClose={() => setOpenId(null)}
                />
              )}
            </div>
          ))}
          {draft && (
            <div className={`memo-pin open${draft.x > 60 ? ' flip' : ''}`} style={{ left: `${draft.x}%`, top: draft.y }}>
              <span className="memo-dot">+</span>
              <NewMemo busy={create.isPending} error={create.error} onSave={(body) => create.mutate({ ...draft, body })} onCancel={() => setDraft(null)} />
            </div>
          )}
        </>,
        main,
      )
    : null

  return (
    <>
      {pins}
      {placing && main && createPortal(<div className="memo-placing" onClick={place}><span>메모를 붙일 곳을 누르세요 · Esc 취소</span></div>, main)}
      <EscListener active={placing} onEsc={() => setPlacing(false)} />

      <div className="memo-dock" aria-label="화면 메모(임시)">
        <button className={placing ? 'on' : ''} onClick={() => setPlacing((v) => !v)} title="이 화면에 메모 붙이기">📝 메모 붙이기</button>
        {here.length > 0 && (
          <button onClick={() => setVisible((v) => !v)} title="이 화면 메모 보이기/숨기기">
            {visible ? '숨기기' : `보이기 (${here.length})`}
          </button>
        )}
        <button onClick={() => setListOpen((v) => !v)} title="모든 화면의 메모 모아 보기">
          전체 메모{openCount > 0 && <b>{openCount}</b>}
        </button>
      </div>

      {listOpen && (
        <MemoList
          memos={memos}
          onGo={(m) => { setListOpen(false); navigate(m.path); setVisible(true); setTimeout(() => setOpenId(m.id), 300) }}
          onToggle={(m) => update.mutate({ id: m.id, resolved: !m.resolved })}
          onClose={() => setListOpen(false)}
        />
      )}
    </>
  )
}

function EscListener({ active, onEsc }: { active: boolean; onEsc: () => void }) {
  useEffect(() => {
    if (!active) return
    const h = (e: KeyboardEvent) => e.key === 'Escape' && onEsc()
    document.addEventListener('keydown', h)
    return () => document.removeEventListener('keydown', h)
  }, [active, onEsc])
  return null
}

function NewMemo({ busy, error, onSave, onCancel }: { busy: boolean; error: unknown; onSave: (body: string) => void; onCancel: () => void }) {
  const [body, setBody] = useState('')
  const ref = useRef<HTMLTextAreaElement>(null)
  useEffect(() => ref.current?.focus(), [])
  return (
    <div className="memo-card">
      <textarea
        ref={ref}
        value={body}
        onChange={(e) => setBody(e.target.value)}
        maxLength={2000}
        placeholder="궁금한 점을 적어 두세요(예: 이 단계는 누가 바꿀 수 있어야 하나요?)"
        onKeyDown={(e) => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey) && body.trim()) onSave(body) }}
      />
      {error ? <p className="memo-err">{error instanceof Error ? error.message : '저장하지 못했습니다.'}</p> : null}
      <div className="memo-actions">
        <small>Ctrl+Enter로 저장</small>
        <button className="secondary" onClick={onCancel}>취소</button>
        <button className="primary" disabled={busy || !body.trim()} onClick={() => onSave(body)}>{busy ? '저장 중…' : '붙이기'}</button>
      </div>
    </div>
  )
}

function MemoCard({ memo, busy, onSave, onDelete, onClose }: {
  memo: Memo
  busy: boolean
  onSave: (v: { body?: string; resolved?: boolean; answer?: string }) => void
  onDelete: () => void
  onClose: () => void
}) {
  const [editing, setEditing] = useState(false)
  const [body, setBody] = useState(memo.body)
  const [answer, setAnswer] = useState(memo.answer ?? '')
  return (
    <div className="memo-card">
      <div className="memo-head">
        <small>{memo.createdByName ?? '알 수 없음'} · {when(memo.createdAt)}{memo.resolved && ' · 해결됨'}</small>
        <button className="memo-x" onClick={onClose} aria-label="닫기">×</button>
      </div>
      {editing ? (
        <textarea value={body} onChange={(e) => setBody(e.target.value)} maxLength={2000} autoFocus />
      ) : (
        <p className="memo-body">{memo.body}</p>
      )}
      <label className="memo-answer">
        <span>들은 답</span>
        <textarea value={answer} onChange={(e) => setAnswer(e.target.value)} maxLength={2000} placeholder="팀원에게 들은 답(선택)" />
      </label>
      <div className="memo-actions">
        <button className="text-button danger" disabled={busy} onClick={onDelete}>삭제</button>
        <span style={{ flex: 1 }} />
        {editing ? (
          <button className="secondary" disabled={busy || !body.trim()} onClick={() => { onSave({ body }); setEditing(false) }}>내용 저장</button>
        ) : (
          <button className="secondary" onClick={() => setEditing(true)}>내용 고치기</button>
        )}
        {answer !== (memo.answer ?? '') && <button className="secondary" disabled={busy} onClick={() => onSave({ answer })}>답 저장</button>}
        <button className="primary" disabled={busy} onClick={() => onSave({ resolved: !memo.resolved, ...(answer !== (memo.answer ?? '') ? { answer } : {}) })}>
          {memo.resolved ? '다시 열기' : '해결'}
        </button>
      </div>
    </div>
  )
}

function MemoList({ memos, onGo, onToggle, onClose }: { memos: Memo[]; onGo: (m: Memo) => void; onToggle: (m: Memo) => void; onClose: () => void }) {
  const [showResolved, setShowResolved] = useState(false)
  const [copied, setCopied] = useState(false)
  const shown = memos.filter((m) => showResolved || !m.resolved)
  const groups = shown.reduce<Record<string, Memo[]>>((acc, m) => ((acc[m.path] ??= []).push(m), acc), {})
  // 팀원에게 물어볼 목록(해결 안 된 것)을 글로 복사
  const copy = async () => {
    const open = memos.filter((m) => !m.resolved)
    const byPath = open.reduce<Record<string, Memo[]>>((acc, m) => ((acc[m.path] ??= []).push(m), acc), {})
    const text = Object.entries(byPath)
      .map(([p, list]) => `[${list[0].screen ?? p}] ${p}\n` + list.map((m) => `- ${m.body.replace(/\n/g, ' ')}`).join('\n'))
      .join('\n\n')
    try {
      await navigator.clipboard.writeText(text || '물어볼 메모가 없습니다.')
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    } catch {
      window.prompt('아래 내용을 복사하세요', text)
    }
  }
  return (
    <div className="memo-list" role="dialog" aria-label="모든 화면 메모">
      <div className="memo-head">
        <b>화면 메모 <small>(임시 · 데모용)</small></b>
        <button className="memo-x" onClick={onClose} aria-label="닫기">×</button>
      </div>
      <div className="memo-actions" style={{ justifyContent: 'flex-start', marginBottom: 8 }}>
        <label style={{ fontSize: 13, display: 'flex', gap: 6, alignItems: 'center' }}>
          <input type="checkbox" checked={showResolved} onChange={(e) => setShowResolved(e.target.checked)} /> 해결된 것도 보기
        </label>
        <span style={{ flex: 1 }} />
        <button className="secondary" onClick={copy}>{copied ? '복사했습니다' : '물어볼 목록 복사'}</button>
      </div>
      {shown.length === 0 && <p className="memo-empty">메모가 없습니다. 아래 '📝 메모 붙이기'로 화면에 붙여 보세요.</p>}
      {Object.entries(groups).map(([p, list]) => (
        <section key={p}>
          <h4>{list[0].screen ?? p} <small>{p}</small></h4>
          {list.map((m) => (
            <div key={m.id} className={`memo-row${m.resolved ? ' resolved' : ''}`}>
              <input type="checkbox" checked={m.resolved} onChange={() => onToggle(m)} aria-label="해결" />
              <button className="memo-row-body" onClick={() => onGo(m)}>
                {m.body}
                {m.answer && <small>답: {m.answer}</small>}
              </button>
            </div>
          ))}
        </section>
      ))}
    </div>
  )
}
