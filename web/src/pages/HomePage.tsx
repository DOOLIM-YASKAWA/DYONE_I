import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api } from '../api/client'
import type { HomeSummary, Project } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { Badge, ErrorBox, Loading, PageHead } from '../components/ui'
import { formatKoreanDate } from '../routes'

export function ProjectRow({ p }: { p: Project }) {
  const navigate = useNavigate()
  return (
    <button className="project-row" onClick={() => navigate(`/projects?project=${p.id}`)}>
      <span className="project-logo">{(p.partnerName ?? p.code).slice(0, 2)}</span>
      <div>
        <strong>{p.name}</strong>
        <small>
          {p.code} · {p.partnerName ?? '발주처 미지정'} · ERP {p.erpRegDate ?? '-'}
        </small>
      </div>
      {p.status && <Badge status={p.status} />}
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
          <div className="stats" style={{ gridTemplateColumns: `repeat(${data.statusCounts.length}, minmax(0, 1fr))` }}>
            {data.statusCounts.map((s) => (
              <button key={s.statusId} className="stat stat-button" onClick={() => navigate(`/projects?status=${s.statusId}`)}>
                <span>{s.status}</span>
                <strong>{s.count.toLocaleString()}</strong>
                <small>그룹웨어 프로젝트 기준</small>
              </button>
            ))}
          </div>
          <div className="home-grid">
            <section className="panel">
              <div className="panel-head">
                <div>
                  <h2>최근 수주 프로젝트</h2>
                  <p>수주 상태 프로젝트를 ERP 등록 최신순으로 표시합니다.</p>
                </div>
                <Link className="text-button" to="/projects">
                  전체 보기 →
                </Link>
              </div>
              {data.recentProjects.map((p) => (
                <ProjectRow key={p.id} p={p} />
              ))}
            </section>
            <section className="panel">
              <div className="panel-head">
                <div>
                  <h2>최근 랩 테스트</h2>
                  <p>SmartLab에 등록된 최근 테스트입니다.</p>
                </div>
              </div>
              <div className="attention">
                {data.recentLabTests.map((l) => (
                  <button key={l.id} onClick={() => navigate(l.projectId ? `/projects?project=${l.projectId}&tab=lab` : '/projects?tab=lab')}>
                    <span>LAB</span>
                    <div>
                      <strong>{l.name}</strong>
                      <small>
                        {l.type ?? '-'} · {l.projectCode ?? '프로젝트 미연결'} · {l.startedAt ? new Date(l.startedAt).toLocaleDateString('ko-KR') : '-'}
                      </small>
                    </div>
                  </button>
                ))}
              </div>
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
