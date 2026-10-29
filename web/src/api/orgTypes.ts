/** 조직도(org.*, 그룹웨어 조직도 미러) */
export interface OrgDepartment {
  id: number
  parentId?: number | null
  name: string
  sortOrder: number
}

export interface OrgEmployee {
  id: number
  name: string
  departmentId?: number | null
  position?: string
  positionRank: number
  duty?: string
  dutyOrder?: number | null
  email?: string
  loginId?: string
  /** PM을 맡은 프로젝트 수 */
  pmCount: number
  /** 투입 인원으로 들어간 프로젝트 수(PM 제외) */
  memberCount: number
  /** 휴대폰(그룹웨어 프로필) */
  mobile?: string
  /** 회사 직통·내선 */
  directTel?: string
}

export interface OrgData {
  departments: OrgDepartment[]
  employees: OrgEmployee[]
}

export interface OrgEmployeeProject {
  projectId: number
  code: string
  name: string
  role: 'pm' | 'member'
  task?: string
  assignedAt: string
}

/** project.project_member — 프로젝트 인원(PM · 투입 인원) */
export interface ProjectMember {
  id: number
  employeeId: number
  name: string
  department?: string
  position?: string
  duty?: string
  email?: string
  role: 'pm' | 'member'
  task?: string
  assignedAt: string
  assignedByName?: string
  mobile?: string
  directTel?: string
}
