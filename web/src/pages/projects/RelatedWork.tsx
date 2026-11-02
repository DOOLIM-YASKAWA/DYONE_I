import { useState } from 'react'
import { useAuth } from '../../auth/AuthContext'
import type { Code } from '../../api/types'
import { StageFeature } from './StageFeature'
import { Icon, type IconName } from '../../components/icons'
import '../../styles/d-people.css'

/**
 * 관련 업무: 보는 사람의 부서에 맞는 업무 기능만 보인다(PM은 전부).
 * 기능은 기존 단계 기능을 그대로 쓴다 — 영업 = 사양서 분석, 도장공정 = SmartLab·SRS Analyzer, CS = ES 이슈(WORKS 281), 모듈라 = 모듈라빌드(WORKS 376).
 */
type WorkKey = 'spec' | 'lab' | 'es' | 'modular'
const WORKS: { key: WorkKey; name: string; dept: string; stageCode: string; icon: IconName; source: string }[] = [
  { key: 'spec', name: '사양서 분석', dept: '영업', stageCode: 'opportunity', icon: 'srs', source: 'DY ONE' },
  { key: 'lab', name: 'SmartLab · SRS Analyzer', dept: '도장공정', stageCode: 'commission', icon: 'lab', source: 'SMARTWORK' },
  { key: 'es', name: 'ES 이슈 (WORKS)', dept: 'CS', stageCode: 'line', icon: 'issue', source: '그룹웨어 WORKS' },
  { key: 'modular', name: '모듈라빌드 (WORKS)', dept: '모듈라', stageCode: 'modula', icon: 'modular', source: '그룹웨어 WORKS' },
]

/** 부서 이름 → 그 부서 업무. 영업 부서 이름이 여럿이라(도장영업팀·자동화영업팀·ES영업팀…) 낱말로 맞춘다. */
function worksForDept(dept: string | undefined, isPm: boolean): WorkKey[] {
  if (isPm || /^PM/.test(dept ?? '')) return WORKS.map((w) => w.key)
  const d = dept ?? ''
  if (/영업/.test(d)) return ['spec']
  if (/도장공정|공정/.test(d)) return ['lab']
  if (/^CS/.test(d)) return ['es']
  if (/모듈라/.test(d)) return ['modular']
  return []
}

export function RelatedWork({ projectId, projectCode, pmName, stages }: { projectId: number; projectCode: string; pmName?: string; stages: Code[] }) {
  const { user } = useAuth()
  const isPm = !!user && !!pmName && user.name === pmName
  const mine = worksForDept(user?.department, isPm)
  // 다른 부서 업무 보기: 기본은 내 업무만. 필요하면 다른 부서 것도 골라 볼 수 있다
  const [other, setOther] = useState<WorkKey | ''>('')
  const shownKeys = other ? [other] : mine
  const [active, setActive] = useState<WorkKey | null>(null)
  const cur = shownKeys.includes(active as WorkKey) ? (active as WorkKey) : shownKeys[0]
  const work = WORKS.find((w) => w.key === cur)
  const stage = work ? stages.find((s) => s.code === work.stageCode) : undefined

  return (
    <div className="related-work dp-work">
      <div className="related-head dp-head">
        <span className="dp-work-title">
          {isPm || /^PM/.test(user?.department ?? '') ? 'PM — 관련 업무 전체' : `${user?.department ?? '부서 정보 없음'} 업무`}
        </span>
        <label className="small muted related-other">
          다른 부서 업무 보기
          <select value={other} onChange={(e) => { setOther(e.target.value as WorkKey | ''); setActive(null) }}>
            <option value="">내 업무만</option>
            {WORKS.filter((w) => !mine.includes(w.key)).map((w) => <option key={w.key} value={w.key}>{w.dept} — {w.name}</option>)}
          </select>
        </label>
      </div>
      {shownKeys.length > 1 && (
        <div className="dp-work-cards" role="tablist" aria-label="관련 업무">
          {shownKeys.map((k) => {
            const w = WORKS.find((x) => x.key === k)!
            return (
              <button key={k} role="tab" aria-selected={k === cur} className={k === cur ? 'on' : ''} onClick={() => setActive(k)}>
                <span className="dp-work-icon"><Icon name={w.icon} size={20} /></span>
                <span><small>{w.dept}</small><b>{w.name}</b><em>{w.source}</em></span>
              </button>
            )
          })}
        </div>
      )}
      {work ? (
        <StageFeature key={work.key} stageCode={work.stageCode} stage={stage} projectId={projectId} projectCode={projectCode} />
      ) : (
        <>
          <div className="dp-empty">
            <b>{user?.department ?? '이 부서'}에 연결된 업무 기능이 아직 없습니다.</b>
            <span>아래 다른 부서 기능을 누르거나, 오른쪽 위 '다른 부서 업무 보기'로 고를 수 있습니다.</span>
          </div>
          <div className="dp-work-cards">
            {WORKS.map((w) => (
              <button key={w.key} onClick={() => { setOther(w.key); setActive(null) }}>
                <span className="dp-work-icon"><Icon name={w.icon} size={20} /></span>
                <span><small>{w.dept}</small><b>{w.name}</b><em>{w.source}</em></span>
              </button>
            ))}
          </div>
        </>
      )}
    </div>
  )
}
