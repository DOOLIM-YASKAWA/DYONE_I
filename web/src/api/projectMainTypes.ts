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

/** 최근 흐름 한 상자(NAS 작업 · ERP 발주). 묶기·꼬리표는 규칙, 추세·제목은 사내 AI(검사 통과 시) — 11-05 */
export interface BriefFlow { tag: string; trend: string; source: 'ai' | 'rule'; steps: { when: string; title: string; detail: string }[] }

/** '상세히' 창 자료(Worker가 브리핑과 함께 evidence에 담음, kind = 'detail') */
export interface BriefDetail {
  kind: 'detail'
  /** 단계는 하나만: DY ONE에서 사람이 정한 단계가 있으면 'DY ONE 예상 프로젝트 단계', 없으면 'ERP 상태' */
  stage: { label: string; value?: string; others?: string[]; changedAt?: string }
  /** 모듈라 상태(앞 순번 숫자 뗀 것) */
  modular?: { status: string; lastAt?: string }
  erp: { status?: string; from?: string; to?: string }
  process?: string
  nasDir?: string
  nas14: number
  nasDays14: number
  lastPo?: string
  changes: string[]
  nas?: BriefFlow
  po?: BriefFlow
}

/** GET /api/projects/main/brief-detail — 문서 일정(단계별 기준) · 이 프로젝트의 일정 주의 · 고객사 */
export interface BriefSchedule {
  code: string
  partner?: string
  schedule: { stage: string; name: string; from: string; to: string; raw: string; doc: string; path?: string; kind: string; mtime?: string; page?: string }[]
  alerts: { kind: 'late' | 'soon' | 'after_sop'; stage: string; due: string; reason: string }[]
}

export interface Brief {
  code: string
  codeLabel: string
  projectId?: number
  name: string
  sentence: string
  source: 'ai' | 'rule'
  changedYesterday: number
  evidence?: (Evidence | BriefDetail)[]
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
