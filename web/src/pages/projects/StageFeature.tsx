import { useState } from 'react'
import type { Code } from '../../api/types'
import { CommissionStage } from './CommissionStage'
import { ModularSection } from './ModularSection'
import { SalesAgentPanel } from './SalesAgentPanel'
import { SpecAnalysisPanel } from './SpecAnalysis'
import { WorksAppSection } from './WorksAppSection'

/**
 * 진행 단계별로 단계 패널에 보여 줄 업무 기능. 문서 올리기는 아래 '파일' 탭에서 한다.
 * - 영업: 탭 [영업 일지(준비 중) | 사양서 분석(고객 사양서 ↔ NAS 과거 사양정리 비교)]   · 모듈라·OLT: 이 프로젝트의 모듈라빌드 정보(그룹웨어 376)
 * - 라인 대응·종료: 이 프로젝트의 ES 이슈(그룹웨어 281)
 * - 시운전·인수: 이 프로젝트의 SmartLab 랩 테스트 · SRS 측정(읽기 전용 요약)
 * - 수주·설계·구매·출하·현장 설치: 아직 없음(기능이 생기면 여기에 추가)
 */
export const STAGE_FEATURE: Record<string, { title: string }> = {
  opportunity: { title: '영업 일지 · 사양서 분석' },
  order: { title: '기능 준비 중' },
  design: { title: '기능 준비 중' },
  modula: { title: '모듈라빌드 정보' },
  ship: { title: '기능 준비 중' },
  install: { title: '기능 준비 중' },
  commission: { title: 'SmartLab · SRS Analyzer 데이터' },
  line: { title: 'ES 이슈' },
}
const ES_ISSUE_APPLET = 281

// stage는 단계 기능이 생기면 쓴다
export function StageFeature({ stageCode, stage, projectId, projectCode }: { stageCode: string; stage?: Code; projectId: number; projectCode: string; onOpenFiles?: () => void }) {
  switch (stageCode) {
    case 'opportunity':
      return <SalesStage projectId={projectId} stage={stage} />
    case 'modula':
      return <ModularSection projectCode={projectCode} />
    case 'commission':
      return <CommissionStage projectId={projectId} />
    case 'line':
      return <WorksAppSection appletId={ES_ISSUE_APPLET} projectCode={projectCode} />
    default:
      return <div className="empty" style={{ padding: 28 }}>이 단계의 기능은 아직 준비 중입니다. 관련 문서는 아래 '파일' 탭에서 올리고 볼 수 있습니다.</div>
  }
}

const SALES_TABS = ['영업 일지', '사양서 분석'] as const
type SalesTab = (typeof SALES_TABS)[number]

/** 영업 단계: [영업 일지 | 사양서 분석] 탭. 영업 일지는 아직 자리만 있어서 사양서 분석을 먼저 연다. */
function SalesStage({ projectId, stage }: { projectId: number; stage?: Code }) {
  const [tab, setTab] = useState<SalesTab>('사양서 분석')
  return (
    <>
      <div className="project-tabs stage-tabs" role="tablist" aria-label="영업 단계 기능">
        {SALES_TABS.map((t) => (
          <button key={t} role="tab" aria-selected={t === tab} className={t === tab ? 'active' : ''} onClick={() => setTab(t)}>{t}</button>
        ))}
      </div>
      {tab === '영업 일지' && (
        <div className="empty" style={{ padding: 28 }}>영업 일지는 준비 중입니다. 영업 활동(방문·미팅·통화 기록)을 이 프로젝트에 남기는 기능이 들어갈 자리입니다.</div>
      )}
      {tab === '사양서 분석' && <SpecAnalysisPanel projectId={projectId} stage={stage} />}
      {/* 영업 에이전트: 화면 위에 떠 있는 창(끌어서 옮기기·접기) */}
      <SalesAgentPanel projectId={projectId} tab={tab} />
    </>
  )
}
