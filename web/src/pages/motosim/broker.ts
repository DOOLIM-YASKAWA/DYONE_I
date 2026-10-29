import { useEffect, useState } from 'react'
import { tokenStore } from '../../api/client'
import { platform } from '../../platform/bridge'

/**
 * MotoSim 화면 ↔ Windows 앱 Local Broker(desktop/Broker/*.cs).
 * PC 작업(Cell 고르기·압축·전송·MotoSim 실행·플러그인 설치)은 설치형 앱에서만 된다. PC 웹에서는 조회·삭제·브라우저 다운로드만.
 */
export const isDesktop = platform.kind === 'desktop'

export interface CellPlan {
  canceled: false
  vclPath: string
  folder: string
  folderName: string
  motosimVersion?: string
  fileCount: number
  totalBytes: number
  includedFileCount: number
  includedBytes: number
  removedModelGroups: string[]
  excludedFolders: string[]
  excludedBackupFiles: string[]
  excludedBackupCount: number
}

export interface MotosimInstall {
  label: string
  path: string
  folder: string
  dllBinPath?: string
}

export interface MotosimStatus {
  installed: boolean
  installs: MotosimInstall[]
  running: boolean
}

export interface SmartCellStatus {
  state: 'ok' | 'update-ready' | 'problem' | 'unsupported'
  message: string
  channel?: 'full' | 'update'
  installedVersion?: string
  latestVersion?: string
  motoSimRunning: boolean
  dllPath: string
  diagnosis: { label: string; ok: boolean; detail: string }[]
}

/** 전송 진행(cell.progress) */
export interface TransferProgress {
  jobId: string
  phase: 'compressing' | 'upload' | 'finishing' | 'download' | 'extracting'
  done?: number
  total?: number
  percent: number
  bytesPerSecond?: number
  etaSeconds?: number
}

export interface RunEvent {
  runId: string
  stage: 'cleaning' | 'launching' | 'running' | 'closing' | 'closed' | 'failed'
  message?: string
  cleanupCount?: number
  label?: string
}

export const broker = <T>(cmd: string, args?: Record<string, unknown>) => platform.broker<T>(cmd, args)
/** 서버 호출이 필요한 명령에는 지금 로그인 토큰을 붙인다(앱은 토큰을 저장하지 않는다) */
export const brokerWithToken = <T>(cmd: string, args: Record<string, unknown> = {}) => broker<T>(cmd, { ...args, token: tokenStore.get() ?? '' })
export const newJobId = () => `job-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`

export function useBrokerEvent<T>(event: string, cb: (data: T) => void) {
  useEffect(() => platform.onBrokerEvent<T>(event, cb), [event, cb])
}

/** 고른 MotoSim 실행 파일(원본 런처와 같은 키) */
const RUN_PATH_KEY = 'motosim-selected-run-path'
export function useRunPath(): [string | null, (v: string | null) => void] {
  const [v, setV] = useState(() => platform.storage.get(RUN_PATH_KEY))
  return [v, (next) => { if (next) platform.storage.set(RUN_PATH_KEY, next); else platform.storage.remove(RUN_PATH_KEY); setV(next) }]
}

/** Zone마다 마지막으로 받거나 올린 내 PC의 .vcl(이 PC에서만) */
const zoneVclKey = (zoneId: number) => `motosim-zone-vcl:${zoneId}`
export const zoneVcl = {
  get: (zoneId: number) => platform.storage.get(zoneVclKey(zoneId)),
  set: (zoneId: number, path: string) => platform.storage.set(zoneVclKey(zoneId), path),
  remove: (zoneId: number) => platform.storage.remove(zoneVclKey(zoneId)),
}

/** 마지막 작업(허브의 "이어서 작업") */
const LAST_KEY = 'motosim-last-work'
export interface LastWork { zoneId: number; zoneName: string; path: string; at: string }
export const lastWork = {
  get(): LastWork | null {
    try { return JSON.parse(platform.storage.get(LAST_KEY) ?? 'null') as LastWork | null } catch { return null }
  },
  set: (w: LastWork) => platform.storage.set(LAST_KEY, JSON.stringify(w)),
}

export function fmtBytes(n?: number | null) {
  if (n == null) return '—'
  if (n >= 1024 ** 3) return `${(n / 1024 ** 3).toFixed(2)} GB`
  if (n >= 1024 ** 2) return `${(n / 1024 ** 2).toFixed(1)} MB`
  return `${Math.max(1, Math.round(n / 1024))} KB`
}

/** 원본 런처 표기: 2026. 10. 28. 18:46:00 (한국 시간) */
export const fmtDateTime = (s: string) =>
  new Date(s).toLocaleString('ko-KR', { timeZone: 'Asia/Seoul', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false })

export function timeAgo(s: string) {
  const sec = (Date.now() - new Date(s).getTime()) / 1000
  if (sec < 60) return '방금 전'
  if (sec < 3600) return `${Math.floor(sec / 60)}분 전`
  if (sec < 86400) return `${Math.floor(sec / 3600)}시간 전`
  if (sec < 86400 * 7) return `${Math.floor(sec / 86400)}일 전`
  return new Date(s).toLocaleDateString('ko-KR', { timeZone: 'Asia/Seoul' })
}

export const errorText = (e: unknown) => (e instanceof Error ? e.message : String(e))
