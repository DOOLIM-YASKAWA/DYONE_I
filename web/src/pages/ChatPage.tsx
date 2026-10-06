import { useMutation } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { api } from '../api/client'
import type { AskResponse } from '../api/types'
import { PageHead } from '../components/ui'

interface Message {
  role: 'user' | 'assistant'
  text: string
}

const suggestions = ['전체 프로젝트 현황 요약해줘', '광주 프로젝트 찾아줘', 'DLP-24-182 공정 배당 알려줘', '최근 랩 테스트 보여줘']

/** 전체 AI 챗봇. 서버의 IAiAssistant(지금 규칙형 → 추후 로컬 LLM + RAG)에 질문한다. */
export function ChatPage() {
  const [messages, setMessages] = useState<Message[]>([])
  const ask = useMutation({
    mutationFn: (question: string) => api<AskResponse>('/api/ai/ask', { method: 'POST', json: { question } }),
    onMutate: (question) => setMessages((m) => [...m, { role: 'user', text: question }]),
    onSuccess: (res) => setMessages((m) => [...m, { role: 'assistant', text: res.answer }]),
    onError: (e) => setMessages((m) => [...m, { role: 'assistant', text: e.message }]),
  })

  const submit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = e.currentTarget
    const q = String(new FormData(form).get('question') ?? '').trim()
    if (!q) return
    ask.mutate(q)
    form.reset()
  }

  return (
    <>
      <PageHead eyebrow="AI ASSISTANT" title="AI 챗봇" sub="프로젝트, 담당자와 업무 정보를 검색하는 공간" />
      <div className="chat-layout">
        {messages.length === 0 && (
          <>
            <div className="chat-start">
              <span className="ai-icon">✦</span>
              <h2>어떤 정보를 찾고 계신가요?</h2>
              <p>
                담당 프로젝트부터 모듈라·랩 테스트·현장 이슈까지
                <br />
                필요한 정보를 질문해 보세요.
              </p>
            </div>
            <div className="suggestions">
              {suggestions.map((q) => (
                <button key={q} onClick={() => ask.mutate(q)}>
                  {q} ↗
                </button>
              ))}
            </div>
          </>
        )}
        <div className="chat-messages">
          {messages.map((m, i) => (
            <div key={i} className={`message ${m.role}`}>
              {m.text}
              {m.role === 'assistant' && <small>dyone DB 조회 기반 규칙형 답변 · 로컬 LLM 연결 전</small>}
            </div>
          ))}
          {ask.isPending && <div className="message assistant">답변을 찾는 중…</div>}
        </div>
        <form className="chat-form" onSubmit={submit}>
          <input name="question" aria-label="AI 질문" placeholder="찾고 싶은 정보를 입력하세요" required maxLength={1000} />
          <button className="primary" disabled={ask.isPending}>
            보내기 ↑
          </button>
        </form>
      </div>
    </>
  )
}
