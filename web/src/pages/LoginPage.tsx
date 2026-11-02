import { useState, type FormEvent } from 'react'
import logoLarge from '../assets/dyone-logo-large.png'
import { useAuth } from '../auth/AuthContext'

/**
 * 로그인. 그룹웨어 계정(아이디·비밀번호)으로 로그인한다. 협력사는 주소록 이메일 + 회사 공용 비밀번호(서버가 이메일로 구분).
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
    <div className="dy-login">
      <section className="dy-login-brand">
        <img src={logoLarge} alt="DY ONE" className="dy-login-logo" />
        <div className="dy-login-copy">
          <span className="dy-login-tag">INTERNAL</span>
          <h1>프로젝트와 사내 서비스를<br />한곳에서.</h1>
          <p>두림야스카와 내부 업무 플랫폼 · 프로젝트 자료함, 진행 일지, 조직도, 모듈라·ES 이슈를 한 화면에서 봅니다.</p>
        </div>
      </section>
      <section className="dy-login-form">
        <form onSubmit={submit} noValidate>
          <h2>로그인</h2>
          <p className="dy-login-sub">그룹웨어와 같은 아이디·비밀번호를 씁니다.</p>
          <label>
            아이디 또는 이메일
            <input name="loginId" autoComplete="username" autoFocus required />
          </label>
          <label>
            비밀번호
            <input name="password" type="password" autoComplete="current-password" required />
          </label>
          {error && <div className="dy-login-error" role="alert">{error}</div>}
          <button className="dy-login-submit" disabled={busy}>
            {busy ? '로그인 중…' : '로그인'}
          </button>
          <div className="dy-login-help">
            <span>로그인이 안 되면 조직도 등록 여부를 관리자에게 문의해 주세요.</span>
            {/* 협력사: 그룹웨어 주소록 이메일 + 회사 공용 비밀번호(docs/partner-access). 배정된 프로젝트가 있어야 로그인된다 */}
            <span><b>협력사</b>는 이메일 주소와 안내받은 회사 공용 비밀번호로 로그인합니다.</span>
          </div>
        </form>
      </section>
    </div>
  )
}
