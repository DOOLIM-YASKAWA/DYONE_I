// 기존 dyone DB(PostgreSQL 18) 기준 API 타입

export type Section = 'progress' | 'modular' | 'lab' | 'journal' | 'es'

export interface User {
  id: string
  empNo: string
  loginId: string
  name: string
  position: string
  displayName: string
  department: string
  role: string
}

export interface LoginResponse {
  accessToken: string
  expiresAt: string
  user: User
}

export interface PagedResult<T> {
  total: number
  page: number
  pageSize: number
  items: T[]
}

export interface Code {
  id: number
  code: string
  name: string
}

/** project.project (그룹웨어 applet285 미러) */
export interface Project {
  id: number
  code: string
  name: string
  partnerName?: string
  statusId?: number
  status?: string
  type?: string
  erpRegDate?: string
  startDate?: string
  endDate?: string
  isActive: boolean
  updatedAt: string
  description?: string
  sourceUpdatedAt?: string
  effectiveStage?: string
  effectiveSource?: 'dyone' | 'gw'
  /** 대표 단계의 상태(진행·완료). 목록·홈·상세가 같은 규칙으로 계산 */
  effectiveStatus?: ProgressStatus
  /** 최근 변경 = 그룹웨어 문서 수정일과 DY ONE 단계·진행 상황 변경 중 더 최근 것 */
  lastChangedAt?: string
  /** 최근 활동 종류(진행 상황 변경·파일 업로드·인원 배정·그룹웨어 수정·ERP 등록·모듈라 작업·ES 이슈·SmartLab 테스트·SRS 측정) */
  lastChangedSource?: string
  /** 최근 활동을 한 사람(알 수 있을 때) */
  lastChangedBy?: string
  /** 최근 7일 활동 수 */
  recentActivity7d?: number
  /** 프로젝트 PM(project.project_member, DY ONE에서 지정) */
  pmName?: string
}

/** project.project_assignment → plant.* */
export interface Assignment {
  level: string
  customer?: string
  factory?: string
  booth?: string
  zone?: string
}

export interface ProjectDetail {
  project: Project
  assignments: Assignment[]
  apps: string[]
  labTestCount: number
  dyoneStage?: { stageId: number; stageCode: string; stageName: string; updatedAt: string; updatedByName?: string; updatedByDept?: string } | null
  /** 단계별 진행 상황(S1~S9 순서). 저장 전이면 대표 단계로 추정한 값(updatedAt 없음) */
  progress?: ProgressItem[]
  /** 대표(현재) 단계 — 목록의 진행 단계와 같음 */
  currentStageId?: number | null
}

/** project.project_file — 프로젝트별 업로드 문서(dyone 자체) */
export interface ProjectFile {
  id: number
  projectId: number
  categoryId?: number
  category?: string
  categoryCode?: string
  stageId?: number
  stage?: string
  fileName: string
  contentType?: string
  sizeBytes: number
  description?: string
  uploadedByName?: string
  departmentId?: number
  department?: string
  uploadedAt: string
}

/** smartlab.lab_test */
export interface LabTest {
  id: number
  name: string
  type?: string
  projectId?: number
  projectCode?: string
  projectName?: string
  operator?: string
  startedAt?: string
  endedAt?: string
  description?: string
}

export interface HomeSummary {
  customerCounts: { name: string; count: number }[]
  /** 내가 관여한 프로젝트: PM·투입 인원 또는 내가 진행 상황 변경·파일 업로드·인원 배정 */
  myProjects: { project: Project; myLastAt: string; reasons: string[] }[]
  recentChanged: Project[]
  sync: { source: string; entity: string; updatedAt?: string }[]
}

export interface AiSource {
  kind: 'project' | 'lab'
  id: number
  label: string
}

export interface AskResponse {
  answer: string
  sources: AiSource[]
  engine: string
}

export interface AppNotification {
  id: number
  title: string
  body: string
  link?: string
  createdAt: string
  readAt?: string
}

/** modular.* — 모듈라빌드 프로젝트 정보 (그룹웨어 applet 376 이관) */
export interface ModularStatus {
  id: number
  name: string
  sort: number
  color?: string
  isEnd: boolean
  isBegin: boolean
}

export interface ModularTransition {
  id: number
  from: number
  to: number
  action: string
  sort: number
}

export interface ModularOption {
  code: number
  label: string
  isDefault: boolean
}

export type ModularListKey = 'install_location' | 'robot_model' | 'cable_robot_box' | 'cable_box_rc' | 'comm' | 'applicator' | 'category' | 'region'
export type ModularRole = 'pm' | 'pl' | 'control' | 'design' | 'teach'

export interface ModularMeta {
  statuses: ModularStatus[]
  transitions: ModularTransition[]
  options: Partial<Record<ModularListKey, ModularOption[]>>
  roles: Record<ModularRole, string>
  editEnabled: boolean
  syncedAt?: string
}

export interface ModularBuild {
  id: number
  fromGroupware: boolean
  year?: number
  status?: ModularStatus
  projectCode: string
  projectName: string
  installLocations: string[]
  shipMonth?: string
  installStart?: string
  commissioningStart?: string
  category?: string
  shipDue?: string
  region?: string
  robotCount?: string
  robotModels: string[]
  applicators: string[]
  comms: string[]
  plant?: string
  customer?: string
  process?: string
  processDetail?: string
  externalInspection?: string
  updatedAt?: string
  /** DY ONE 관리 전환: 그룹웨어 변경을 더 이상 반영하지 않는 건 */
  managedInDyone: boolean
  /** 그룹웨어에서 문서가 지워진 건(DY ONE에서는 남겨 둠) */
  gwDeleted: boolean
}

export interface ModularList extends PagedResult<ModularBuild> {
  years: number[]
  statusCounts: { statusId?: number; count: number }[]
}

export interface ModularGanttItem {
  id: number
  projectCode: string
  projectName: string
  start: string
  end: string
  endIsEstimate: boolean
  commissioningStart?: string
  status?: ModularStatus
}

export interface ModularFields {
  year?: number
  projectCode: string
  projectName: string
  installStart?: string
  installStartRaw?: string
  commissioningStart?: string
  commissioningStartRaw?: string
  externalInspection?: string
  shipDue?: string
  shipMonth?: string
  originToolReceipt?: string
  note?: string
  customer?: string
  plant?: string
  process?: string
  processDetail?: string
  robotCount?: number
  robotCountRaw?: string
  categoryCode?: number
  regionCode?: number
  mainIssue?: string
  attachmentCount: number
  createdByName?: string
  createdAt?: string
  updatedByName?: string
  updatedAt?: string
  syncedAt?: string
  gwDocId?: number
}

export interface ModularDetail {
  item: ModularBuild
  fields: ModularFields
  options: Record<string, number[]>
  members: { role: ModularRole; roleLabel: string; name: string; employeeId?: number }[]
  activities: ModularActivity[]
  files: ModularFile[]
  history: ModularHistoryItem[]
  transitions: ModularTransition[]
  project?: { id: number; code: string; name: string; partnerName?: string }
  /** DY ONE 값이 그룹웨어 값과 다른 칸 */
  differences: { key: string; label: string; dyOne: string; groupware: string }[]
  fromGroupware: boolean
  managedInDyone: boolean
  managedInDyoneAt?: string
  managedInDyoneBy?: string
  gwDeletedAt?: string
  editable: boolean
  gwUrl?: string
}

export interface ModularFile {
  id: number
  source: 'doc' | 'activity'
  name?: string
  ext?: string
  size?: number
  /** DY ONE 저장소로 복사됐는지. false면 본체가 그룹웨어 서버에만 있다. */
  stored: boolean
  at?: string
}

export interface ModularActivity {
  id: number
  body?: string
  by?: string
  at?: string
  comments: { id: number; body?: string; by?: string; at?: string }[]
  files: ModularFile[]
}

export interface ModularHistoryItem {
  source: 'groupware' | 'dyone'
  kind: 'status' | 'edit' | 'created' | 'activity' | 'comment' | 'etc' | 'overwrite' | 'manage'
  title?: string
  detail?: string
  from?: string
  to?: string
  by?: string
  at?: string
}

/** works.* — 그룹웨어 WORKS 앱 공통(앱 번호 기준, 281 ES 이슈 등) */
export interface WorksStatus {
  id: number
  name: string
  sort: number
  color?: string
  isEnd: boolean
  isBegin: boolean
}

export interface WorksField {
  cid: string
  label: string
  fieldType: string
  valueType?: string
  required: boolean
  editable: boolean
  options?: { code: number; label: string; isDefault: boolean }[]
  props?: Record<string, unknown>
}

/** 그룹웨어 폼 배치: canvas · label · columns · column · hr · (필드) */
export interface WorksLayoutNode {
  type: string
  cid?: string
  label?: string
  count?: string
  children?: WorksLayoutNode[]
}

export interface WorksMeta {
  appletId: number
  name: string
  layout?: WorksLayoutNode
  viewColumns?: string[]
  fields: WorksField[]
  statuses: WorksStatus[]
  transitions: { id: number; from: number; to: number; action: string; sort: number }[]
  editEnabled: boolean
  syncedAt?: string
}

export interface WorksDocItem {
  id: number
  fromGroupware: boolean
  status?: WorksStatus
  display: Record<string, string | null>
  managedInDyone: boolean
  gwDeleted: boolean
  createdAt?: string
  createdBy?: string
}

export interface WorksDocList extends PagedResult<WorksDocItem> {
  statusCounts: { statusId: number; count: number }[]
}

export interface WorksDocDetail {
  id: number
  appletId: number
  status?: WorksStatus
  values: Record<string, string | null>
  display: Record<string, string | null>
  fromGroupware: boolean
  managedInDyone: boolean
  managedInDyoneBy?: string
  gwDeletedAt?: string
  createdByName?: string
  createdAt?: string
  updatedByName?: string
  updatedAt?: string
  differences: { key: string; label: string; dyOne: string; groupware: string }[]
  transitions: { id: number; from: number; to: number; action: string; sort: number }[]
  activities: ModularActivity[]
  files: ModularFile[]
  history: ModularHistoryItem[]
  editable: boolean
  gwUrl?: string
}

/** project.project_stage_history — 프로젝트 진행 단계 변경 이력(최신순). fromStage가 없으면 DY ONE에서 처음 지정 */
export interface StageHistoryItem {
  id: number
  changedAt: string
  changedByName?: string
  changedByDept?: string
  changedByPosition?: string
  fromStageId?: number
  fromStage?: string
  fromOrder?: number
  toStageId?: number
  toStage?: string
  toOrder?: number
  note?: string
  /** 단계별 진행 상황 변경이면 전·후 상태 */
  fromStatus?: ProgressStatus
  toStatus?: ProgressStatus
  /** dyone(화면에서 변경) · groupware(그룹웨어 상태 동기화: gwFrom → gwTo) */
  source?: 'dyone' | 'groupware'
  gwFrom?: string
  gwTo?: string
}

/** 단계별 진행 상황: 대기 · 진행 · 완료 */
export type ProgressStatus = 'wait' | 'run' | 'done'

export interface ProgressItem {
  stageId: number
  code: string
  name: string
  status: ProgressStatus
  updatedAt?: string
  updatedByName?: string
  /** 영업·수주처럼 그룹웨어 상태로 계산되는 단계(DY ONE에서 바꾸지 않음) */
  fromGroupware?: boolean
}
