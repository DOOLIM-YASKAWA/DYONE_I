import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api } from '../api/client'
import type { HomeSummary, Project } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { Badge, ErrorBox, Loading, PageHead } from '../components/ui'
import { formatKoreanDate } from '../routes'

const fmtDate = (v?: string) => (v ? new Date(v).toLocaleDateString('ko-KR') : '-')

export function ProjectRow({ p, dateText }: { p: Project; dateText?: string }) {
  const navigate = useNavigate()
  return (
    <button className="project-row" onClick={() => navigate(`/projects/${p.id}`)}>
      <span className="project-logo">{(p.partnerName ?? p.code).slice(0, 2)}</span>
      <div>
        <strong>{p.name}</strong>
        <small>
          {p.code} · {p.partnerName ?? '발주처 미지정'} · {dateText ?? `ERP ${p.erpRegDate ?? '-'}`}
        </small>
      </div>
      {p.effectiveStage && <Badge status={p.effectiveStage} />}
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
                  <h2>최근 등록 프로젝트</h2>
                  <p>ERP 등록날짜가 최신인 프로젝트입니다.</p>
                </div>
                <Link className="text-button" to="/projects">
                  전체 보기 →
                </Link>
              </div>
              {data.recentRegistered.map((p) => (
                <ProjectRow key={p.id} p={p} />
              ))}
              {data.recentRegistered.length === 0 && <div className="empty">표시할 프로젝트가 없습니다.</div>}
            </section>
            <section className="panel">
              <div className="panel-head">
                <div>
                  <h2>최근 변경 프로젝트</h2>
                  <p>그룹웨어에서 최근 수정된 프로젝트입니다.</p>
                </div>
              </div>
              {data.recentChanged.map((p) => (
                <ProjectRow key={p.id} p={p} dateText={`변경 ${fmtDate(p.sourceUpdatedAt)}`} />
              ))}
              {data.recentChanged.length === 0 && <div className="empty">아직 변경 이력이 없습니다 (Worker 적재 후 표시).</div>}
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
