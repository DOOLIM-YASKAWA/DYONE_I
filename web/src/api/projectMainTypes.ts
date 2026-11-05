// 프로젝트 메인(첫 화면) API 모양 — server/src/DyOne.Api/Modules/Projects/ProjectMainModule.cs 와 같게

/** label = 선정 담당용(DB 값·숫자), plain = 전 직원용 쉬운 말(금액 없음), group = size(규모) · now(지금 진행) */
export interface ScorePart { key: string; pts: number; label: string; plain?: string; group?: 'size' | 'now' }

/** 내 관심·회사 주요 공통 한 줄. projectId가 없으면 ERP에만 있는 코드(상세 화면 없음) */
export interface MainRow {
  code: string
  codeLabel: string
  projectId?: number
  name: string
  partnerName?: string
  erpStatus?: string
  stage?: string
  pmName?: string
  amountEok?: number
  next?: string
  lastActivityAt?: string
  lastActivityLabel?: string
  category?: string
  rank?: number
  score?: number
  reason?: string
  parts?: ScorePart[]
  signals?: string[]
  decision?: 'selected' | 'excluded'
  favorite: boolean
}

export interface Evidence { kind: 'modular' | 'nas' | 'po' | 'post'; label: string; path?: string; at?: string }

export interface Brief {
  code: string
  codeLabel: string
  projectId?: number
  name: string
  sentence: string
  source: 'ai' | 'rule'
  changedYesterday: number
  evidence?: Evidence[]
  madeAt: string
}

/** selected = 앞쪽 선정 건수(회사 주요). 나머지 items는 추천안 */
export interface MainList { total: number; items: MainRow[]; mode?: 'selected' | 'recommended'; selected: number }

/** 일정 주의 한 줄. kind: late 지연 의심 · soon 일정 임박 · after_sop 양산 후 작업 중 · erp ERP 기간 갱신 필요(관리자만). days = 오늘 − 기준일 */
export interface ScheduleItem {
  code: string
  codeLabel: string
  projectId?: number
  name: string
  kind: 'late' | 'soon' | 'after_sop' | 'erp'
  stage: string
  stageName: string
  dueDate: string
  days: number
  reason: string
  evidence?: { doc?: string; kind?: string; page?: string; line?: string; modular?: string }
  scope: 'fav' | 'feat' | 'other'
  firstSeen: string
}

export interface MainData {
  erpOrdered: number
  erpSales: number
  favorites: MainList
  featured: MainList
  favBriefs: Brief[]
  featBriefs: Brief[]
  briefAt?: string
  runDate?: string
  candidates: number
  canSelect: boolean
  schedule?: ScheduleItem[]
  scheduleTotal: number
}
