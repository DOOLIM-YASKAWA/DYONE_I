import { useQuery } from '@tanstack/react-query'
import { Fragment, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, downloadFile } from '../../api/client'
import { ErrorBox, Loading } from '../../components/ui'

/** smartlab.lab_test — 이 프로젝트의 랩 테스트 */
interface LabTestRow {
  id: number
  name: string
  type?: string
  operator?: string
  startedAt?: string
  endedAt?: string
  description?: string
  conditionCount: number
  measuredCount: number
  approvedCount: number
}
/** 조건 한 줄. params = DYCTM 9파라미터 순서. 결과는 SmartLab 결과 화면과 같은 식(서버에서 계산) */
interface LabCondition {
  number: number
  params: (number | null)[]
  points: number
  approved: boolean
  /** Pattern: 최댓값(µm) · 패턴 폭(mm) */
  max?: number
  patternMm?: number
  /** DFT: 평균 도막(µm) · 표준편차 · 도착 효율 TE(%) */
  dft?: number
  stdev?: number
  te?: number
}
/** srs.measurement — 이 프로젝트의 SRS 측정 */
interface SrsRow {
  id: number
  capturedAt?: string
  customer?: string
  factory?: string
  booth?: string
  zone?: string
  robotCount?: number
  modelCount?: number
  colorCount?: number
  maxRobotVelocity?: number
  avgFlowRate?: number
  historyRange?: string
}
interface SrsDetail {
  robots: { name?: string; type?: string; tool?: string; avgVelocity?: string; gunDistance?: string; avgGunOnTime?: string; hvAvg?: string; paintVolAvg?: string }[]
  models: string[]
  colors: string[]
  brushCount?: number
}

// SmartLab 결과 화면과 같은 이름·단위(src/lib/calc.js PARAM_COLS)
const PARAM_LABELS = ['Speed(mm/s)', 'Distance(mm)', 'Pitch(mm)', 'RPM(Krpm)', 'V SA(Lpm)', 'S SA(Lpm)', 'HV(㎂)', 'Flow1(cc/m)', 'Flow2(cc/m)']
const day = (v?: string) => (v ? new Date(v).toLocaleDateString('ko-KR') : '-')
const num = (v?: number | null, d = 1) => (v == null ? '-' : Number(v).toLocaleString('ko-KR', { maximumFractionDigits: d }))
/** '[2022-04-30,2022-06-03)' → '2022-04-30 ~ 2022-06-02' 대신 원문 날짜 둘만 */
const range = (v?: string) => {
  const m = v?.match(/(\d{4}-\d{2}-\d{2}).*?(\d{4}-\d{2}-\d{2})/)
  return m ? `${m[1]} ~ ${m[2]}` : v ?? '-'
}

const TABS = ['SmartLab', 'SRS Analyzer'] as const
type Tab = (typeof TABS)[number]

/**
 * 시운전·인수 단계: 이 프로젝트에 연결된 SmartLab 랩 테스트와 SRS 측정을 DY ONE에서 요약해 보여 준다(읽기만).
 * 행을 누르면 바로 아래에 상세가 펼쳐진다. 편집·전체 기능은 각 프로그램에서('…에서 열기' = 첫 화면).
 */
export function CommissionStage({ projectId }: { projectId: number }) {
  const navigate = useNavigate()
  const labs = useQuery({ queryKey: ['project-lab-tests', projectId], queryFn: () => api<LabTestRow[]>(`/api/projects/${projectId}/lab-tests`) })
  const srs = useQuery({ queryKey: ['project-srs', projectId], queryFn: () => api<SrsRow[]>(`/api/projects/${projectId}/srs-measurements`) })
  const [picked, setPicked] = useState<Tab | null>(null)
  // 처음엔 데이터가 있는 쪽을 연다(둘 다 없으면 SmartLab)
  const tab: Tab = picked ?? (!labs.data?.length && srs.data?.length ? 'SRS Analyzer' : 'SmartLab')
  const count = (t: Tab) => (t === 'SmartLab' ? labs.data?.length : srs.data?.length)

  return (
    <>
      <div className="project-tabs stage-tabs" role="tablist" aria-label="시운전 데이터">
        {TABS.map((t) => (
          <button key={t} role="tab" aria-selected={t === tab} className={t === tab ? 'active' : ''} onClick={() => setPicked(t)}>
            {t} <small className="tab-count">{count(t) ?? ''}</small>
          </button>
        ))}
        <button className="text-button commission-open" onClick={() => navigate(tab === 'SmartLab' ? '/smartlab' : '/srs-analyzer')}>
          {tab}에서 열기 ↗
        </button>
      </div>
      {tab === 'SmartLab' ? <LabTests query={labs} projectId={projectId} /> : <SrsMeasurements query={srs} projectId={projectId} />}
    </>
  )
}

function LabTests({ query, projectId }: { query: { data?: LabTestRow[]; isLoading: boolean; error: unknown }; projectId: number }) {
  const [open, setOpen] = useState<number | null>(null)
  if (query.isLoading) return <Loading />
  if (query.error) return <ErrorBox error={query.error} />
  const rows = query.data ?? []
  if (rows.length === 0) return <div className="empty" style={{ padding: 28 }}>이 프로젝트에 연결된 SmartLab 랩 테스트가 없습니다.</div>
  return (
    <div className="data-wrap">
      <table className="commission-table">
        <thead>
          <tr><th>테스트명</th><th>종류</th><th>담당</th><th>시작</th><th>종료</th><th>조건</th><th>측정</th><th>승인</th></tr>
        </thead>
        <tbody>
          {rows.map((t) => (
            <Fragment key={t.id}>
              <tr className={open === t.id ? 'open' : ''}>
                <td data-label="테스트명">
                  <button className="row-title" onClick={() => setOpen(open === t.id ? null : t.id)} aria-expanded={open === t.id}>
                    {open === t.id ? '▾' : '▸'} {t.name}
                  </button>
                  {t.description && <small className="commission-sub">{t.description}</small>}
                </td>
                <td data-label="종류">{t.type ?? '-'}</td>
                <td data-label="담당">{t.operator ?? '-'}</td>
                <td data-label="시작">{day(t.startedAt)}</td>
                <td data-label="종료">{day(t.endedAt)}</td>
                <td data-label="조건">{t.conditionCount}</td>
                <td data-label="측정">{t.measuredCount}/{t.conditionCount}</td>
                <td data-label="승인">{t.approvedCount}/{t.conditionCount}</td>
              </tr>
              {open === t.id && (
                <tr className="commission-detail-row"><td colSpan={8}><LabTestDetail projectId={projectId} testId={t.id} type={t.type} name={t.name} /></td></tr>
              )}
            </Fragment>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function LabTestDetail({ projectId, testId, type, name }: { projectId: number; testId: number; type?: string; name: string }) {
  const [exporting, setExporting] = useState(false)
  const [exportError, setExportError] = useState<unknown>(null)
  const exportExcel = async () => {
    setExporting(true)
    setExportError(null)
    try {
      await downloadFile(`/api/projects/${projectId}/lab-tests/${testId}/export`, `${name.replace(/[\/:*?"<>|]/g, '_')}_결과.xlsx`)
    } catch (e) {
      setExportError(e)
    } finally {
      setExporting(false)
    }
  }
  const { data, isLoading, error } = useQuery({
    queryKey: ['project-lab-test', projectId, testId],
    queryFn: () => api<LabCondition[]>(`/api/projects/${projectId}/lab-tests/${testId}`),
  })
  if (isLoading) return <Loading />
  if (error) return <ErrorBox error={error} />
  if (!data?.length) return <p className="commission-empty">등록된 조건이 없습니다.</p>
  // SmartLab 결과 화면처럼 종류별 결과 칸: Pattern = Max · Pattern(mm) / DFT = DFT · Stdev · TE / 모르면 둘 다
  const isPattern = type !== 'DFT'
  const isDft = type !== 'Pattern'
  return (
    <div className="commission-detail">
      <div className="commission-detail-head">
        <p className="commission-note">
          조건별 도장 파라미터와 결과 — SmartLab 결과 화면과 같은 계산
        {isPattern && ' · Pattern(mm) = 상위 3개 평균의 25% 이상 구간 × 20mm'}
          {isDft && ' · TE는 SVR 45(SmartLab 기본값) 기준'}
        </p>
        <button className="secondary commission-export" onClick={exportExcel} disabled={exporting}>
          {exporting ? '만드는 중…' : '엑셀 내보내기'}
        </button>
      </div>
      {exportError ? <ErrorBox error={exportError} /> : null}
      <div className="data-wrap">
        <table>
          <thead>
            <tr>
              <th>No.</th><th>채택</th>{PARAM_LABELS.map((l) => <th key={l}>{l}</th>)}
              {isPattern && <><th>Max</th><th>Pattern(mm)</th></>}
              {isDft && <><th>DFT(µm)</th><th>Stdev</th><th>TE(%)</th></>}
            </tr>
          </thead>
          <tbody>
            {data.map((c) => (
              <tr key={c.number}>
                <td>{c.number}</td>
                <td>{c.approved ? <span className="badge green">★</span> : ''}</td>
                {PARAM_LABELS.map((l, i) => <td key={l}>{num(c.params[i], 2)}</td>)}
                {isPattern && <><td><b>{num(c.max, 2)}</b></td><td><b>{num(c.patternMm, 0)}</b></td></>}
                {isDft && <><td><b>{num(c.dft, 2)}</b></td><td>{num(c.stdev, 2)}</td><td>{num(c.te, 2)}</td></>}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function SrsMeasurements({ query, projectId }: { query: { data?: SrsRow[]; isLoading: boolean; error: unknown }; projectId: number }) {
  const [open, setOpen] = useState<number | null>(null)
  if (query.isLoading) return <Loading />
  if (query.error) return <ErrorBox error={query.error} />
  const rows = query.data ?? []
  if (rows.length === 0) return <div className="empty" style={{ padding: 28 }}>이 프로젝트에 연결된 SRS 측정이 없습니다.</div>
  return (
    <div className="data-wrap">
      <table className="commission-table">
        <thead>
          <tr><th>측정 위치</th><th>측정일</th><th>이력 기간</th><th>로봇</th><th>모델</th><th>색상</th><th>최대 속도</th><th>평균 유량</th></tr>
        </thead>
        <tbody>
          {rows.map((m) => (
            <Fragment key={m.id}>
              <tr className={open === m.id ? 'open' : ''}>
                <td data-label="측정 위치">
                  <button className="row-title" onClick={() => setOpen(open === m.id ? null : m.id)} aria-expanded={open === m.id}>
                    {open === m.id ? '▾' : '▸'} {[m.booth, m.zone].filter(Boolean).join(' > ') || '위치 미지정'}
                  </button>
                  <small className="commission-sub">{[m.customer, m.factory].filter(Boolean).join(' · ')}</small>
                </td>
                <td data-label="측정일">{day(m.capturedAt)}</td>
                <td data-label="이력 기간">{range(m.historyRange)}</td>
                <td data-label="로봇">{m.robotCount ?? '-'}</td>
                <td data-label="모델">{m.modelCount ?? '-'}</td>
                <td data-label="색상">{m.colorCount ?? '-'}</td>
                <td data-label="최대 속도">{num(m.maxRobotVelocity)}</td>
                <td data-label="평균 유량">{num(m.avgFlowRate)}</td>
              </tr>
              {open === m.id && (
                <tr className="commission-detail-row"><td colSpan={8}><SrsDetailView projectId={projectId} id={m.id} /></td></tr>
              )}
            </Fragment>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function SrsDetailView({ projectId, id }: { projectId: number; id: number }) {
  const { data, isLoading, error } = useQuery({
    queryKey: ['project-srs-detail', projectId, id],
    queryFn: () => api<SrsDetail>(`/api/projects/${projectId}/srs-measurements/${id}`),
  })
  if (isLoading) return <Loading />
  if (error) return <ErrorBox error={error} />
  if (!data) return null
  return (
    <div className="commission-detail">
      {data.robots.length > 0 ? (
        <div className="data-wrap">
          <table>
            <thead>
              <tr><th>로봇</th><th>기종</th><th>도장기</th><th>평균 속도</th><th>건 거리</th><th>평균 건 ON 시간</th><th>평균 전압</th><th>평균 도료량</th></tr>
            </thead>
            <tbody>
              {data.robots.map((r, i) => (
                <tr key={i}>
                  <td><b>{r.name ?? `#${i + 1}`}</b></td>
                  <td>{r.type ?? '-'}</td>
                  <td>{r.tool ?? '-'}</td>
                  <td>{r.avgVelocity ?? '-'}</td>
                  <td>{r.gunDistance ?? '-'}</td>
                  <td>{r.avgGunOnTime ?? '-'}</td>
                  <td>{r.hvAvg ?? '-'}</td>
                  <td>{r.paintVolAvg ?? '-'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <p className="commission-empty">로봇별 요약이 없습니다.</p>
      )}
      <div className="commission-chips">
        <span className="commission-chip-label">모델 {data.models.length}</span>
        {data.models.map((n, i) => <span key={i} className="badge">{n}</span>)}
      </div>
      <div className="commission-chips">
        <span className="commission-chip-label">색상 {data.colors.length}</span>
        {data.colors.map((n, i) => <span key={i} className="badge">{n}</span>)}
      </div>
      {data.brushCount != null && <p className="commission-note">브러시 {data.brushCount}개</p>}
    </div>
  )
}
