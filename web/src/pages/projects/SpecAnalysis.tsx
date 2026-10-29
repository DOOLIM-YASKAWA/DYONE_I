import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { api, downloadFile } from '../../api/client'
import type { Code, ProjectFile } from '../../api/types'
import { ErrorBox, Loading } from '../../components/ui'
import { ProjectFilesTab } from './ProjectFiles'
import { SpecSummaryDraft } from './SpecSummaryDraft'
import { SpecDeptReview } from './SpecDeptReview'
import '../../styles/spec.css'

// ---- API 타입(서버 Modules/Spec/SpecModule.cs)
type SpecFile = { id: number; fileName: string; uploadedAt: string; sizeBytes: number }
type LibFile = { fileName: string; modified: string; revisions: number }
type MatchReason = { label: string; detail: string }
type Features = {
  customers: string[]; plants: string[]; processes: string[]; robots: string[]
  atomizers: string[]; plcs: string[]; paints: string[]; uph?: number | null
}
type Similar = {
  id: string; projectFolder: string; projectCode?: string | null; customerFolder: string; year: string
  customer?: LibFile | null; summary?: LibFile | null; minutesCount: number; hasSheet: boolean
  score: number; matchedBy: 'customer' | 'summary'; reasons: MatchReason[]; dyoneProjectId?: number | null; sameProject: boolean
}
type Analysis = {
  file?: SpecFile | null; files: SpecFile[]; features?: Features | null
  libraryCount: number; libraryWithSheet: number; libraryWithCustomer: number
  similar: Similar[]
  error?: string | null
}

const fileUrl = (id: string, kind: 'summary' | 'customer' | 'minute', n?: number) =>
  `/api/spec/library/${id}/file?kind=${kind}${n !== undefined ? `&n=${n}` : ''}`

/**
 * 영업 단계 '사양서 분석': 이번 고객 사양서를 고르면
 * ① 사양정리 초안(영업부 V5 양식) — 값은 이번 사양서에서만, 모델이 '기설치와 동일'이거나 정해지지 않은 칸은 과거 프로젝트 모델 제안(SpecSummaryDraft)
 * ② 비슷한 고객 사양서를 받았던 과거 프로젝트(고객 사양서끼리 비교) — 참고 자료.
 * NAS와 원본은 읽기만 한다.
 */
export function SpecAnalysisPanel({ projectId, stage }: { projectId: number; stage?: Code }) {
  // 사용자가 '사양서 분석'을 누른 파일만 본다(파일을 올리기만 해서는 분석하지 않음)
  const [fileId, setFileId] = useState<number | null>(null)
  const [view, setView] = useState<'draft' | 'dept' | 'similar'>('draft')
  const files = useQuery({ queryKey: ['project-files', projectId], queryFn: () => api<ProjectFile[]>(`/api/projects/${projectId}/files`) })
  const specFiles = (files.data ?? []).filter((f) => f.categoryCode === 'spec')

  const q = useQuery({
    queryKey: ['spec-analysis', projectId, fileId],
    enabled: fileId != null && view === 'similar',
    queryFn: () => api<Analysis>(`/api/projects/${projectId}/spec-analysis?fileId=${fileId}`),
  })
  const a = fileId != null ? q.data : undefined

  return (
    <div className="spec-panel">
      {/* 파일마다 '사양서 분석' 버튼 — 누른 파일만 분석한다 */}
      <ProjectFilesTab
        projectId={projectId}
        stage={stage}
        onlyCategory="spec"
        rowActions={(f) => (
          <button className={fileId === f.id ? 'secondary' : 'primary'} onClick={() => setFileId(f.id)}>
            {fileId === f.id ? '분석 결과 보는 중' : '사양서 분석'}
          </button>
        )}
      />
      {fileId == null && specFiles.length > 0 && (
        <div className="empty">
          분석할 사양서 오른쪽의 '사양서 분석'을 누르면 그 사양서의 사양정리 초안 화면이 열립니다.
          <br />모델이 정해지지 않은 칸에는 비슷한 과거 프로젝트 모델을 기본 사양으로 넣고, '참고'에서 근거·다른 후보를 봅니다.
        </div>
      )}
      {fileId != null && (
        <>
          <div className="project-tabs">
            <button className={view === 'draft' ? 'active' : ''} onClick={() => setView('draft')}>사양정리 초안</button>
            <button className={view === 'dept' ? 'active' : ''} onClick={() => setView('dept')}>부서별 확인</button>
            <button className={view === 'similar' ? 'active' : ''} onClick={() => setView('similar')}>비슷한 과거 프로젝트</button>
          </div>
          {view === 'draft' && <SpecSummaryDraft key={fileId} projectId={projectId} fileId={fileId} />}
          {view === 'dept' && <SpecDeptReview key={fileId} projectId={projectId} fileId={fileId} />}
          {view === 'similar' && (
            <>
              {q.isFetching && !a && <Loading />}
              {q.error && <ErrorBox error={q.error} />}
              {a?.error && <div className="notice spec-warn">{a.error}</div>}
              {a?.file && (
                <>
                  {a.features && <FeatureChips f={a.features} fileName={a.file.fileName} />}
                  <SimilarList items={a.similar} total={a.libraryWithCustomer} />
                </>
              )}
            </>
          )}
        </>
      )}
    </div>
  )
}

function FeatureChips({ f, fileName }: { f: Features; fileName: string }) {
  const groups: [string, string[]][] = [
    ['고객', f.customers], ['공장', f.plants], ['공정', f.processes], ['로봇', f.robots],
    ['도장기', f.atomizers], ['PLC', f.plcs], ['도료', f.paints], ['UPH', f.uph ? [String(f.uph)] : []],
  ]
  return (
    <section className="spec-section">
      <h3>이 사양서에서 찾은 특징 <small>{fileName}</small></h3>
      <div className="spec-chips">
        {groups.filter(([, v]) => v.length > 0).map(([k, v]) => (
          <span key={k} className="spec-chip"><em>{k}</em>{v.join(' · ')}</span>
        ))}
      </div>
      <p className="spec-hint">이 특징으로 과거 고객 사양서를 비교합니다. 공정이 가장 중요하고 로봇·고객·공장·도장기 순으로 봅니다.</p>
    </section>
  )
}

function SimilarList({ items, total }: { items: Similar[]; total: number }) {
  return (
    <section className="spec-section">
      <h3>비슷한 고객 사양서를 받았던 과거 프로젝트 <small>과거 고객 사양서 {total}건 중 상위 {items.length}건</small></h3>
      {items.length === 0 ? (
        <div className="empty">비슷한 프로젝트를 찾지 못했습니다. 과거 프로젝트 색인은 매일 새벽 NAS에서 새로 읽습니다.</div>
      ) : (
        <div className="spec-similar">
          {items.map((s, i) => (
            <div key={s.id} className="spec-sim">
              <span className="spec-rank">{i + 1}</span>
              <div className="spec-sim-main">
                <b>{s.projectFolder}</b>
                {s.sameProject && <span className="badge blue mini">이 프로젝트</span>}
                <small>{s.year} · {s.customerFolder}{s.matchedBy === 'summary' ? ' · 고객 사양서가 없어 사양정리로 비교' : ''}</small>
                <div className="spec-files-of">
                  <span className={s.customer ? '' : 'off'}>
                    <em>고객 사양서</em>
                    {s.customer ? <button className="text-button" onClick={() => downloadFile(fileUrl(s.id, 'customer'), s.customer!.fileName)}>{s.customer.fileName}</button> : '없음(또는 스캔 문서)'}
                  </span>
                  <span className={s.summary ? '' : 'off'}>
                    <em>사양정리</em>
                    {s.summary ? (
                      <button className="text-button" onClick={() => downloadFile(fileUrl(s.id, 'summary'), s.summary!.fileName)}>
                        {s.summary.fileName}{s.summary.revisions > 1 ? ` (판 ${s.summary.revisions}개 중 마지막)` : ''}
                      </button>
                    ) : '없음'}
                  </span>
                  <span className={s.minutesCount ? '' : 'off'}><em>회의록</em>{s.minutesCount ? `${s.minutesCount}건` : '없음'}</span>
                </div>
                <div className="badge-row">
                  {s.reasons.map((r) => (
                    <span key={r.label} className="badge mini">{r.label}: {r.detail}</span>
                  ))}
                </div>
              </div>
              <div className="spec-score" title="비슷한 정도(0~100)">
                <i style={{ width: `${Math.min(100, s.score)}%` }} />
                <span>{Math.round(s.score)}</span>
              </div>
            </div>
          ))}
        </div>
      )}
    </section>
  )
}

