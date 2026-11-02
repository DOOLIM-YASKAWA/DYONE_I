import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { type DragEvent, Fragment, type ReactNode, useMemo, useRef, useState } from 'react'
import { api, downloadFile } from '../../api/client'
import { useIsPartner } from '../../auth/AuthContext'
import type { Code, ProjectCategory, ProjectFile, Share } from '../../api/types'
import { ErrorBox, Loading } from '../../components/ui'
import { classifyDocs, useAiStatus, type DocItem } from '../../ai/aiClient'
import { CATEGORY_HELP, suggestCategory } from './docClassify'
import { FilePreview } from './FilePreview'
import '../../styles/project-docs.css'

const date = (v?: string) => (v ? new Date(v).toLocaleDateString('ko-KR') : '-')
const size = (n: number) => (n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(0)} KB` : `${(n / 1048576).toFixed(1)} MB`)
const extOf = (name: string) => (name.includes('.') ? (name.split('.').pop() ?? '').slice(0, 4).toUpperCase() : '') || 'FILE'

/** 올리지 않을 파일(탐색기·Office가 만드는 부산물) */
const SKIP = /^(thumbs\.db|desktop\.ini|\.ds_store)$|^~\$/i

type Picked = { file: File; path: string }

/** 끌어다 놓은 항목을 파일 목록으로. 폴더째 놓으면 하위 파일을 모두 읽고 폴더 경로(분류 근거)를 남긴다. */
async function readDrop(dt: DataTransfer): Promise<Picked[]> {
  // getAsEntry는 이벤트 처리 중에만 유효하다 → 먼저 동기로 모두 뽑아 둔다
  const entries = Array.from(dt.items ?? []).map((i) => i.webkitGetAsEntry?.()).filter((e): e is FileSystemEntry => !!e)
  if (entries.length === 0) return Array.from(dt.files).map((file) => ({ file, path: file.name }))
  const out: Picked[] = []
  const walk = async (entry: FileSystemEntry, prefix: string): Promise<void> => {
    if (entry.isFile) {
      const file = await new Promise<File>((res, rej) => (entry as FileSystemFileEntry).file(res, rej))
      out.push({ file, path: prefix + file.name })
    } else if (entry.isDirectory) {
      const reader = (entry as FileSystemDirectoryEntry).createReader()
      // readEntries는 한 번에 일부만 준다 → 빈 배열이 나올 때까지 읽는다
      for (;;) {
        const batch = await new Promise<FileSystemEntry[]>((res, rej) => reader.readEntries(res, rej))
        if (batch.length === 0) break
        for (const e of batch) await walk(e, `${prefix}${entry.name}/`)
      }
    }
  }
  for (const e of entries) await walk(e, '')
  return out
}

type RowStatus = 'wait' | 'up' | 'done' | 'err'
interface PendingRow {
  key: string
  file: File
  path: string
  categoryId: number | null
  /** 프로젝트별 직접 입력 종류(있으면 categoryId 대신) */
  projectCategoryId?: number | null
  stageId: number | null
  reasons: string[]
  dupe: boolean
  /** 폴더째 올린 묶음(맨 위 폴더 하나 = 묶음 하나). 낱개 파일은 없음 */
  bundle?: { id: string; name: string }
  /** 묶음 안 폴더 경로(맨 위 폴더부터, 파일 이름 제외) */
  relDir?: string
  /** 사람이 직접 고름 — AI 결과가 늦게 와도 덮지 않는다 */
  manual?: boolean
  /** AI 분류 확신(0~1). 낮으면 확인 표시 */
  aiConfidence?: number
  status: RowStatus
  error?: string
}

/** 목록 한 줄: 낱개 파일 또는 폴더 묶음 */
type Entry = { kind: 'file'; file: ProjectFile } | { kind: 'bundle'; id: string; name: string; files: ProjectFile[] }
/** 파일들을 목록 줄로: 같은 묶음은 한 줄(처음 나온 자리에) */
function toEntries(files: ProjectFile[]): Entry[] {
  const out: Entry[] = []
  const seen = new Map<string, Extract<Entry, { kind: 'bundle' }>>()
  for (const f of files) {
    if (!f.bundleId) { out.push({ kind: 'file', file: f }); continue }
    let b = seen.get(f.bundleId)
    if (!b) { b = { kind: 'bundle', id: f.bundleId, name: f.relPath?.split('/')[0] ?? '폴더', files: [] }; seen.set(f.bundleId, b); out.push(b) }
    b.files.push(f)
  }
  return out
}

/** 왼쪽 필터(한 번에 하나만): all · recent(최근 7일) · mine(내가 올린) · share:<dept|project> · cat:<id|none> · pcat:<id> · stage:<id|none> */
type Filter = string
const WEEK = 7 * 86400000
const NONE = 'none'
const SHARE_LABEL: Record<Share, string> = { me: '개인', dept: '부서', project: '전체' }

/** 자료함 첫 화면의 종류 폴더 순서 = NAS 13칸 + 현장 백업(10번). 화면 번호만 이 순서로 붙인다(NAS 경로 인식은 docClassify가 원래 번호로). */
const FOLDER_ORDER = ['quote', 'spec', 'schedule', 'design', 'modula', 'teach', 'plc', 'hmi', 'commission', 'backup', 'manual', 'report', 'final', 'etc']
/** 폴더 열쇠: c:<기준 코드 id> · p:<프로젝트 종류 id> · none(종류 없음) */
const folderOf = (f: ProjectFile) => (f.projectCategoryId != null ? `p:${f.projectCategoryId}` : f.categoryId != null ? `c:${f.categoryId}` : NONE)

const FOLDER_BACK = 'M3 6.5A1.5 1.5 0 0 1 4.5 5H9.2l2 2.2h8.3A1.5 1.5 0 0 1 21 8.7V18a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 18z'
const FOLDER_FRONT = 'M3 10.2a1.5 1.5 0 0 1 1.5-1.5h15a1.5 1.5 0 0 1 1.5 1.5V18a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 18z'
const FILE_D = 'M14 3H6.5A1.5 1.5 0 0 0 5 4.5v15A1.5 1.5 0 0 0 6.5 21h11a1.5 1.5 0 0 0 1.5-1.5V8zM14 3v5h5'
const TONE: Record<string, string> = {
  PDF: 'pdf', DWG: 'cad', DXF: 'cad', DWL: 'cad', SLDP: 'cad', SLDA: 'cad', STEP: 'cad', STP: 'cad',
  XLSX: 'sheet', XLS: 'sheet', CSV: 'sheet', JBI: 'robot', JPG: 'img', JPEG: 'img', PNG: 'img', HEIC: 'img',
}
function FolderIcon({ empty, size: s = 16 }: { empty?: boolean; size?: number }) {
  return (
    <span className={`dx-tile${empty ? ' off' : ''}`}>
      <svg width={s} height={s} viewBox="0 0 24 24" aria-hidden="true"><path className="fb" d={FOLDER_BACK} /><path className="ff" d={FOLDER_FRONT} /></svg>
    </span>
  )
}
function FileIcon({ name }: { name: string }) {
  const ext = extOf(name)
  return (
    <span className={`dx-tile ${TONE[ext] ?? 'plain'}`} title={ext}>
      <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={FILE_D} /></svg>
    </span>
  )
}
const md = (v?: string) => (v ? `${new Date(v).getMonth() + 1}.${String(new Date(v).getDate()).padStart(2, '0')}` : '')
/** 종류 선택칸 값: c:<기준 코드 id> · p:<프로젝트 종류 id> */
const catValue = (categoryId?: number | null, projectCategoryId?: number | null) =>
  projectCategoryId != null ? `p:${projectCategoryId}` : categoryId != null ? `c:${categoryId}` : ''
const parseCat = (v: string) => (v.startsWith('p:') ? { categoryId: null, projectCategoryId: Number(v.slice(2)) } : { categoryId: Number(v.slice(2)), projectCategoryId: null })

/**
 * 프로젝트 자료함. 단계와 상관없이 프로젝트에 자료를 모으고, 종류·단계는 꼬리표로 붙인다(둘 다 나중에 바꿀 수 있음).
 * - 올리기: 끌어다 놓기(파일·폴더) → 확인 목록에서 파일마다 종류·단계 확인(자동 제안) → 모두 올리기
 * - 보기: 종류 / 단계 / 올린 부서로 묶어 보기 전환, 최근 올린 자료, 이름·꼬리표 검색
 * - stage: 단계 화면에서 열었을 때 그 단계(올릴 때 기본 단계 + 처음 보기를 그 단계로)
 * - onlyCategory: 한 종류만 보고 바로 올리는 간단한 모양(사양서 분석 탭)
 */
export function ProjectFilesTab({ projectId, stage, onlyCategory, rowActions }: {
  projectId: number
  stage?: Code
  /** 예전 호출 호환용(이제 stage가 있으면 처음 보기가 그 단계) */
  filterByStage?: boolean
  /** 이 분류(코드)의 문서만 보이고 올린다 */
  onlyCategory?: string
  /** 행마다 다운로드 앞에 붙일 버튼(예: 사양서 분석 탭의 '사양서 분석') */
  rowActions?: (f: ProjectFile) => ReactNode
}) {
  const qc = useQueryClient()
  const partner = useIsPartner() // 협력사: 목록·내려받기만(올리기는 협력사 일지 단계에서)
  const inputRef = useRef<HTMLInputElement>(null)
  const folderRef = useRef<HTMLInputElement>(null)
  const [drag, setDrag] = useState(false)
  const [pending, setPending] = useState<PendingRow[]>([])
  const [description, setDescription] = useState('')
  const [uploading, setUploading] = useState(false)
  const [filter, setFilter] = useState<Filter>(stage && !onlyCategory ? `stage:${stage.id}` : 'all')
  const [preview, setPreview] = useState<number | null>(null)
  const [now] = useState(() => Date.now()) // '최근 7일' 기준 시각(화면을 연 때)
  const [query, setQuery] = useState('')
  const [share, setShare] = useState<Share>('me') // 올릴 때 공유 범위(기본 개인)
  const [newCatFor, setNewCatFor] = useState<string | null>(null) // 직접 입력 중인 확인 목록 줄
  const [newCatName, setNewCatName] = useState('')
  const [help, setHelp] = useState(false) // 종류 도움말 펼침
  const [folder, setFolder] = useState<string | null>(null) // 들어가 있는 종류 폴더(없으면 첫 화면)
  const [hideEmpty, setHideEmpty] = useState(false)
  const [overFolder, setOverFolder] = useState<string | null>(null) // 끌어다 놓는 중인 폴더 줄
  const ai = useAiStatus()
  const [aiRun, setAiRun] = useState<'idle' | 'running' | 'error'>('idle') // 사내 AI 분류 진행
  const [aiError, setAiError] = useState('')

  const categories = useQuery({ queryKey: ['codes', 'document_category'], queryFn: () => api<Code[]>('/api/codes/document_category') })
  const stages = useQuery({ queryKey: ['codes', 'project_stage'], queryFn: () => api<Code[]>('/api/codes/project_stage') })
  const files = useQuery({ queryKey: ['project-files', projectId], queryFn: () => api<ProjectFile[]>(`/api/projects/${projectId}/files`) })
  const projectCats = useQuery({ queryKey: ['project-categories', projectId], queryFn: () => api<ProjectCategory[]>(`/api/projects/${projectId}/categories`) })
  const refresh = () => qc.invalidateQueries({ queryKey: ['project-files', projectId] })

  const del = useMutation({
    mutationFn: (id: number) => api(`/api/projects/${projectId}/files/${id}`, { method: 'DELETE' }),
    onSuccess: refresh,
  })
  // 업로드 후 꼬리표 변경: 종류(실물 폴더도 옮김) · 단계(꼬리표만)
  const recategorize = useMutation({
    mutationFn: ({ id, categoryId, projectCategoryId }: { id: number; categoryId: number | null; projectCategoryId: number | null }) =>
      api(`/api/projects/${projectId}/files/${id}`, { method: 'PUT', json: { categoryId, projectCategoryId } }),
    onSuccess: refresh,
  })
  // 공유 범위 변경 — 올린 사람만(서버도 확인)
  const reshare = useMutation({
    mutationFn: ({ id, share }: { id: number; share: Share }) => api(`/api/projects/${projectId}/files/${id}/share`, { method: 'PUT', json: { share } }),
    onSuccess: refresh,
  })
  // 폴더 묶음째 바꾸기: 종류(실물 폴더째 이동) · 단계 · 공유 범위
  const bundleOp = useMutation({
    mutationFn: ({ bid, what, json }: { bid: string; what: 'category' | 'stage' | 'share'; json: unknown }) =>
      api(`/api/projects/${projectId}/files/bundles/${bid}/${what}`, { method: 'PUT', json }),
    onSuccess: refresh,
  })
  // 종류 직접 입력: 이 프로젝트에만 추가(같은 이름이 있으면 그것)
  const addCat = useMutation({
    mutationFn: (name: string) => api<ProjectCategory>(`/api/projects/${projectId}/categories`, { method: 'POST', json: { name } }),
    onSuccess: (c, _name) => {
      qc.invalidateQueries({ queryKey: ['project-categories', projectId] })
      if (newCatFor) setRow(newCatFor, { categoryId: null, projectCategoryId: c.id, reasons: ['직접 입력'], manual: true })
      setNewCatFor(null)
      setNewCatName('')
    },
  })
  const delBundle = useMutation({
    mutationFn: (bid: string) => api(`/api/projects/${projectId}/files/bundles/${bid}`, { method: 'DELETE' }),
    onSuccess: refresh,
  })

  const cats = useMemo(() => categories.data ?? [], [categories.data])
  const pcats = projectCats.data ?? []
  const catCode = (id: number | null) => cats.find((c) => c.id === id)?.code

  // 한 종류만 보는 화면: 확인 목록 없이 그 종류·단계로 바로 올린다(예전 동작)
  const quick = useMutation({
    mutationFn: async (picked: File[]) => {
      const fd = new FormData()
      picked.forEach((f) => fd.append('files', f))
      const cid = cats.find((c) => c.code === onlyCategory)?.id
      if (cid != null) fd.append('categoryId', String(cid))
      if (stage) fd.append('stageId', String(stage.id))
      fd.append('share', 'project') // 사양서 분석에 올린 사양서는 모든 부서가 본다
      return api(`/api/projects/${projectId}/files`, { method: 'POST', body: fd })
    },
    onSuccess: refresh,
  })

  /** 고른 파일을 확인 목록에 넣는다(종류 자동 제안). target: 놓은 종류 폴더(c:id · p:id)면 그 종류로 정해 둔다 */
  const addPicked = (picked: Picked[], target?: string | null) => {
    const list = picked.filter((p) => !SKIP.test(p.file.name) && p.file.size > 0)
    if (list.length === 0) return
    if (onlyCategory) { quick.mutate(list.map((p) => p.file)); return }
    const existing = new Set((files.data ?? []).map((f) => f.fileName.toLowerCase()))
    // 폴더째 고르면 맨 위 폴더 하나가 묶음 하나(종류·단계는 묶음째 — 안 파일들 제안 중 가장 많은 것)
    const bundleOf = new Map<string, { id: string; name: string }>()
    for (const p of list) {
      const top = p.path.includes('/') ? p.path.split('/')[0] : null
      if (top && !bundleOf.has(top)) bundleOf.set(top, { id: crypto.randomUUID(), name: top })
    }
    const majority = new Map<string, { categoryId: number | null; reasons: string[] }>()
    for (const [top] of bundleOf) {
      const votes = new Map<number | null, number>()
      let reasons: string[] = []
      for (const p of list.filter((x) => x.path.startsWith(`${top}/`))) {
        const sg = suggestCategory(p.path, cats)
        votes.set(sg.categoryId, (votes.get(sg.categoryId) ?? 0) + (sg.reasons.length ? 2 : 1))
        if (!reasons.length) reasons = sg.reasons
      }
      const best = [...votes.entries()].sort((a, b) => b[1] - a[1])[0]
      majority.set(top, { categoryId: best?.[0] ?? null, reasons: best ? [`안 파일 ${list.filter((x) => x.path.startsWith(`${top}/`)).length}개 중 가장 많은 종류`, ...reasons] : [] })
    }
    const seen = new Set(pending.map((r) => r.path))
    {
      const fixed = target && target !== NONE ? parseCat(target) : null
      const fixedName = fixed && (fixed.projectCategoryId != null ? pcats.find((c) => c.id === fixed.projectCategoryId)?.name : cats.find((c) => c.id === fixed.categoryId)?.name)
      const rows = list.filter((p) => !seen.has(p.path)).map((p, i): PendingRow => {
        const top = p.path.includes('/') ? p.path.split('/')[0] : null
        const s = top ? majority.get(top)! : suggestCategory(p.path, cats)
        return {
          bundle: top ? bundleOf.get(top) : undefined,
          relDir: top ? p.path.split('/').slice(0, -1).join('/') : undefined,
          key: `${Date.now()}-${i}-${p.path}`,
          file: p.file,
          path: p.path,
          ...(fixed
            ? { ...fixed, reasons: [`'${fixedName ?? '고른'}' 폴더에 놓음`], manual: true }
            : { categoryId: s.categoryId, reasons: s.reasons }),
          // 자료함은 단계를 쓰지 않는다(단계 화면에서 열었을 때만 그 단계를 꼬리표로)
          stageId: stage?.id ?? null,
          dupe: existing.has(p.file.name.toLowerCase()),
          status: 'wait',
        }
      })
      setPending((cur) => [...cur, ...rows])
      // 종류는 사내 AI가 정한다(규칙 결과는 먼저 보여 주고 AI에게 참고로 넘긴다). AI가 없거나 폴더에 놓았으면 그대로
      if (!fixed && ai.data?.configured && rows.length > 0) void runAi(rows)
    }
  }

  /** 사내 AI 분류: 낱개 파일은 한 줄씩, 폴더 묶음은 묶음 하나로 묻는다 */
  const runAi = async (rows: PendingRow[]) => {
    setAiRun('running')
    setAiError('')
    try {
      const TEXT = /\.(txt|csv|ini|log|jbi|cnd|xml|json|md)$/i
      const items: DocItem[] = []
      for (const r of rows.filter((x) => !x.bundle)) {
        items.push({
          key: r.key, path: r.path, size: r.file.size,
          sample: TEXT.test(r.file.name) && r.file.size < 5_000_000 ? await r.file.slice(0, 1500).text() : undefined,
          ruleCode: catCode(r.categoryId), ruleReasons: r.reasons,
        })
      }
      const bundles = new Map<string, PendingRow[]>()
      for (const r of rows.filter((x) => x.bundle)) bundles.set(r.bundle!.id, [...(bundles.get(r.bundle!.id) ?? []), r])
      for (const [id, rs] of bundles) {
        items.push({
          key: `bundle:${id}`, path: rs[0].bundle!.name, size: rs.reduce((t, x) => t + x.file.size, 0),
          files: rs.slice(0, 40).map((x) => x.path), ruleCode: catCode(rs[0].categoryId), ruleReasons: rs[0].reasons,
        })
      }
      const result = await classifyDocs(items, cats.map((c) => ({ code: c.code, name: c.name, what: CATEGORY_HELP[c.code]?.what })))
      const byKey = new Map(result.map((x) => [x.key, x]))
      setPending((cur) => cur.map((r) => {
        const x = byKey.get(r.bundle ? `bundle:${r.bundle.id}` : r.key)
        const c = x && cats.find((k) => k.code === x.code)
        if (!x || !c || r.manual || r.status === 'done') return r
        return {
          ...r, categoryId: c.id, projectCategoryId: null, aiConfidence: x.confidence,
          reasons: [`사내 AI: ${x.reason}`],
        }
      }))
      setAiRun('idle')
    } catch (e) {
      setAiRun('error')
      setAiError(e instanceof Error ? e.message : '사내 AI 분류 실패')
    }
  }

  const pickInput = (input: HTMLInputElement | null) => {
    // FileList는 입력창에 연결된 라이브 객체라 value를 비우면 같이 비워진다 → 먼저 배열로 옮긴다
    const list = Array.from(input?.files ?? []).map((file) => ({ file, path: file.webkitRelativePath || file.name }))
    if (input) input.value = ''
    addPicked(list, folder) // 폴더 안에서 고르면 그 종류로
  }

  const setRow = (key: string, patch: Partial<PendingRow>) => setPending((cur) => cur.map((r) => (r.key === key ? { ...r, ...patch } : r)))
  const setAll = (patch: Partial<PendingRow>) => setPending((cur) => cur.map((r) => (r.status === 'done' ? r : { ...r, ...patch })))

  /** 파일마다 종류·단계가 달라서 한 개씩 올린다(진행 상황도 줄마다 보인다) */
  const uploadAll = async () => {
    setUploading(true)
    let failed = 0
    for (const r of pending) {
      if (r.status === 'done') continue
      setRow(r.key, { status: 'up', error: undefined })
      const fd = new FormData()
      fd.append('files', r.file)
      if (r.projectCategoryId != null) fd.append('projectCategoryId', String(r.projectCategoryId))
      else if (r.categoryId != null) fd.append('categoryId', String(r.categoryId))
      fd.append('share', share)
      if (r.bundle && r.relDir) { fd.append('bundleId', r.bundle.id); fd.append('relPath', r.relDir) }
      if (r.stageId != null) fd.append('stageId', String(r.stageId))
      if (description.trim()) fd.append('description', description.trim())
      try {
        await api(`/api/projects/${projectId}/files`, { method: 'POST', body: fd })
        setRow(r.key, { status: 'done' })
      } catch (e) {
        failed++
        setRow(r.key, { status: 'err', error: e instanceof Error ? e.message : '올리지 못했습니다' })
      }
    }
    setUploading(false)
    await refresh()
    // 다 올렸으면 목록을 닫고, 실패한 줄만 남겨 다시 올리게 한다
    setPending((cur) => cur.filter((r) => r.status !== 'done'))
    if (failed === 0) setDescription('')
  }

  if (files.isLoading || categories.isLoading || stages.isLoading) return <Loading />
  if (files.error) return <ErrorBox error={files.error} />

  const all = (files.data ?? []).filter((f) => !onlyCategory || f.categoryCode === onlyCategory)

  // 보기 범위(왼쪽, 하나만)와 검색어가 함께 걸린다. 종류는 폴더가 대신한다(자료함은 단계를 쓰지 않음)
  const test = (f: ProjectFile, k: Filter): boolean => {
    if (k === 'all') return true
    if (k === 'recent') return now - new Date(f.uploadedAt).getTime() < WEEK
    if (k === 'mine') return f.mine
    const [kind, v] = k.split(':')
    if (kind === 'share') return f.share === v
    return true
  }
  const q = query.trim().toLowerCase()
  const matches = (f: ProjectFile) =>
    !q || [f.fileName, f.description, f.category, f.uploadedByName, f.department].some((v) => v?.toLowerCase().includes(q))
  const scoped = all.filter((f) => test(f, filter))
  const n = (k: Filter) => toEntries(all.filter((f) => test(f, k))).length
  const pick = (k: Filter) => setFilter(filter === k ? 'all' : k)
  const catName = (code: string) => cats.find((c) => c.code === code)?.name ?? code

  // 종류 폴더: 기준 코드(FOLDER_ORDER 순서 + 번호) → 이 프로젝트에서 직접 만든 종류 → 종류 없음(있을 때만)
  const ordered = [...cats].sort((a, b) => {
    const ia = FOLDER_ORDER.indexOf(a.code), ib = FOLDER_ORDER.indexOf(b.code)
    return (ia < 0 ? 99 : ia) - (ib < 0 ? 99 : ib)
  })
  const folderDefs = [
    ...ordered.map((c, i) => ({ key: `c:${c.id}`, name: `${i + 1}. ${c.name}`, help: CATEGORY_HELP[c.code]?.what })),
    ...pcats.map((c) => ({ key: `p:${c.id}`, name: `${c.name} (이 프로젝트)`, help: undefined as string | undefined })),
    ...(all.some((f) => folderOf(f) === NONE) ? [{ key: NONE, name: '종류 없음', help: '종류가 정해지지 않은 자료 — 줄 끝 메뉴에서 종류를 정하세요' }] : []),
  ]
  const folderRows = folderDefs.map((d) => {
    const inIt = scoped.filter((f) => folderOf(f) === d.key)
    const last = inIt.reduce<ProjectFile | null>((m, f) => (!m || f.uploadedAt > m.uploadedAt ? f : m), null)
    return { ...d, count: toEntries(inIt).length, last }
  }).filter((r) => !hideEmpty || r.count > 0)
  const current = folder ? folderDefs.find((d) => d.key === folder) : undefined
  const inFolder = current ? scoped.filter((f) => folderOf(f) === current.key) : []
  const listed = (q ? scoped.filter(matches) : inFolder).sort((x, y) => y.uploadedAt.localeCompare(x.uploadedAt))

  /** 끌어다 놓기: 폴더 줄 위 → 그 종류, 폴더 안 → 그 종류, 빈 곳 → 자동 제안 */
  const dropProps = (target: string | null) => ({
    onDragOver: (e: DragEvent) => { if (partner) return; e.preventDefault(); e.stopPropagation(); setDrag(true); setOverFolder(target) },
    onDragLeave: (e: DragEvent) => { e.stopPropagation(); setOverFolder(null) },
    onDrop: (e: DragEvent) => {
      if (partner) return
      e.preventDefault(); e.stopPropagation(); setDrag(false); setOverFolder(null)
      void readDrop(e.dataTransfer).then((p) => addPicked(p, target))
    },
  })

  const waiting = pending.filter((r) => r.status !== 'done').length
  const waitingLabel = (() => {
    const left = pending.filter((r) => r.status !== 'done')
    const bundles = new Set(left.filter((r) => r.bundle).map((r) => r.bundle!.id)).size
    const loose = left.filter((r) => !r.bundle).length
    return [bundles ? `폴더 ${bundles}개` : '', loose ? `파일 ${loose}개` : ''].filter(Boolean).join(' · ')
  })()

  /** 종류 도움말: 칸마다 무엇을 넣는지 + 실제 예 */
  const helpBox = help && (
    <div className="docs-help">
      <div className="docs-help-head"><b>종류 도움말</b> — NAS 프로젝트 폴더 13칸 + 현장 백업. 맞는 칸이 없으면 '기타자료'나 이 프로젝트에만 쓰는 종류를 직접 넣으세요.
        <button className="text-button" onClick={() => setHelp(false)}>닫기</button></div>
      <dl>
        {ordered.filter((c) => CATEGORY_HELP[c.code]).map((c) => (
          <div key={c.id}><dt>{c.name}</dt><dd>{CATEGORY_HELP[c.code].what}<small>예: {CATEGORY_HELP[c.code].ex}</small></dd></div>
        ))}
      </dl>
    </div>
  )

  const pickers = (
    <>
      <input ref={inputRef} type="file" multiple hidden onChange={(e) => pickInput(e.target)} />
      <input ref={folderRef} type="file" multiple hidden {...{ webkitdirectory: '' }} onChange={(e) => pickInput(e.target)} />
    </>
  )

  const pendingBox = pending.length > 0 && (
    <section className="docs-pending">
      <div className="docs-pending-head">
        <b>올릴 자료 {waitingLabel || '0개'}</b>
        <span className={`docs-ai ${aiRun}`}>
          {!ai.data?.configured ? '종류: 규칙(사내 AI 연결 전)' : aiRun === 'running' ? '⟳ 사내 AI가 종류를 정하는 중…' : aiRun === 'error' ? `사내 AI 실패 — 규칙 결과로 둠(${aiError})` : '✦ 종류: 사내 AI'}
          {aiRun === 'error' && <button className="text-button" onClick={() => void runAi(pending.filter((r) => r.status !== 'done'))}>다시</button>}
        </span>
        <span className="docs-bulk">
          일괄 바꾸기
          <select aria-label="종류 일괄 바꾸기" value="" disabled={uploading} onChange={(e) => e.target.value && setAll({ categoryId: Number(e.target.value), projectCategoryId: null, reasons: ['직접 고름'], manual: true })}>
            <option value="">종류…</option>
            {ordered.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </span>
        <span className="seg" title="공유 범위(모두에 적용)">
          {(Object.keys(SHARE_LABEL) as Share[]).map((k) => (
            <button key={k} type="button" className={share === k ? 'on' : ''} disabled={uploading} onClick={() => setShare(k)}>{SHARE_LABEL[k]}</button>
          ))}
        </span>
        <input className="search-input" placeholder="설명(선택, 모두에 붙음)" value={description} disabled={uploading} onChange={(e) => setDescription(e.target.value)} />
        <button className="primary" disabled={uploading || waiting === 0 || aiRun === 'running'} onClick={() => void uploadAll()}>{uploading ? '올리는 중…' : `${waitingLabel} 올리기`}</button>
        <button className="secondary" disabled={uploading} onClick={() => setPending([])}>취소</button>
        <button className="text-button" onClick={() => setHelp(!help)}>ⓘ 종류 도움말</button>
      </div>
      <ul className="docs-pending-list">
        {[...new Map(pending.filter((r) => r.bundle).map((r) => [r.bundle!.id, r.bundle!])).values()].map((b) => {
          const rows = pending.filter((r) => r.bundle?.id === b.id)
          const r = rows[0]
          const done = rows.filter((x) => x.status === 'done').length
          const failed = rows.filter((x) => x.status === 'err').length
          const setB = (patch: Partial<PendingRow>) => setPending((cur) => cur.map((x) => (x.bundle?.id === b.id && x.status !== 'done' ? { ...x, ...patch } : x)))
          return (
            <li key={b.id} className={`docs-bundle-pending st-${done === rows.length ? 'done' : failed ? 'err' : 'wait'}`}>
              <FolderIcon />
              <div className="docs-pending-name">
                <span title={b.name}><b>{b.name}</b> — 폴더째 · 파일 {rows.length}개</span>
                <small>
                  {size(rows.reduce((t, x) => t + x.file.size, 0))}
                  {r.reasons.length > 0 ? <> · 제안 근거: {r.reasons.join(' · ')}</> : <em> · 근거 없음 — 종류를 확인하세요</em>}
                  {r.aiConfidence != null && r.aiConfidence < 0.6 && !r.manual && <em> · AI 확신 낮음 — 확인하세요</em>}
                  {r.categoryId != null && CATEGORY_HELP[catCode(r.categoryId) ?? ''] && <span className="docs-cat-help"> · {catName(catCode(r.categoryId)!)}: {CATEGORY_HELP[catCode(r.categoryId)!].what}</span>}
                  {uploading || done ? ` · ${done}/${rows.length} 올림` : ''}{failed ? <em> · {failed}개 실패</em> : null}
                </small>
              </div>
              <select aria-label="종류(묶음)" value={catValue(r.categoryId, r.projectCategoryId)} disabled={uploading}
                onChange={(e) => e.target.value !== '__new' && setB({ ...parseCat(e.target.value), reasons: ['직접 고름'], manual: true })}>
                {ordered.map((c) => <option key={c.id} value={`c:${c.id}`}>{c.name}</option>)}
                {pcats.map((c) => <option key={`p${c.id}`} value={`p:${c.id}`}>{c.name} (이 프로젝트)</option>)}
              </select>
              <span className="docs-pending-st">{done === rows.length ? '완료' : uploading ? '올리는 중' : ''}</span>
              <button className="text-button" aria-label="목록에서 빼기" disabled={uploading} onClick={() => setPending((cur) => cur.filter((x) => x.bundle?.id !== b.id))}>✕</button>
            </li>
          )
        })}
        {pending.filter((r) => !r.bundle).map((r) => (
          <li key={r.key} className={`st-${r.status}`}>
            <FileIcon name={r.file.name} />
            <div className="docs-pending-name">
              <span title={r.path}>{r.path}</span>
              <small>
                {size(r.file.size)}
                {r.reasons.length > 0 ? <> · 제안 근거: {r.reasons.join(' · ')}</> : <em> · 근거 없음 — 종류를 확인하세요</em>}
                {r.aiConfidence != null && r.aiConfidence < 0.6 && !r.manual && <em> · AI 확신 낮음 — 확인하세요</em>}
                {r.categoryId != null && CATEGORY_HELP[catCode(r.categoryId) ?? ''] && <span className="docs-cat-help"> · {catName(catCode(r.categoryId)!)}: {CATEGORY_HELP[catCode(r.categoryId)!].what}</span>}
                {r.dupe && <em> · 같은 이름이 이미 있음(새로 올리면 ' (2)'로 저장)</em>}
                {r.error && <em> · {r.error}</em>}
              </small>
            </div>
            {newCatFor === r.key ? (
              <span className="docs-newcat">
                <input autoFocus value={newCatName} maxLength={50} placeholder="새 종류 (이 프로젝트만)" onChange={(e) => setNewCatName(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter' && !e.nativeEvent.isComposing && newCatName.trim()) addCat.mutate(newCatName.trim())
                    if (e.key === 'Escape') setNewCatFor(null)
                  }} />
                <button className="text-button" disabled={!newCatName.trim() || addCat.isPending} onClick={() => addCat.mutate(newCatName.trim())}>추가</button>
                <button className="text-button" onClick={() => setNewCatFor(null)}>취소</button>
              </span>
            ) : (
              <select aria-label="종류" value={catValue(r.categoryId, r.projectCategoryId)} disabled={uploading || r.status === 'done'}
                className={r.reasons.length ? '' : 'unsure'}
                onChange={(e) => e.target.value === '__new'
                  ? (setNewCatFor(r.key), setNewCatName(''))
                  : setRow(r.key, { ...parseCat(e.target.value), reasons: ['직접 고름'], manual: true })}>
                {ordered.map((c) => <option key={c.id} value={`c:${c.id}`}>{c.name}</option>)}
                {pcats.map((c) => <option key={`p${c.id}`} value={`p:${c.id}`}>{c.name} (이 프로젝트)</option>)}
                <option value="__new">＋ 직접 입력 (이 프로젝트만)…</option>
              </select>
            )}
            <span className="docs-pending-st">{{ wait: '', up: '올리는 중', done: '완료', err: '실패' }[r.status]}</span>
            <button className="text-button" aria-label="목록에서 빼기" disabled={uploading} onClick={() => setPending((cur) => cur.filter((x) => x.key !== r.key))}>✕</button>
          </li>
        ))}
      </ul>
    </section>
  )

  // 사양서 분석 탭: 한 종류만 보고 바로 올리는 예전 모양
  if (onlyCategory) {
    return (
      <div className="files-tab docs-box">
        {!partner && (
          <div className={drag ? 'dropzone over' : 'dropzone'} {...dropProps(null)} onClick={() => inputRef.current?.click()} role="button" tabIndex={0}>
            {pickers}
            <strong>{quick.isPending ? '업로드 중…' : '파일을 여기로 끌어다 놓거나 클릭해 선택'}</strong>
            <span>여러 개 한 번에 올릴 수 있습니다 · 올린 파일은 '프로젝트 자료함'에 종류 '{cats.find((c) => c.code === onlyCategory)?.name ?? onlyCategory}'로 함께 들어갑니다</span>
          </div>
        )}
        {quick.error && <ErrorBox error={quick.error} />}
        <FileRows files={all} projectId={projectId} rowActions={rowActions} emptyText="업로드된 문서가 없습니다." />
      </div>
    )
  }

  const catOptions = (f: ProjectFile) => (
    <>
      {f.categoryId == null && f.projectCategoryId == null && <option value="">종류 없음</option>}
      {ordered.map((c) => <option key={c.id} value={`c:${c.id}`}>{c.name}</option>)}
      {pcats.map((c) => <option key={`p${c.id}`} value={`p:${c.id}`}>{c.name} (이 프로젝트)</option>)}
    </>
  )

  return (
    <div className={`files-tab dx${drag ? ' dragging' : ''}`} {...dropProps(folder)}>
      {pickers}
      <div className="dx-body">
        <nav className="dx-side" aria-label="보기 범위(하나만 선택)">
          <small>보기 범위</small>
          {([['all', '전체 자료'], ['recent', '최근 7일'], ['mine', '내가 올린 자료'], ['share:dept', '우리 부서 공유'], ['share:project', '전체 공유']] as const).map(([k, label]) => (
            <button key={k} className={filter === k ? 'active' : ''} onClick={() => (k === 'all' ? setFilter('all') : pick(k))}>
              <span>{label}</span><b>{n(k)}</b>
            </button>
          ))}
        </nav>

        <div className="dx-main">
          <div className="dx-toolbar">
            <label className="dx-search">
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" aria-hidden="true"><path d="M11 18a7 7 0 1 0 0-14 7 7 0 0 0 0 14zM21 21l-5-5" /></svg>
              <input aria-label="자료 검색" placeholder="파일 이름·종류·올린 사람 검색" value={query} onChange={(e) => setQuery(e.target.value)} />
            </label>
            <button className="dx-btn" onClick={() => setHelp(!help)}>ⓘ 종류 도움말</button>
            {!partner && <button className="dx-btn" onClick={() => folderRef.current?.click()}>폴더 올리기</button>}
            {!partner && (
              <button className="dx-btn primary" onClick={() => inputRef.current?.click()}>
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M12 16V4M7 9l5-5 5 5M4 16v4h16v-4" /></svg>
                올리기
              </button>
            )}
          </div>
          {helpBox}
          {pendingBox}

          {q ? (
            <>
              <div className="dx-path">
                <span><b>검색</b> '{query.trim()}' · <b>{toEntries(listed).length}</b>건</span>
                <button className="text-button" onClick={() => setQuery('')}>검색 지우기</button>
              </div>
              <FileTable files={listed} projectId={projectId} showKind rowActions={rowActions} preview={preview} onPreview={(id) => setPreview(preview === id ? null : id)}
                catOptions={catOptions} onRecat={(f, v) => recategorize.mutate({ id: f.id, ...parseCat(v) })} onReshare={(f, s) => reshare.mutate({ id: f.id, share: s })}
                onBundle={(bid, what, json) => bundleOp.mutate({ bid, what, json })} onDelete={(f) => confirm(`"${f.fileName}"을(를) 삭제할까요?`) && del.mutate(f.id)} onDeleteBundle={(bid) => delBundle.mutate(bid)}
                editable={!partner} emptyText="검색 결과가 없습니다." />
            </>
          ) : current ? (
            <>
              <div className="dx-path">
                <nav aria-label="자료함 경로">
                  <button className="text-button" onClick={() => setFolder(null)}>자료함</button>
                  <span className="sep">›</span>
                  <FolderIcon size={14} />
                  <b>{current.name}</b>
                  <span><b className="num">{toEntries(inFolder).length}</b>건</span>
                </nav>
                {!partner && <span>끌어다 놓으면 <b>{current.name.replace(/^\d+\. /, '')}</b>(으)로 올라갑니다</span>}
              </div>
              <FileTable files={listed} projectId={projectId} rowActions={rowActions} preview={preview} onPreview={(id) => setPreview(preview === id ? null : id)}
                catOptions={catOptions} onRecat={(f, v) => recategorize.mutate({ id: f.id, ...parseCat(v) })} onReshare={(f, s) => reshare.mutate({ id: f.id, share: s })}
                onBundle={(bid, what, json) => bundleOp.mutate({ bid, what, json })} onDelete={(f) => confirm(`"${f.fileName}"을(를) 삭제할까요?`) && del.mutate(f.id)} onDeleteBundle={(bid) => delBundle.mutate(bid)}
                editable={!partner} emptyText={partner ? '이 폴더에 자료가 없습니다.' : '이 폴더에 자료가 없습니다. 파일을 여기로 끌어다 놓으세요.'} />
            </>
          ) : (
            <>
              <div className="dx-path">
                <span><b className="title">자료함</b> 종류 폴더 <b className="num">{folderDefs.length}</b> · 자료 <b className="num">{toEntries(scoped).length}</b></span>
                <label className="dx-check"><input type="checkbox" checked={hideEmpty} onChange={(e) => setHideEmpty(e.target.checked)} /> 빈 폴더 숨기기</label>
              </div>
              {!partner && <p className="dx-hint">파일을 <b>폴더 위로</b> 끌어다 놓으면 그 종류로, <b>빈 곳</b>에 놓으면 이름·확장자를 보고 알맞은 폴더를 먼저 제안합니다.</p>}
              <div className="dx-table">
                <div className="dx-row dx-head dx-folders"><span /><span>종류</span><span className="r">자료 수</span><span className="r">최근 올린 날</span><span>최근 올린 사람</span><span /></div>
                {folderRows.map((r) => (
                  <button key={r.key} className={`dx-row dx-folders${r.count ? '' : ' is-empty'}${overFolder === r.key ? ' over' : ''}`} title={r.help}
                    onClick={() => setFolder(r.key)} {...dropProps(r.key)}>
                    <FolderIcon empty={!r.count} />
                    <span className="name">{r.name}</span>
                    <span className="r num">{r.count ? `${r.count}건` : '비어 있음'}</span>
                    <span className="r num muted">{r.last ? md(r.last.uploadedAt) : ''}</span>
                    <span className="muted ell">{r.last ? [r.last.department, r.last.uploadedByName].filter(Boolean).join(' ') : ''}</span>
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="#98A2B3" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M9 6l6 6-6 6" /></svg>
                  </button>
                ))}
              </div>
            </>
          )}
        </div>
      </div>
      {(recategorize.error || reshare.error || addCat.error || bundleOp.error || del.error || delBundle.error) && <ErrorBox error={recategorize.error ?? reshare.error ?? addCat.error ?? bundleOp.error ?? del.error ?? delBundle.error} />}
    </div>
  )
}

/** 폴더 안 자료 표(촘촘한 줄). 폴더째 올린 묶음은 폴더 아이콘 줄로, 누르면 안 파일이 펼쳐진다. 종류·공유 바꾸기·삭제는 줄 끝 ⋮ 메뉴 */
function FileTable({ files, projectId, showKind, rowActions, preview, onPreview, catOptions, onRecat, onReshare, onBundle, onDelete, onDeleteBundle, editable, emptyText }: {
  files: ProjectFile[]
  projectId: number
  /** 검색 결과처럼 여러 폴더가 섞일 때 종류 열을 보인다 */
  showKind?: boolean
  rowActions?: (f: ProjectFile) => ReactNode
  preview: number | null
  onPreview: (id: number) => void
  catOptions: (f: ProjectFile) => ReactNode
  onRecat: (f: ProjectFile, value: string) => void
  onReshare: (f: ProjectFile, share: Share) => void
  onBundle: (bundleId: string, what: 'category' | 'stage' | 'share', json: unknown) => void
  onDelete: (f: ProjectFile) => void
  onDeleteBundle: (bundleId: string) => void
  editable: boolean
  emptyText: string
}) {
  const [open, setOpen] = useState<string | null>(null) // 펼친 묶음
  const entries = toEntries(files)
  if (entries.length === 0) return <div className="dx-empty">{emptyText}</div>
  const cls = `dx-row dx-files${showKind ? ' kind' : ''}`
  const download = (f: ProjectFile) => downloadFile(`/api/projects/${projectId}/files/${f.id}/download`, f.fileName)
  const shareCell = (s: Share) => <span className={`dx-share ${s}`}><i />{SHARE_LABEL[s]}</span>
  return (
    <div className="dx-table">
      <div className={`${cls} dx-head`}>
        <span /><span>이름</span>{showKind && <span>종류</span>}<span>공유</span><span className="r">크기</span><span className="r">올린 날</span><span />
      </div>
      {entries.map((en) => {
        if (en.kind === 'bundle') {
          const f0 = en.files[0]
          const total = en.files.reduce((t, f) => t + f.sizeBytes, 0)
          const dirs = [...new Set(en.files.map((f) => f.relPath ?? en.name))].sort()
          const isOpen = open === en.id
          return (
            <Fragment key={en.id}>
              <div className={cls} title={[f0.department, f0.uploadedByName].filter(Boolean).join(' ')}>
                <FolderIcon />
                <button className="dx-name strong" onClick={() => setOpen(isOpen ? null : en.id)} aria-expanded={isOpen}>
                  {en.name} <span className="muted">· 파일 {en.files.length} · 폴더 {dirs.length}</span> <span className="caret">{isOpen ? '▴' : '▾'}</span>
                </button>
                {showKind && <span className="muted ell">{f0.category ?? '종류 없음'}</span>}
                {shareCell(f0.share)}
                <span className="r num muted">{size(total)}</span>
                <span className="r num muted">{md(f0.uploadedAt)}</span>
                <RowMenu label={`${en.name} 더보기`}>
                  <button onClick={() => setOpen(isOpen ? null : en.id)}>{isOpen ? '접기' : '안 파일 보기'}</button>
                  <button onClick={() => downloadFile(`/api/projects/${projectId}/files/bundles/${en.id}/zip`, `${en.name}.zip`)}>ZIP 다운로드</button>
                  {editable && (
                    <label>종류 바꾸기(폴더째)
                      <select value={catValue(f0.categoryId, f0.projectCategoryId)} onChange={(e) => onBundle(en.id, 'category', parseCat(e.target.value))}>{catOptions(f0)}</select>
                    </label>
                  )}
                  {editable && f0.mine && (
                    <label>공유 범위
                      <select value={f0.share} onChange={(e) => onBundle(en.id, 'share', { share: e.target.value })}>
                        {(Object.keys(SHARE_LABEL) as Share[]).map((k) => <option key={k} value={k}>{SHARE_LABEL[k]}</option>)}
                      </select>
                    </label>
                  )}
                  {editable && <button className="danger" onClick={() => confirm(`폴더 "${en.name}"(파일 ${en.files.length}개)을 삭제할까요?`) && onDeleteBundle(en.id)}>삭제</button>}
                </RowMenu>
              </div>
              {isOpen && (
                <div className="dx-tree">
                  {dirs.map((d) => (
                    <div key={d} className="fb-dir">
                      <div className="fb-dir-name"><FolderIcon size={12} /> {d}</div>
                      {en.files.filter((f) => (f.relPath ?? en.name) === d).map((f) => (
                        <Fragment key={f.id}>
                          <div className="fb-file">
                            <FileIcon name={f.fileName} />
                            <span className="fb-name" title={f.fileName}>{f.fileName}</span>
                            <small className="muted">{size(f.sizeBytes)}</small>
                            <button className="text-button" onClick={() => onPreview(f.id)}>{preview === f.id ? '닫기' : '미리 보기'}</button>
                            <button className="text-button" onClick={() => download(f)}>다운로드</button>
                          </div>
                          {preview === f.id && <div className="fb-preview"><FilePreview projectId={projectId} file={f} /></div>}
                        </Fragment>
                      ))}
                    </div>
                  ))}
                </div>
              )}
            </Fragment>
          )
        }
        const f = en.file
        return (
          <Fragment key={f.id}>
            <div className={cls} title={[f.department, f.uploadedByName, f.description].filter(Boolean).join(' · ')}>
              <FileIcon name={f.fileName} />
              <button className="dx-name" onClick={() => onPreview(f.id)}>{f.fileName}</button>
              {showKind && <span className="muted ell">{f.category ?? '종류 없음'}</span>}
              {shareCell(f.share)}
              <span className="r num muted">{size(f.sizeBytes)}</span>
              <span className="r num muted">{md(f.uploadedAt)}</span>
              <RowMenu label={`${f.fileName} 더보기`}>
                {rowActions?.(f)}
                <button onClick={() => onPreview(f.id)}>{preview === f.id ? '미리 보기 닫기' : '미리 보기'}</button>
                <button onClick={() => download(f)}>다운로드</button>
                {editable && (
                  <label>종류 바꾸기
                    <select value={catValue(f.categoryId, f.projectCategoryId)} onChange={(e) => onRecat(f, e.target.value)}>{catOptions(f)}</select>
                  </label>
                )}
                {editable && f.mine && (
                  <label>공유 범위
                    <select value={f.share} onChange={(e) => onReshare(f, e.target.value as Share)}>
                      {(Object.keys(SHARE_LABEL) as Share[]).map((k) => <option key={k} value={k}>{SHARE_LABEL[k]}</option>)}
                    </select>
                  </label>
                )}
                {editable && <button className="danger" onClick={() => onDelete(f)}>삭제</button>}
              </RowMenu>
            </div>
            {preview === f.id && <div className="dx-preview"><FilePreview projectId={projectId} file={f} /></div>}
          </Fragment>
        )
      })}
    </div>
  )
}

/** 줄 끝 ⋮ 메뉴. 바깥을 누르면 닫힌다 */
function RowMenu({ label, children }: { label: string; children: ReactNode }) {
  const ref = useRef<HTMLDetailsElement>(null)
  return (
    <details ref={ref} className="dx-menu" onBlur={(e) => { if (!e.currentTarget.contains(e.relatedTarget as Node)) ref.current?.removeAttribute('open') }}>
      <summary aria-label={label}>
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" aria-hidden="true"><path d="M12 6h.01M12 12h.01M12 18h.01" /></svg>
      </summary>
      <div className="dx-menu-pop" onClick={(e) => { if ((e.target as HTMLElement).tagName === 'BUTTON') ref.current?.removeAttribute('open') }}>{children}</div>
    </details>
  )
}

/** 자료 목록 한 덩어리. editors가 없으면 종류를 이름표로만 보인다(한 종류만 보는 화면). */
function FileRows({ files, projectId, rowActions, emptyText, editors, onDelete, deleting, preview, onPreview, bundleEditors }: {
  files: ProjectFile[]
  /** 폴더 묶음 줄의 종류·단계·공유 칸(묶음째 바꿈) */
  bundleEditors?: (first: ProjectFile, bundleId: string) => ReactNode
  /** 미리 보기를 연 자료 id */
  preview?: number | null
  onPreview?: (id: number) => void
  projectId: number
  rowActions?: (f: ProjectFile) => ReactNode
  emptyText: string
  editors?: (f: ProjectFile) => ReactNode
  onDelete?: (f: ProjectFile) => void
  deleting?: boolean
}) {
  const qc = useQueryClient()
  const partner = useIsPartner()
  const del = useMutation({
    mutationFn: (id: number) => api(`/api/projects/${projectId}/files/${id}`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['project-files', projectId] }),
  })
  if (files.length === 0) return <div className="empty" style={{ padding: 36 }}>{emptyText}</div>
  const download = (f: ProjectFile) => downloadFile(`/api/projects/${projectId}/files/${f.id}/download`, f.fileName)
  const remove = onDelete ?? ((f: ProjectFile) => confirm(`"${f.fileName}"을(를) 삭제할까요?`) && del.mutate(f.id))
  return (
    <ul className="file-list">
      {toEntries(files).map((en) => en.kind === 'bundle'
        ? <BundleRow key={en.id} projectId={projectId} id={en.id} name={en.name} files={en.files} editors={bundleEditors} preview={preview} onPreview={onPreview} />
        : ((f) => (
        <Fragment key={f.id}>
        <li>
          <span className="file-ext">{extOf(f.fileName)}</span>
          <div className="file-main">
            <button className="row-title" onClick={() => download(f)}>{f.fileName}</button>
            <small>
              {editors ? editors(f) : <span className="badge">{f.category ?? '-'}</span>}
              {' '}{!editors && f.stage ? `${f.stage} · ` : ''}{size(f.sizeBytes)}
              {f.uploadedByName ? ` · ${[f.department, f.uploadedByName].filter(Boolean).join(' ')}` : ''} · {date(f.uploadedAt)}
              {f.description ? ` · ${f.description}` : ''}
            </small>
          </div>
          {rowActions?.(f)}
          {onPreview && <button className="secondary" onClick={() => onPreview(f.id)}>{preview === f.id ? '미리 보기 닫기' : '미리 보기'}</button>}
          <button className="secondary" onClick={() => download(f)}>다운로드</button>
          {!partner && <button className="text-button danger" onClick={() => remove(f)} disabled={deleting ?? del.isPending}>삭제</button>}
        </li>
        {preview === f.id && <li className="file-preview-row"><FilePreview projectId={projectId} file={f} /></li>}
        </Fragment>
      ))(en.file))}
    </ul>
  )
}

/** 폴더 묶음 한 줄: 📁 이름 · 파일 수 · 크기. 펼치면 폴더별로 안 파일(미리 보기·다운로드), ZIP으로 통째 내려받기 */
function BundleRow({ projectId, id, name, files, editors, preview, onPreview }: {
  projectId: number
  id: string
  name: string
  files: ProjectFile[]
  editors?: (first: ProjectFile, bundleId: string) => ReactNode
  preview?: number | null
  onPreview?: (id: number) => void
}) {
  const qc = useQueryClient()
  const partner = useIsPartner()
  const [open, setOpen] = useState(false)
  const del = useMutation({
    mutationFn: () => api(`/api/projects/${projectId}/files/bundles/${id}`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['project-files', projectId] }),
  })
  const f0 = files[0]
  const total = files.reduce((t, f) => t + f.sizeBytes, 0)
  // 폴더별로 모아 경로 순서대로(맨 위 폴더 이름은 빼고 보인다)
  const dirs = [...new Set(files.map((f) => f.relPath ?? name))].sort()
  return (
    <>
      <li className="file-bundle">
        <span className="file-ext">📁</span>
        <div className="file-main">
          <button className="row-title" onClick={() => setOpen(!open)}>{name} <span className="muted">— 파일 {files.length}개 · 폴더 {dirs.length}개</span> {open ? '▴' : '▾'}</button>
          <small>
            {editors ? editors(f0, id) : <span className="badge">{f0.category ?? '-'}</span>}
            {' '}{!editors && f0.stage ? `${f0.stage} · ` : ''}{size(total)}
            {f0.uploadedByName ? ` · ${[f0.department, f0.uploadedByName].filter(Boolean).join(' ')}` : ''} · {date(f0.uploadedAt)}
          </small>
        </div>
        <button className="secondary" onClick={() => setOpen(!open)}>{open ? '접기' : '펼치기'}</button>
        <button className="secondary" onClick={() => downloadFile(`/api/projects/${projectId}/files/bundles/${id}/zip`, `${name}.zip`)}>ZIP 다운로드</button>
        {!partner && <button className="text-button danger" disabled={del.isPending} onClick={() => confirm(`폴더 "${name}"(파일 ${files.length}개)을 삭제할까요?`) && del.mutate()}>삭제</button>}
      </li>
      {open && (
        <li className="file-bundle-tree">
          {dirs.map((d) => (
            <div key={d} className="fb-dir">
              <div className="fb-dir-name">📂 {d}</div>
              {files.filter((f) => (f.relPath ?? name) === d).map((f) => (
                <Fragment key={f.id}>
                  <div className="fb-file">
                    <span className="file-ext sm">{extOf(f.fileName)}</span>
                    <span className="fb-name" title={f.fileName}>{f.fileName}</span>
                    <small className="muted">{size(f.sizeBytes)}</small>
                    {onPreview && <button className="text-button" onClick={() => onPreview(f.id)}>{preview === f.id ? '닫기' : '미리 보기'}</button>}
                    <button className="text-button" onClick={() => downloadFile(`/api/projects/${projectId}/files/${f.id}/download`, f.fileName)}>다운로드</button>
                  </div>
                  {preview === f.id && <div className="fb-preview"><FilePreview projectId={projectId} file={f} /></div>}
                </Fragment>
              ))}
            </div>
          ))}
        </li>
      )}
    </>
  )
}
