import { useQuery } from '@tanstack/react-query'
import { Link, useSearchParams } from 'react-router-dom'
import { api } from '../api/client'
import type { Code, ProjectDetail } from '../api/types'
import { ErrorBox, Loading, PageHead } from '../components/ui'
import { ProjectPicker } from './projects/ProjectPicker'
import { SpecAnalysisPanel } from './projects/SpecAnalysis'

/**
 * 사양서 분석(엔지니어링 메뉴). 프로젝트 상세 → 관련 업무 → 영업 '사양서 분석'과 같은 기능을
 * 부서와 상관없이 메뉴에서 바로 연다. 프로젝트를 고르면(?project=id) 그 프로젝트의 사양서를 올리고 분석한다.
 */
export function SpecAnalysisPage() {
  const [params, setParams] = useSearchParams()
  const projectId = params.get('project') ? Number(params.get('project')) : null
  const stages = useQuery({ queryKey: ['codes', 'project_stage'], queryFn: () => api<Code[]>('/api/codes/project_stage') })
  const detail = useQuery({
    queryKey: ['project', projectId],
    queryFn: () => api<ProjectDetail>(`/api/projects/${projectId}`),
    enabled: projectId !== null,
  })
  // 사양서는 영업 단계 자료로 올린다(관련 업무 탭과 같은 기준)
  const stage = stages.data?.find((s) => s.code === 'opportunity')
  const pick = (id: number | null) => {
    const p = new URLSearchParams(params)
    if (id) p.set('project', String(id))
    else p.delete('project')
    setParams(p)
  }
  const project = detail.data?.project

  return (
    <>
      <PageHead eyebrow="SPEC ANALYSIS" title="사양서 분석" sub="프로젝트를 고르면 그 프로젝트의 고객 사양서를 올리고, 사양정리 초안·부서별 확인·과거 비슷한 사양과 비교합니다" />
      <section className="panel spec-page">
        <div className="spec-page-pick">
          <ProjectPicker onPick={(id) => pick(id)} />
          {project && (
            <div className="spec-page-current">
              <span className="pl-code">{project.code}</span>
              <b>{project.name}</b>
              <span className="pl-muted">{project.partnerName ?? ''}</span>
              <Link to={`/projects/${project.id}`}>프로젝트 상세</Link>
              <button className="text-button" onClick={() => pick(null)}>다른 프로젝트</button>
            </div>
          )}
        </div>
        <div className="spec-page-body">
          {projectId === null ? (
            <div className="pl-empty">위에서 프로젝트 코드나 이름을 두 글자 이상 입력해 고르세요.</div>
          ) : detail.isLoading || stages.isLoading ? (
            <Loading />
          ) : detail.error ? (
            <ErrorBox error={detail.error} />
          ) : (
            <SpecAnalysisPanel key={projectId} projectId={projectId} stage={stage} />
          )}
        </div>
      </section>
    </>
  )
}
