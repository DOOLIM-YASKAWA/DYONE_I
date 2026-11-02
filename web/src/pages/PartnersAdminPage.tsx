import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../api/client'
import { ErrorBox, Loading, PageHead } from '../components/ui'
import { Icon } from '../components/icons'
import '../styles/d-misc.css'

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
      <div className="dm-partners">
        <section className="panel dm-p-table">
          <div className="dm-p-head">
            <span>
              <h2>회사 <em>{companies.data.length}</em></h2>
              <small>로그인은 프로젝트에 배정된 사람만 됩니다. 주소록에서 빠지면 바로 막힙니다.</small>
            </span>
          </div>
          <div className="data-wrap">
            <table>
              <thead>
                <tr>
                  <th><input type="checkbox" aria-label="전체 선택" checked={all} onChange={() => setPicked(all ? [] : usable.map((c) => c.id))} /></th>
                  <th>회사</th><th className="r">주소록 인원</th><th className="r">배정된 사람</th><th>공용 비밀번호</th><th>로그인</th><th />
                </tr>
              </thead>
              <tbody>
                {companies.data.map((c) => {
                  const can = c.isActive && c.people > 0
                  return (
                    <tr key={c.id} className={can ? undefined : 'dm-off'}>
                      <td>
                        <input type="checkbox" aria-label={`${c.name} 선택`} disabled={!can} checked={picked.includes(c.id)}
                          onChange={(e) => setPicked((cur) => (e.target.checked ? [...cur, c.id] : cur.filter((x) => x !== c.id)))} />
                      </td>
                      <td data-label="회사"><b>{c.name}</b>{!c.isActive && <small className="dm-sub"> (주소록에서 빠짐)</small>}</td>
                      <td data-label="주소록 인원" className="r num">{c.people}명</td>
                      <td data-label="배정된 사람" className={`r num${c.assigned ? ' dm-strong' : ' dm-dim'}`}>{c.assigned}명</td>
                      <td data-label="공용 비밀번호">
                        {c.hasPassword
                          ? <span className="dm-dot ok">설정됨<small>{when(c.passwordChangedAt)}{c.passwordChangedBy ? ` · ${c.passwordChangedBy}` : ''}</small></span>
                          : <span className="dm-dot no">없음 · 로그인 불가</span>}
                      </td>
                      <td data-label="로그인">{c.loginDisabled ? <span className="dm-dot no">중지</span> : <span className="dm-dot on">허용</span>}</td>
                      <td>
                        {can && (
                          <button className={c.loginDisabled ? 'secondary dm-btn' : 'dm-stop'} disabled={toggle.isPending}
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
        </section>

        <aside className="panel dm-p-pw">
          <div className="dm-p-pw-title">
            <span className="dm-lock"><Icon name="shield" size={18} /></span>
            <h2>공용 비밀번호 설정·교체</h2>
          </div>
          <p>고른 회사 모두에 같은 값을 넣습니다. 바꾸면 그 회사 사람은 모두 새 값으로 로그인합니다. 저장은 해시로만 합니다.</p>
          <div className="dm-picked">
            고른 회사: <b>{picked.length > 0 ? companies.data.filter((c) => picked.includes(c.id)).map((c) => c.name).join(', ') : '없음'}</b>
            {picked.length === 0 && <small> · 왼쪽 표에서 회사를 고르세요</small>}
          </div>
          <form onSubmit={(e) => { e.preventDefault(); setPw.mutate() }}>
            <label>
              새 공용 비밀번호(8자 이상)
              <input type="password" autoComplete="new-password" value={password} maxLength={100} onChange={(e) => setPassword(e.target.value)} />
            </label>
            <button className="primary" disabled={setPw.isPending || picked.length === 0 || password.length < 8}>
              {picked.length > 0 ? `${picked.length}개 회사에 설정` : '회사를 고르세요'}
            </button>
          </form>
          {setPw.isSuccess && <p className="dm-ok">{setPw.data.changed}개 회사의 공용 비밀번호를 바꿨습니다.</p>}
          {setPw.error && <ErrorBox error={setPw.error} />}
        </aside>
      </div>
    </>
  )
}
