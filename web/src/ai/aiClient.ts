import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client'

/**
 * 사내 AI 공통 — 화면 에이전트·자료 분류 등 모든 AI 호출은 여기를 거친다.
 * 서버: /api/ai/*(DyOne.Api Modules/Ai/AiAgentModule.cs, Platform/Llm.cs). 프롬프트는 서버에서만 정한다.
 * 사내 AI가 연결 안 됐으면(키 없음) status.configured = false — 화면은 AI 없이 동작하게 만든다.
 */
export interface AiStatus {
  configured: boolean
  model: string
  agents: { key: string; name: string }[]
}
export interface AgentTurn {
  role: 'user' | 'assistant'
  content: string
}

export const useAiStatus = () =>
  useQuery({ queryKey: ['ai-status'], queryFn: () => api<AiStatus>('/api/ai/status'), staleTime: 5 * 60_000 })

/** 화면 에이전트에게 묻기. context = 지금 화면 상황(글자, 서버가 3만 자로 자름) */
export const agentChat = (agent: string, messages: AgentTurn[], context?: string) =>
  api<{ reply: string; agent: string; model: string }>(`/api/ai/agent/${agent}`, { method: 'POST', json: { messages, context: context ?? null } })

export interface DocItem {
  key: string
  path: string
  size?: number
  /** 폴더 묶음이면 안 파일 경로 일부 */
  files?: string[]
  /** 글자 파일이면 앞부분 */
  sample?: string
  ruleCode?: string
  ruleReasons?: string[]
}
export interface DocClass {
  key: string
  code: string
  reason: string
  confidence: number
}

/** 자료 종류 분류(60개씩 나눠 묻는다) */
export async function classifyDocs(items: DocItem[], categories: { code: string; name: string; what?: string }[], projectName?: string): Promise<DocClass[]> {
  const out: DocClass[] = []
  for (let i = 0; i < items.length; i += 60) {
    const r = await api<{ items: DocClass[] }>('/api/ai/classify-docs', {
      method: 'POST',
      json: { items: items.slice(i, i + 60), categories, projectName: projectName ?? null },
    })
    out.push(...r.items)
  }
  return out
}
