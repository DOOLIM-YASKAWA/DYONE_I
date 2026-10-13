import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api } from '../../api/client'
import type { Code, ProjectDetail } from '../../api/types'
import { Badge, ErrorBox, Loading } from '../../components/ui'
import { AssignmentList } from './DetailDialogs'

const date = (v?: string) => (v ? new Date(v).toLocaleDateString('ko-KR') : '-')
const TABS = ['개요', '모듈라 일지', 'ES 이슈', '랩 테스트'] as const
type Tab = (typeof TABS)[number]

/** 프로젝트 전용 풀페이지 상세. 상단에 진행 단계바(S1~S9) + 섹션 탭. */
export function ProjectDetailPage() {
  const { id } = useParams()
  const pid = Number(id)
  const qc = useQueryClient()
  const [tab, setTab] = useState<Tab>('개요')
  const { data, isLoading, error } = useQuery({
    queryKey: ['project', pid],
    queryFn: () => api<ProjectDetail>(`/api/projects/${pid}`),
    enabled: !Number.isNaN(pid),
  })
  const stages = useQuery({ queryKey: ['codes', 'project_stage'], queryFn: () => api<Code[]>('/api/codes/project_stage') })
  const save = useMutation({
    mutationFn: (sid: number) => api(`/api/projects/${pid}/stage`, { method: 'PUT', json: { stageId: sid } }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['project', pid] })
      qc.invalidateQueries({ queryKey: ['home'] })
      qc.invalidateQueries({ queryKey: ['projects'] })
    },
  })

  if (isLoading) return <Loading />
  if (error || !data) return <ErrorBox error={error} />
  const p = data.project
  const curIdx = stages.data?.findIndex((s) => s.name === p.effectiveStage) ?? -1

  return (
    <>
      <Link to="/projects" className="text-button" style={{ display: 'inline-flex', gap: 6, marginBottom: 10, padding: 0 }}>← 프로젝트 목록</Link>
      <div className="scope-card">
        <div>
          <small>{p.code} · {p.partnerName ?? '발주처 미지정'} · {p.type ?? '종류 미지정'}</small>
          <h2>{p.name}</h2>
          <p>
            ERP {p.erpRegDate ?? '-'} · 공정 배당 {data.assignments.length}건 · 랩 테스트 {data.labTestCount}건
            {data.apps.length ? ` · 앱: ${data.apps.join(', ')}` : ''}
          </p>
        </div>
        <span />
        {p.status ? <Badge status={p.status} /> : <span className="badge">상태 미지정</span>}
      </div>

      <section className="panel stage-panel" style={{ marginBottom: 16 }}>
        <div className="panel-head">
          <div>
            <h2>예상 현재 진행 상황</h2>
            <p>단계를 클릭하면 변경됩니다.{data.dyoneStage && ` · 변경: ${data.dyoneStage.updatedByName ?? '-'} · ${date(data.dyoneStage.updatedAt)}`}</p>
          </div>
        </div>
        <div className="panel-body" style={{ paddingTop: 6 }}>
          <div className="stage-flow">
            {stages.data?.map((s, i) => {
              const state = i === curIdx ? 'current' : curIdx >= 0 && i < curIdx ? 'done' : 'todo'
              return (
                <button key={s.id} className={`stage-step ${state}`} onClick={() => save.mutate(s.id)} disabled={save.isPending}>
                  <div className="s-num">S{i + 1}</div>
                  <div className="s-name">{s.name}</div>
                  <div className="s-marker"><span className="dot" /></div>
                  <div className="s-state">{state === 'done' ? '완료' : state === 'current' ? '진행' : '대기'}</div>
                </button>
              )
            })}
          </div>
        </div>
      </section>

      <div className="project-tabs" role="tablist" aria-label="프로젝트 섹션">
        {TABS.map((t) => (
          <button key={t} role="tab" aria-selected={t === tab} className={t === tab ? 'active' : ''} onClick={() => setTab(t)}>{t}</button>
        ))}
      </div>

      <section className="panel">
        <div className="panel-body" style={{ paddingTop: 18 }}>
          {tab === '개요' && (
            <>
              <dl className="detail-meta">
                <div><dt>발주처</dt><dd>{p.partnerName ?? '-'}</dd></div>
                <div><dt>프로젝트 종류</dt><dd>{p.type ?? '-'}</dd></div>
                <div><dt>ERP 등록날짜</dt><dd>{p.erpRegDate ?? '-'}</dd></div>
                <div><dt>앱 소속</dt><dd>{data.apps.join(', ') || '없음'}</dd></div>
                {p.description && <div className="full"><dt>설명</dt><dd>{p.description}</dd></div>}
              </dl>
              <AssignmentList assignments={data.assignments} />
            </>
          )}
          {tab === '모듈라 일지' && <div className="empty" style={{ padding: 40 }}>모듈라 설치·시운전 작업 일지 — 추후(modular 연계)</div>}
          {tab === 'ES 이슈' && <div className="empty" style={{ padding: 40 }}>현장 이슈·고장 보고 — 추후(그룹웨어 ES)</div>}
          {tab === '랩 테스트' && <div className="empty" style={{ padding: 40 }}>SmartLab 연동 테스트 {data.labTestCount}건</div>}
        </div>
      </section>
    </>
  )
}
