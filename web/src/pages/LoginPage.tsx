import { useState, type FormEvent } from 'react'
import { useAuth } from '../auth/AuthContext'

/**
 * 로그인. 그룹웨어 계정(아이디·비밀번호)으로 로그인한다.
 * 서버가 그룹웨어 인증 API로 검증 → org.employee(login_id)로 직원 확인 → DY ONE 토큰 발급.
 * (dy-chat-agent-app의 그룹웨어 SSO 로그인 방식과 동일)
 */
export function LoginPage() {
  const { login } = useAuth()
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    const loginId = String(form.get('loginId') ?? '').trim()
    const password = String(form.get('password') ?? '')
    if (!loginId || !password) {
      setError('아이디와 비밀번호를 입력해주세요.')
      return
    }
    setBusy(true)
    setError(null)
    try {
      await login(loginId, password)
    } catch (err) {
      setError(err instanceof Error ? err.message : '로그인에 실패했습니다.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="login-wrap">
      <section className="panel login-card">
        <span className="brand">
          <span className="brand-mark">D</span>
          <span>
            DY <b>ONE</b>
            <small>INTERNAL WORKSPACE</small>
          </span>
        </span>
        <h1>로그인</h1>
        <p>그룹웨어 계정으로 로그인하세요.</p>
        <form onSubmit={submit}>
          <label className="field">
            아이디
            <input name="loginId" autoComplete="username" autoFocus required />
          </label>
          <label className="field">
            비밀번호
            <input name="password" type="password" autoComplete="current-password" required />
          </label>
          {error && <div className="notice">{error}</div>}
          <button className="primary" disabled={busy}>
            {busy ? '로그인 중…' : '로그인'}
          </button>
        </form>
        <p className="login-help">그룹웨어와 같은 아이디·비밀번호를 사용합니다. 로그인이 안 되면 조직도 등록 여부를 관리자에게 문의하세요.</p>
      </section>
    </div>
  )
}
