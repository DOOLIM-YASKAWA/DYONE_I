import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../api/client'
import { ErrorBox, Loading, PageHead } from '../components/ui'

// 협력사 관리(관리자 전용, docs/partner-access 3-2). 명단은 그룹웨어 회사 공용 주소록을 Worker가 매일 미러하고,
// 여기서는 회사별 공용 비밀번호(해시만 저장)와 회사 단위 로그인 중지만 정한다. 협력사 아이디 = 주소록 이메일.

interface PartnerCompany {
  id: number
  name: string
  isActive: boolean
  people: number
  assigned: number
  hasPassword: boolean
  passwordChangedAt?: string | null
  passwordChangedBy?: string | null
  loginDisabled: boolean
}

const when = (v?: string | null) => (v ? new Date(v).toLocaleString('ko-KR', { dateStyle: 'medium', timeStyle: 'short' }) : '-')

export function PartnersAdminPage() {
  const qc = useQueryClient()
  const companies = useQuery({ queryKey: ['partner-companies'], queryFn: () => api<PartnerCompany[]>('/api/partners/companies') })
  const [picked, setPicked] = useState<number[]>([])
  const [password, setPassword] = useState('')
  const refresh = () => qc.invalidateQueries({ queryKey: ['partner-companies'] })

  const setPw = useMutation({
    mutationFn: () => api<{ changed: number }>('/api/partners/password', { method: 'POST', json: { companyIds: picked, password } }),
    onSuccess: () => { setPassword(''); setPicked([]); refresh() },
  })
  const toggle = useMutation({
    mutationFn: (c: PartnerCompany) => api(`/api/partners/companies/${c.id}/login-disabled`, { method: 'PUT', json: { disabled: !c.loginDisabled } }),
    onSuccess: refresh,
  })

  if (companies.isLoading) return <Loading />
  if (companies.error || !companies.data) return <ErrorBox error={companies.error} />
  // 사람이 있는 살아 있는 회사만 비밀번호를 정할 수 있다(구매처처럼 우리 직원만 있던 그룹은 0명)
  const usable = companies.data.filter((c) => c.isActive && c.people > 0)
  const all = usable.length > 0 && usable.every((c) => picked.includes(c.id))

  return (
    <>
      <PageHead eyebrow="관리" title="협력사 관리"
        sub="협력사 명단은 그룹웨어 회사 공용 주소록에서 매일 가져옵니다. 아이디는 주소록 이메일, 비밀번호는 회사별 공용 비밀번호입니다." />
      <section className="panel" style={{ marginBottom: 16 }}>
        <div className="panel-head">
          <div>
            <h2>회사</h2>
            <p>로그인은 프로젝트에 배정된 사람만 됩니다. 주소록에서 빠지면 바로 막힙니다.</p>
          </div>
        </div>
        <div className="panel-body">
          <div className="data-wrap">
            <table>
              <thead>
                <tr>
                  <th><input type="checkbox" aria-label="전체 선택" checked={all} onChange={() => setPicked(all ? [] : usable.map((c) => c.id))} /></th>
                  <th>회사</th><th>주소록 인원</th><th>배정된 사람</th><th>공용 비밀번호</th><th>로그인</th><th />
                </tr>
              </thead>
              <tbody>
                {companies.data.map((c) => {
                  const can = c.isActive && c.people > 0
                  return (
                    <tr key={c.id} className={can ? undefined : 'muted'}>
                      <td>
                        <input type="checkbox" aria-label={`${c.name} 선택`} disabled={!can} checked={picked.includes(c.id)}
                          onChange={(e) => setPicked((cur) => (e.target.checked ? [...cur, c.id] : cur.filter((x) => x !== c.id)))} />
                      </td>
                      <td data-label="회사"><b>{c.name}</b>{!c.isActive && ' (주소록에서 빠짐)'}</td>
                      <td data-label="주소록 인원">{c.people}명</td>
                      <td data-label="배정된 사람">{c.assigned}명</td>
                      <td data-label="공용 비밀번호">
                        {c.hasPassword ? <>설정됨 <small className="muted">{when(c.passwordChangedAt)}{c.passwordChangedBy ? ` · ${c.passwordChangedBy}` : ''}</small></> : <span className="muted">없음(로그인 불가)</span>}
                      </td>
                      <td data-label="로그인">{c.loginDisabled ? <b className="danger">중지</b> : '허용'}</td>
                      <td>
                        {can && (
                          <button className={c.loginDisabled ? 'secondary' : 'text-button danger'} disabled={toggle.isPending}
                            onClick={() => (c.loginDisabled || confirm(`${c.name} 협력사 전원의 로그인을 중지할까요? 로그인 중인 사람도 바로 막힙니다.`)) && toggle.mutate(c)}>
                            {c.loginDisabled ? '로그인 다시 허용' : '로그인 중지'}
                          </button>
                        )}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
          {toggle.error && <ErrorBox error={toggle.error} />}
        </div>
      </section>

      <section className="panel">
        <div className="panel-head">
          <div>
            <h2>공용 비밀번호 설정·교체</h2>
            <p>고른 회사 모두에 같은 값을 넣습니다. 바꾸면 그 회사 사람은 모두 새 값으로 로그인합니다. 저장은 해시로만 합니다.</p>
          </div>
        </div>
        <div className="panel-body">
          <form style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}
            onSubmit={(e) => { e.preventDefault(); setPw.mutate() }}>
            <input className="search-input" type="password" autoComplete="new-password" placeholder="새 공용 비밀번호(8자 이상)" aria-label="새 공용 비밀번호"
              value={password} maxLength={100} onChange={(e) => setPassword(e.target.value)} style={{ maxWidth: 280 }} />
            <button className="primary" disabled={setPw.isPending || picked.length === 0 || password.length < 8}>
              {picked.length > 0 ? `${picked.length}개 회사에 설정` : '회사를 고르세요'}
            </button>
          </form>
          {setPw.isSuccess && <p className="muted" style={{ marginTop: 10 }}>{setPw.data.changed}개 회사의 공용 비밀번호를 바꿨습니다.</p>}
          {setPw.error && <ErrorBox error={setPw.error} />}
        </div>
      </section>
    </>
  )
}
