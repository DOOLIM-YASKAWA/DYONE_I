import { useEffect, useRef, useState } from 'react'
import { api } from '../api/client'
import { platform } from '../platform/bridge'
import '../styles/d-works.css'
type Handoff = { handoffUrl: string; baseUrl: string }
type Props = {
  title: string
  appKey: string
  endpoint: string
  waitForReady?: boolean
}
export function EmbeddedAppPage({
  title,
  appKey,
  endpoint,
  waitForReady = true,
}: Props) {
  const [src, setSrc] = useState<string | null>(null)
  const [base, setBase] = useState<string | null>(null) // 새 창으로 열 주소(일회용 로그인 주소가 아닌 앱 첫 화면)
  const [phase, setPhase] = useState<
    'auth' | 'screen' | 'data' | 'ready' | 'error'
  >('auth')
  const [error, setError] = useState('')
  const [retry, setRetry] = useState(0)
  const frame = useRef<HTMLIFrameElement>(null)
  const timeout = useRef<ReturnType<typeof setTimeout> | null>(null)
  const clearTimer = () => {
    if (timeout.current) {
      clearTimeout(timeout.current)
      timeout.current = null
    }
  }
  const ready = () => {
    clearTimer()
    setPhase('ready')
  }
  useEffect(() => {
    const controller = new AbortController()
    setSrc(null)
    setError('')
    setPhase('auth')
    timeout.current = setTimeout(() => {
      controller.abort()
      setError('연결이 오래 걸리고 있습니다. 다시 연결해 주세요.')
      setPhase('error')
      setSrc(null)
    }, 45000)
    api<Handoff>(endpoint, { signal: controller.signal })
      .then((h) => {
        if (controller.signal.aborted) return
        const url = new URL(h.handoffUrl, window.location.origin)
        const locals = ['localhost', '127.0.0.1', '[::1]']
        if (
          locals.includes(url.hostname) &&
          locals.includes(window.location.hostname)
        )
          url.hostname = window.location.hostname
        setSrc(url.toString())
        setBase(h.baseUrl ? new URL(h.baseUrl, window.location.origin).toString() : null)
        setPhase('screen')
      })
      .catch((e) => {
        if (controller.signal.aborted) return
        clearTimer()
        setError(e?.message ?? '연결에 실패했습니다.')
        setPhase('error')
      })
    return () => {
      controller.abort()
      clearTimer()
    }
  }, [endpoint, retry])
  useEffect(() => {
    const receive = (event: MessageEvent) => {
      if (
        !src ||
        event.source !== frame.current?.contentWindow ||
        event.origin !== new URL(src).origin ||
        event.data?.app !== appKey
      )
        return
      if (event.data.type === 'dyone:embed-ready') ready()
      else if (event.data.type === 'dyone:embed-error') {
        clearTimer()
        setError(event.data.message ?? '화면을 준비하지 못했습니다.')
        setPhase('error')
      }
    }
    window.addEventListener('message', receive)
    return () => window.removeEventListener('message', receive)
  }, [src, appKey])
  useEffect(() => {
    const body = document.body.style.overflow,
      html = document.documentElement.style.overflow
    document.body.style.overflow = 'hidden'
    document.documentElement.style.overflow = 'hidden'
    return () => {
      document.body.style.overflow = body
      document.documentElement.style.overflow = html
    }
  }, [])
  const loading = phase !== 'ready' && phase !== 'error'
  const conn = phase === 'ready' ? { cls: 'ok', text: 'SMARTWORK에서 불러온 화면 · 연결됨' } : phase === 'error' ? { cls: 'err', text: '연결 안 됨' } : { cls: 'wait', text: '연결 중…' }
  const openNew = () => {
    if (!base) return
    if (platform.kind !== 'web') void platform.openExternal(base)
    else window.open(base, '_blank', 'noopener,noreferrer')
  }
  return (
    <>
      {/* D안: 얇은 머리말 — 제목 · 연결 상태 점 · 새 창으로 열기(안쪽 화면은 SMARTWORK 그대로) */}
      <div className="page-head smartlab-head dw-embed-head">
        <div>
          <p className="eyebrow">SMARTWORK</p>
          <h1>{title}</h1>
        </div>
        <div className="dw-embed-actions">
          <span className={`dw-conn ${conn.cls}`} role="status"><i />{conn.text}</span>
          {base && phase !== 'error' && (
            <button className="secondary" onClick={openNew}>새 창으로 열기 ↗</button>
          )}
        </div>
      </div>
      <div className="smartlab-embed embed-app" aria-busy={loading}>
        {src && phase !== 'error' && (
          <iframe
            ref={frame}
            src={src}
            title={title}
            allow="clipboard-read; clipboard-write"
            onLoad={() => {
              if (!waitForReady) ready()
              else {
                setPhase((current) => (current === 'ready' ? 'ready' : 'data'))
                frame.current?.contentWindow?.postMessage(
                  { type: 'dyone:embed-ready-request', app: appKey },
                  new URL(src).origin,
                )
              }
            }}
            onError={() => {
              clearTimer()
              setError('웹 화면에 연결하지 못했습니다.')
              setPhase('error')
            }}
          />
        )}
        {loading && (
          <div className="embed-loading">
            <div className="embed-loading-card">
              <span className="embed-loading-icon" aria-hidden="true">
                ↗
              </span>
              <h2>{title} 연결 중</h2>
              <p role="status">
                {phase === 'auth'
                  ? '로그인 정보를 연결하고 있습니다.'
                  : phase === 'screen'
                    ? '화면을 준비하고 있습니다.'
                    : '초기 데이터를 불러오고 있습니다.'}
              </p>
              <div
                className="embed-loading-bar"
                role="progressbar"
                aria-label={title + ' 불러오는 중'}
              >
                <span />
              </div>
            </div>
          </div>
        )}
        {phase === 'error' && (
          <div className="embed-loading">
            <div className="embed-loading-card dw-embed-error">
              <span className="dw-embed-error-icon" aria-hidden="true">!</span>
              <h2>{title} 연결을 확인해 주세요</h2>
              <p role="alert">{error}</p>
              <div className="dw-embed-error-actions">
                <button className="primary" onClick={() => setRetry((n) => n + 1)}>다시 연결</button>
              </div>
              <small>계속 안 되면 관리자에게 SMARTWORK 연결 설정을 확인해 달라고 알려 주세요.</small>
            </div>
          </div>
        )}
      </div>
    </>
  )
}
