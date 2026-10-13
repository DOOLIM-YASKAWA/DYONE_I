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
  dyoneStage?: { stageId: number; stageCode: string; stageName: string; updatedAt: string; updatedByName?: string } | null
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
  recentRegistered: Project[]
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
