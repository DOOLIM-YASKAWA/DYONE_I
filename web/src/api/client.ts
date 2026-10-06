import { platform } from '../platform/bridge'

/**
 * API 주소. 웹(개발)은 Vite 프록시를 쓰므로 비워 두고,
 * Windows 앱·모바일처럼 다른 출처에서 열릴 때는 VITE_API_BASE로 서버 주소를 지정한다.
 */
export const API_BASE: string = import.meta.env.VITE_API_BASE ?? ''

const TOKEN_KEY = 'dyone.accessToken'

export const tokenStore = {
  get: () => platform.storage.get(TOKEN_KEY),
  set: (token: string) => platform.storage.set(TOKEN_KEY, token),
  clear: () => platform.storage.remove(TOKEN_KEY),
}

export class ApiError extends Error {
  readonly status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

let onUnauthorized: (() => void) | null = null
export const setUnauthorizedHandler = (handler: () => void) => {
  onUnauthorized = handler
}

export async function api<T>(path: string, init: RequestInit & { json?: unknown } = {}): Promise<T> {
  const headers = new Headers(init.headers)
  const token = tokenStore.get()
  if (token) headers.set('Authorization', `Bearer ${token}`)
  let body = init.body
  if (init.json !== undefined) {
    headers.set('Content-Type', 'application/json')
    body = JSON.stringify(init.json)
  }

  const res = await fetch(API_BASE + path, { ...init, headers, body })
  if (res.status === 401) {
    onUnauthorized?.()
    throw new ApiError(401, '로그인이 필요합니다.')
  }
  if (!res.ok) {
    let message = `요청에 실패했습니다. (${res.status})`
    try {
      const problem = await res.json()
      message = problem.detail ?? problem.title ?? message
    } catch {
      /* 본문 없는 오류 */
    }
    throw new ApiError(res.status, message)
  }
  if (res.status === 204) return undefined as T
  return (await res.json()) as T
}
