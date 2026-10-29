import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api } from '../api/client'
import type { HomeSummary, Project } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { ErrorBox, Loading, PageHead, GwEndBadge, StageBadge } from '../components/ui'
import { formatKoreanDate } from '../routes'
import '../styles/project-detail.css'

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

export function HomePage() {
  const { user } = useAuth()
  const navigate = useNavigate()
  const { data, isLoading, error } = useQuery({ queryKey: ['home'], queryFn: () => api<HomeSummary>('/api/home/summary') })
  const [today] = useState(() => formatKoreanDate(new Date()))

  return (
    <>
      <PageHead eyebrow="YOUR WORKSPACE" title={`좋은 하루예요, ${user?.displayName}님`} sub={`${today} · 프로젝트와 사내 서비스를 한곳에서`} />
      {isLoading && <Loading />}
      {error && <ErrorBox error={error} />}
      {data && (
        <>
          <div className="stats" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(150px, 1fr))' }}>
            {data.customerCounts.map((c) => (
              <button key={c.name} className="stat stat-button" onClick={() => navigate(`/projects?customer=${encodeURIComponent(c.name)}`)}>
                <span>{c.name}</span>
                <strong>{c.count.toLocaleString()}</strong>
                <small>고객사 기준</small>
              </button>
            ))}
          </div>
          <div className="home-grid">
            <section className="panel">
              <div className="panel-head">
                <div>
                  <h2>MY 프로젝트</h2>
                  <p>내가 PM·투입 인원이거나, 진행 상황을 바꾸거나 파일을 올린 프로젝트입니다.</p>
                </div>
                <Link className="text-button" to="/projects">
                  전체 보기 →
                </Link>
              </div>
              {data.myProjects.map((m) => (
                <ProjectRow key={m.project.id} p={m.project} dateText={`내 작업 ${fmtWhen(m.myLastAt)}`} tags={m.reasons} />
              ))}
              {data.myProjects.length === 0 && <div className="empty">아직 관여한 프로젝트가 없습니다. 프로젝트 인원으로 배정되거나 진행 상황·파일을 올리면 여기에 모입니다.</div>}
            </section>
            <section className="panel">
              <div className="panel-head">
                <div>
                  <h2>최근 활동 프로젝트</h2>
                  <p>새로 등록됐거나 최근에 작업이 있었던 프로젝트입니다(진행 상황·파일·인원·그룹웨어·모듈라·ES·SmartLab·SRS).</p>
                </div>
              </div>
              {data.recentChanged.map((p) => (
                <ProjectRow
                  key={p.id}
                  p={p}
                  dateText={`${p.lastChangedSource ?? '변경'}${p.lastChangedBy ? ` · ${p.lastChangedBy}` : ''} · ${fmtWhen(p.lastChangedAt)}`}
                  tags={(p.recentActivity7d ?? 0) > 1 ? [`최근 7일 ${p.recentActivity7d}건`] : undefined}
                />
              ))}
              {data.recentChanged.length === 0 && <div className="empty">아직 활동 기록이 없습니다.</div>}
            </section>
            <section className="panel">
              <div className="panel-head">
                <h2>사내 서비스</h2>
              </div>
              <div className="quick-grid">
                {[
                  ['/chat', '✦', 'AI 챗봇', '프로젝트·랩 테스트 검색'],
                  ['/meals', '◉', '오늘의 식단', '점심·저녁 메뉴'],
                  ['/vehicles', '▱', '방문 차량', '방문 차량 등록'],
                  ['/access', '⌁', '모바일 출입', '모바일 사원증'],
                ].map(([to, icon, name, sub]) => (
                  <button key={to} className="quick-card" onClick={() => navigate(to)}>
                    <span>{icon}</span>
                    <div>
                      <strong>{name}</strong>
                      <small>{sub}</small>
                    </div>
                  </button>
                ))}
              </div>
            </section>
          </div>
        </>
      )}
    </>
  )
}
