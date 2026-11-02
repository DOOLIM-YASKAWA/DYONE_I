import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api } from '../api/client'
import type { HomeSummary, Project } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { ErrorBox, Loading, PageHead, GwEndBadge, StageBadge } from '../components/ui'
import { formatKoreanDate } from '../routes'
import '../styles/project-detail.css'
import '../styles/d-misc.css'
import { Icon, type IconName } from '../components/icons'

const fmtWhen = (v?: string) => (v ? new Date(v).toLocaleString('ko-KR', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : '-')

export function ProjectRow({ p, dateText, tags }: { p: Project; dateText?: string; tags?: string[] }) {
  const navigate = useNavigate()
  return (
    <button className="project-row" onClick={() => navigate(`/projects/${p.id}`)}>
      <span className="project-logo">{(p.partnerName ?? p.code).slice(0, 2)}</span>
      <div>
        <strong>{p.name}</strong>
        <small>
          {p.code} · {p.partnerName ?? '발주처 미지정'} · {dateText ?? `ERP ${p.erpRegDate ?? '-'}`}
        </small>
        {tags && tags.length > 0 && (
          <span className="badge-row row-tags">{tags.map((t) => <span key={t} className={`badge ${t === 'PM' ? 'blue' : ''}`}>{t}</span>)}</span>
        )}
      </div>
      {p.effectiveStage && <StageBadge name={p.effectiveStage} status={p.effectiveStatus} />}
      <GwEndBadge status={p.status} />
    </button>
  )
}

/** 진행 단계 점 색(D안, 프로젝트 목록과 같은 규칙) */
const STAGE_COLOR: Record<string, string> = { '영업': '#2563EB', '수주': '#15803D', '설계·검토': '#6D28D9', '모듈라': '#0F766E', '현장': '#B45309', '라인 대응·종료': '#475467' }
/** 고객사 비율 막대 색 — 파랑 명도 단계(점유 큰 '기타'는 회색) */
const CUST_COLOR = ['#0056B9', '#3B82E0', '#7DAAEC', '#A9C6F2', '#C9DAF5', '#9AA6B8']

/** 홈 목록 한 줄(D안): 코드 · 이름(+꼬리표) · 아래 작은 글씨 · 오른쪽 단계 점 */
function HomeRow({ p, sub, tags }: { p: Project; sub: string; tags?: string[] }) {
  const navigate = useNavigate()
  return (
    <button className="dm-hrow" onClick={() => navigate(`/projects/${p.id}`)}>
      <span className="dm-hrow-main">
        <span className="dm-hrow-top">
          <span className="dm-code">{p.code}</span>
          <b>{p.name}</b>
          {tags?.map((t) => <span key={t} className={`dm-tag${t === 'PM' ? ' pm' : ''}`}>{t}</span>)}
          <GwEndBadge status={p.status} />
        </span>
        <small>{sub}</small>
      </span>
      {p.effectiveStage ? (
        <span className="dm-stage"><i style={{ background: STAGE_COLOR[p.effectiveStage] ?? '#475467' }} />{p.effectiveStage}{p.effectiveStatus === 'done' && <em>완료</em>}</span>
      ) : <span className="dm-stage dim">{p.status ?? '미지정'}</span>}
    </button>
  )
}

const SERVICES: [string, IconName, string, string][] = [
  ['/chat', 'spark', 'AI 챗봇', '프로젝트·랩 테스트 검색'],
  ['/meals', 'meals', '오늘의 식단', '점심·저녁 메뉴'],
]

export function HomePage() {
  const { user } = useAuth()
  const navigate = useNavigate()
  const { data, isLoading, error } = useQuery({ queryKey: ['home'], queryFn: () => api<HomeSummary>('/api/home/summary') })
  const [today] = useState(() => formatKoreanDate(new Date()))
  const total = data ? data.customerCounts.reduce((t, c) => t + c.count, 0) : 0

  return (
    <>
      <PageHead eyebrow="YOUR WORKSPACE" title={`좋은 하루예요, ${user?.displayName}님`} sub={`${today} · 프로젝트와 사내 서비스를 한곳에서`} />
      {isLoading && <Loading />}
      {error && <ErrorBox error={error} />}
      {data && (
        <div className="dm-home">
          <section className="panel dm-cust">
            <div className="dm-cust-head">
              <span><h2>고객사별 프로젝트</h2><b className="dm-big">{total.toLocaleString()}</b><small>고객사 기준</small></span>
              <Link className="text-button" to="/projects">프로젝트 전체 보기 →</Link>
            </div>
            <div className="dm-bar" aria-hidden>
              {data.customerCounts.map((c, i) => (
                <span key={c.name} style={{ flexBasis: `${Math.max(0.6, total ? (c.count / total) * 100 : 0)}%`, background: CUST_COLOR[i % CUST_COLOR.length] }} />
              ))}
            </div>
            <div className="dm-cust-nums">
              {data.customerCounts.map((c, i) => (
                <button key={c.name} onClick={() => navigate(`/projects?customer=${encodeURIComponent(c.name)}`)}>
                  <span><i style={{ background: CUST_COLOR[i % CUST_COLOR.length] }} />{c.name}</span>
                  <b>{c.count.toLocaleString()}</b>
                </button>
              ))}
            </div>
          </section>

          <div className="dm-home-grid">
            <section className="panel dm-list">
              <div className="dm-list-head">
                <span><h2>내 프로젝트</h2><em>{data.myProjects.length}</em></span>
                <Link className="text-button" to="/projects">전체 보기 →</Link>
              </div>
              <p className="dm-list-sub">내가 PM·투입 인원이거나, 진행 상황을 바꾸거나 파일을 올린 프로젝트입니다.</p>
              {data.myProjects.map((m) => (
                <HomeRow key={m.project.id} p={m.project} tags={m.reasons}
                  sub={`${m.project.partnerName ?? '발주처 미지정'} · 내 작업 ${fmtWhen(m.myLastAt)}`} />
              ))}
              {data.myProjects.length === 0 && <div className="dm-empty">아직 관여한 프로젝트가 없습니다. 프로젝트 인원으로 배정되거나 진행 상황·파일을 올리면 여기에 모입니다.</div>}
            </section>
            <section className="panel dm-list">
              <div className="dm-list-head">
                <span><h2>최근 활동</h2></span>
                <small>진행·파일·인원·그룹웨어·모듈라·ES·SmartLab·SRS</small>
              </div>
              {data.recentChanged.map((p) => (
                <HomeRow
                  key={p.id}
                  p={p}
                  tags={(p.recentActivity7d ?? 0) > 1 ? [`최근 7일 ${p.recentActivity7d}건`] : undefined}
                  sub={`${p.lastChangedSource ?? '변경'}${p.lastChangedBy ? ` · ${p.lastChangedBy}` : ''} · ${fmtWhen(p.lastChangedAt)}`}
                />
              ))}
              {data.recentChanged.length === 0 && <div className="dm-empty">아직 활동 기록이 없습니다.</div>}
            </section>
          </div>

          <section className="dm-services" aria-label="사내 서비스">
            {SERVICES.map(([to, icon, name, sub]) => (
              <button key={to} onClick={() => navigate(to)}>
                <span><Icon name={icon} size={20} /></span>
                <span><b>{name}</b><small>{sub}</small></span>
              </button>
            ))}
          </section>
        </div>
      )}
    </>
  )
}
