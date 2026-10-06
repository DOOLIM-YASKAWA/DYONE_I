import { useMutation } from '@tanstack/react-query'
import { useEffect, useRef, useState, type FormEvent } from 'react'
import { api } from '../../api/client'
import type { AiSource, AskResponse, Project } from '../../api/types'

interface Message {
  role: 'user' | 'assistant'
  text: string
  sources?: AiSource[]
}

const questions = ['프로젝트 현황 요약해줘', '공정 배당 알려줘', '랩 테스트 보여줘', '담당자가 누구야?']

/** 프로젝트 AI: 선택한 프로젝트 범위로만 답한다(서버가 projectId로 범위를 좁힘). */
export function ProjectAI({ project, onOpenSource }: { project: Project | null; onOpenSource(s: AiSource): void }) {
  const scope = project?.id ?? 'all'
  const [conversations, setConversations] = useState<Record<string, Message[]>>({})
  const messages = conversations[scope] ?? []
  const box = useRef<HTMLDivElement>(null)

  const push = (...m: Message[]) => setConversations((c) => ({ ...c, [scope]: [...(c[scope] ?? []), ...m] }))
  const ask = useMutation({
    mutationFn: (question: string) => api<AskResponse>('/api/ai/ask', { method: 'POST', json: { question, projectId: project?.id ?? null } }),
    onMutate: (question) => push({ role: 'user', text: question }),
    onSuccess: (res) => push({ role: 'assistant', text: res.answer, sources: res.sources }),
    onError: (e) => push({ role: 'assistant', text: e.message }),
  })

  useEffect(() => {
    box.current?.scrollTo({ top: box.current.scrollHeight })
  }, [messages.length])

  const submit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = e.currentTarget
    const q = String(new FormData(form).get('question') ?? '').trim()
    if (q) ask.mutate(q)
    form.reset()
  }

  return (
    <aside className="project-ai panel">
      <div className="project-ai-head">
        <span>✦</span>
        <div>
          <h2>프로젝트 AI</h2>
          <p>{project?.name ?? '전체 프로젝트'}</p>
        </div>
      </div>
      <p className="project-ai-context">
        {project ? '현재 선택한 프로젝트의 기록만 참고합니다.' : '전체 프로젝트를 참고합니다. 프로젝트를 선택하면 답변 범위가 좁혀집니다.'}
      </p>
      <div className="project-ai-questions">
        {questions.map((q) => (
          <button key={q} onClick={() => ask.mutate(q)} disabled={ask.isPending}>
            {q} ↗
          </button>
        ))}
      </div>
      <div className="project-ai-messages" aria-live="polite" ref={box}>
        {messages.slice(-6).map((m, i) => (
          <div key={i} className={`project-ai-message ${m.role}`}>
            {m.text}
            {m.sources && m.sources.length > 0 && (
              <div className="project-ai-sources">
                <small>참고한 기록</small>
                {m.sources.map((s) => (
                  <button key={`${s.kind}-${s.id}`} onClick={() => onOpenSource(s)}>
                    {s.label} ↗
                  </button>
                ))}
              </div>
            )}
          </div>
        ))}
      </div>
      <form className="project-ai-form" onSubmit={submit}>
        <input name="question" aria-label="프로젝트 AI 질문" placeholder="프로젝트에 대해 질문하세요" required maxLength={1000} />
        <button className="primary" aria-label="프로젝트 AI 질문 보내기" disabled={ask.isPending}>
          ↑
        </button>
      </form>
      <p className="project-ai-note">dyone DB 조회 기반 규칙형 답변 · 로컬 LLM + 문서 검색(RAG) 연결 전</p>
    </aside>
  )
}
