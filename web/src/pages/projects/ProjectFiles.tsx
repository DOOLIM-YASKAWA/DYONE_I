import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { type ReactNode, useMemo, useRef, useState } from 'react'
import { api, downloadFile } from '../../api/client'
import { useIsPartner } from '../../auth/AuthContext'
import type { Code, ProjectFile } from '../../api/types'
import { ErrorBox, Loading } from '../../components/ui'

const date = (v?: string) => (v ? new Date(v).toLocaleDateString('ko-KR') : '-')
const size = (n: number) => (n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(0)} KB` : `${(n / 1048576).toFixed(1)} MB`)

/**
 * 단계(stage 코드) → 관련 문서 분류(category 코드). 단계가 기록되지 않은(stage_id 없음) 예전 파일을
 * 단계 화면에 보여줄 때만 쓰는 보조 매핑. 새로 올리는 파일은 stage_id가 직접 기록된다.
 * 분류/단계 코드가 바뀌면 여기만 고치면 된다.
 */
const STAGE_CATEGORIES: Record<string, string[]> = {
  opportunity: ['spec', 'quote'], // 영업(사양 검토·견적 포함)
  design: ['design'],
  modula: ['modula'],
  line: ['line'],
}

/**
 * 프로젝트별 문서함. 상단에서 분류를 고르고 드래그&드롭(또는 선택)으로 업로드,
 * 아래 분류 칩으로 걸러 본다.
 * - stage: 업로드 시 이 단계로 기록(stage_id) + 분류 기본값을 이 단계와 같은 코드로.
 * - filterByStage: true면 이 단계 관련 문서만 보여준다(단계 상세 화면용).
 */
export function ProjectFilesTab({ projectId, stage, filterByStage = false, onlyCategory, rowActions }: {
  projectId: number
  stage?: Code
  filterByStage?: boolean
  /** 이 분류(코드)의 문서만 보이고 올린다 */
  onlyCategory?: string
  /** 행마다 다운로드 앞에 붙일 버튼(예: 사양서 분석 탭의 '사양서 분석') */
  rowActions?: (f: ProjectFile) => ReactNode
}) {
  const qc = useQueryClient()
  const partner = useIsPartner() // 협력사: 목록·내려받기만(올리기는 협력사 일지 단계에서)
  const inputRef = useRef<HTMLInputElement>(null)
  const [filter, setFilter] = useState<number | 'all'>('all')
  const [uploadCategory, setUploadCategory] = useState<number | null>(null)
  const [description, setDescription] = useState('')
  const [drag, setDrag] = useState(false)

  const categories = useQuery({ queryKey: ['codes', 'document_category'], queryFn: () => api<Code[]>('/api/codes/document_category') })
  const files = useQuery({ queryKey: ['project-files', projectId], queryFn: () => api<ProjectFile[]>(`/api/projects/${projectId}/files`) })

  // 업로드 분류 기본값: 단계와 같은 코드의 분류 → 없으면 첫 분류
  const defaultCategory = useMemo(() => {
    const cats = categories.data ?? []
    return cats.find((c) => c.code === (onlyCategory ?? stage?.code))?.id ?? cats[0]?.id ?? null
  }, [categories.data, stage?.code, onlyCategory])
  const selectedCategory = uploadCategory ?? defaultCategory

  const upload = useMutation({
    mutationFn: async (picked: FileList | File[]) => {
      const fd = new FormData()
      Array.from(picked).forEach((f) => fd.append('files', f))
      if (selectedCategory != null) fd.append('categoryId', String(selectedCategory))
      if (stage) fd.append('stageId', String(stage.id))
      if (description.trim()) fd.append('description', description.trim())
      return api(`/api/projects/${projectId}/files`, { method: 'POST', body: fd })
    },
    onSuccess: () => {
      setDescription('')
      qc.invalidateQueries({ queryKey: ['project-files', projectId] })
    },
  })

  const del = useMutation({
    mutationFn: (id: number) => api(`/api/projects/${projectId}/files/${id}`, { method: 'DELETE' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['project-files', projectId] }),
  })

  // 업로드 후 분류 변경
  const recategorize = useMutation({
    mutationFn: ({ id, categoryId }: { id: number; categoryId: number }) =>
      api(`/api/projects/${projectId}/files/${id}`, { method: 'PUT', json: { categoryId } }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['project-files', projectId] }),
  })

  const pick = (list: FileList | null) => {
    // FileList는 입력창에 연결된 라이브 객체라, 아래에서 input.value를 비우면 같이 비워진다.
    // 그러면 비동기 mutate가 FormData를 만들 때 파일이 0개가 되므로, 먼저 배열로 스냅샷해 넘긴다.
    if (list && list.length) upload.mutate(Array.from(list))
    if (inputRef.current) inputRef.current.value = ''
  }

  if (files.isLoading || categories.isLoading) return <Loading />
  if (files.error) return <ErrorBox error={files.error} />

  // 단계 화면이면 이 단계 문서만: stage_id 일치 + (stage_id 없는 예전 파일은 분류 매핑으로)
  const inStage = (f: ProjectFile) =>
    !!stage && (f.stageId === stage.id || (f.stageId == null && (STAGE_CATEGORIES[stage.code] ?? []).includes(f.categoryCode ?? '')))
  const all = (files.data ?? []).filter((f) => (onlyCategory ? f.categoryCode === onlyCategory : !filterByStage || inStage(f)))
  const counts = (id: number) => all.filter((f) => f.categoryId === id).length
  const shown = filter === 'all' ? all : all.filter((f) => f.categoryId === filter)

  return (
    <div className="files-tab">
      {/* 업로드 영역 */}
      <div
        className={drag ? 'dropzone over' : 'dropzone'}
        hidden={partner}
        onDragOver={(e) => { e.preventDefault(); setDrag(true) }}
        onDragLeave={() => setDrag(false)}
        onDrop={(e) => { e.preventDefault(); setDrag(false); pick(e.dataTransfer.files) }}
        onClick={() => inputRef.current?.click()}
        role="button"
        tabIndex={0}
      >
        <input ref={inputRef} type="file" multiple hidden onChange={(e) => pick(e.target.files)} />
        <strong>
          {upload.isPending ? '업로드 중…' : filterByStage && stage ? `'${stage.name}' 단계 문서를 끌어다 놓거나 클릭해 선택` : '파일을 여기로 끌어다 놓거나 클릭해 선택'}
        </strong>
        <span>
          {onlyCategory ? '여러 개 한 번에 올릴 수 있습니다' : '여러 개 한 번에 올릴 수 있습니다 · 아래에서 분류를 먼저 고르세요'}
          {stage ? ` · '${stage.name}' 단계로 기록됩니다` : ''}
        </span>
      </div>
      {!onlyCategory && !partner && (
      <div className="upload-opts">
        <label>
          분류
          <select value={selectedCategory ?? ''} onChange={(e) => setUploadCategory(Number(e.target.value))}>
            {categories.data?.map((c) => (
              <option key={c.id} value={c.id}>{c.name}</option>
            ))}
          </select>
        </label>
        <input className="search-input" placeholder="설명(선택)" value={description} onChange={(e) => setDescription(e.target.value)} />
      </div>
      )}
      {upload.error && <ErrorBox error={upload.error} />}

      {/* 분류 필터(한 분류만 보는 화면에선 숨김) */}
      {!onlyCategory && (
      <div className="customer-chips file-chips">
        <button className={filter === 'all' ? 'chip active' : 'chip'} onClick={() => setFilter('all')}>전체 {all.length}</button>
        {categories.data
          ?.filter((c) => !filterByStage || counts(c.id) > 0) // 단계 화면에선 해당 문서가 있는 분류만
          .map((c) => (
            <button key={c.id} className={filter === c.id ? 'chip active' : 'chip'} onClick={() => setFilter(c.id)}>
              {c.name} {counts(c.id)}
            </button>
          ))}
      </div>
      )}

      {/* 목록 */}
      {shown.length === 0 ? (
        <div className="empty" style={{ padding: 36 }}>{filterByStage ? '이 단계에 올라온 문서가 없습니다.' : '업로드된 문서가 없습니다.'}</div>
      ) : (
        <ul className="file-list">
          {shown.map((f) => (
            <li key={f.id}>
              <span className="file-ext">{(f.fileName.split('.').pop() ?? '').slice(0, 4).toUpperCase() || 'FILE'}</span>
              <div className="file-main">
                <button className="row-title" onClick={() => downloadFile(`/api/projects/${projectId}/files/${f.id}/download`, f.fileName)}>
                  {f.fileName}
                </button>
                <small>
                  {/* 한 분류만 보는 화면(예: 사양서 분석 탭)은 분류가 고정이라 바꾸는 칸 대신 이름만 */}
                  {onlyCategory || partner ? (
                    <span className="badge">{f.category ?? '-'}</span>
                  ) : (
                    <select
                      className="file-cat"
                      value={f.categoryId ?? ''}
                      disabled={recategorize.isPending}
                      onChange={(e) => recategorize.mutate({ id: f.id, categoryId: Number(e.target.value) })}
                      aria-label="분류 변경"
                    >
                      {categories.data?.map((c) => (
                        <option key={c.id} value={c.id}>{c.name}</option>
                      ))}
                    </select>
                  )}
                  {' '}{!filterByStage && f.stage ? `${f.stage} · ` : ''}{size(f.sizeBytes)}
                  {f.uploadedByName ? ` · ${f.uploadedByName}` : ''} · {date(f.uploadedAt)}
                  {f.description ? ` · ${f.description}` : ''}
                </small>
              </div>
              {rowActions?.(f)}
              <button className="secondary" onClick={() => downloadFile(`/api/projects/${projectId}/files/${f.id}/download`, f.fileName)}>다운로드</button>
              {!partner && <button className="text-button danger" onClick={() => confirm(`"${f.fileName}"을(를) 삭제할까요?`) && del.mutate(f.id)} disabled={del.isPending}>삭제</button>}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
