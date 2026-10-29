// MotoSim (MotoSim Launcher 이관) — /api/motosim/* 와 Windows 앱 Broker(cell.* · motosim.* · smartcell.*)
export interface MotosimNode {
  id: number
  parentId: number | null
  name: string
}

export interface MotosimLatest {
  fileName: string
  uploadedAt: string
  uploadedByName?: string
  fileSize?: number
  motosimVersion?: string
}

export interface MotosimZone {
  id: number
  boothId: number
  name: string
  zoneType?: string
  fileCount: number
  latest?: MotosimLatest
}

export interface MotosimTree {
  customers: MotosimNode[]
  factories: MotosimNode[]
  booths: MotosimNode[]
  zones: MotosimZone[]
}

export interface MotosimCellFile {
  id: number
  fileName: string
  fileUrl?: string
  fileSize?: number
  motosimVersion?: string
  commitMessage?: string
  uploadedByName?: string
  uploadedByDepartment?: string
  uploadedAt: string
  isLatest: boolean
  projectId?: number
  projectCode?: string
  projectName?: string
  /** nas = DY ONE NAS(내려받기 가능) · drive = 원본 런처 Google Drive에만 있음 */
  source: 'nas' | 'drive'
}

export interface MotosimProject {
  id: number
  code: string
  name: string
}

export interface MotosimNotice {
  id: number
  title: string
  body: string
  authorName?: string
  createdAt: string
  updatedAt: string
}

export type MotosimActivityKind = 'cell-upload' | 'cell-download' | 'cell-delete' | 'notice'

export interface MotosimActivity {
  id: number
  kind: MotosimActivityKind
  summary?: string
  actorName?: string
  createdAt: string
  zoneId?: number
  zonePath?: string
  projectId?: number
  projectCode?: string
  noticeId?: number
}

export interface MotosimHub {
  notices: MotosimNotice[]
  activities: MotosimActivity[]
  stats: { kind: MotosimActivityKind; today: number; week: number; month: number }[]
  daily: { day: string; uploads: number; downloads: number }[]
  cellCount: number
  zoneCount: number
  storageBytes: number
  driveOnlyCount: number
}

export interface MotosimAdmin {
  uploads: { id: string; fileName: string; totalSize: number; received: number; status: string; createdByName?: string; createdAt: string; updatedAt: string; zonePath?: string }[]
  trashBytes: number
  trashCount: number
  tempBytes: number
}
