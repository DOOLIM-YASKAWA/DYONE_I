import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useMemo, useRef, useState } from 'react'
import { api, downloadFile } from '../../api/client'
import type { Code, ProjectFile } from '../../api/types'
import { ErrorBox, Loading } from '../../components/ui'

const date = (v?: string) => (v ? new Date(v).toLocaleDateString('ko-KR') : '-')
const size = (n: number) => (n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(0)} KB` : `${(n / 1048576).toFixed(1)} MB`)

/**
 * 프로젝트별 문서함. 상단에서 분류를 고르고 드래그&드롭(또는 선택)으로 업로드,
 * 아래 분류 칩으로 걸러 본다. 분류 기본값은 현재 진행 단계와 같은 분류(있으면).
 */
export function ProjectFilesTab({ projectId, currentStageCode }: { projectId: number; currentStageCode?: string }) {
  const qc = useQueryClient()
  const inputRef = useRef<HTMLInputElement>(null)
  const [filter, setFilter] = useState<number | 'all'>('all')
  const [uploadCategory, setUploadCategory] = useState<number | null>(null)
  const [description, setDescription] = useState('')
  const [drag, setDrag] = useState(false)

  const categories = useQuery({ queryKey: ['codes', 'document_category'], queryFn: () => api<Code[]>('/api/codes/document_category') })
  const files = useQuery({ queryKey: ['project-files', projectId], queryFn: () => api<ProjectFile[]>(`/api/projects/${projectId}/files`) })

  // 업로드 분류 기본값: 현재 단계와 같은 코드의 분류 → 없으면 첫 분류
  const defaultCategory = useMemo(() => {
    const cats = categories.data ?? []
    return cats.find((c) => c.code === currentStageCode)?.id ?? cats[0]?.id ?? null
  }, [categories.data, currentStageCode])
  const selectedCategory = uploadCategory ?? defaultCategory

  const upload = useMutation({
    mutationFn: async (picked: FileList | File[]) => {
      const fd = new FormData()
      Array.from(picked).forEach((f) => fd.append('files', f))
      if (selectedCategory != null) fd.append('categoryId', String(selectedCategory))
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
    if (list && list.length) upload.mutate(list)
    if (inputRef.current) inputRef.current.value = ''
  }

  if (files.isLoading || categories.isLoading) return <Loading />
  if (files.error) return <ErrorBox error={files.error} />

  const all = files.data ?? []
  const counts = (id: number) => all.filter((f) => f.categoryId === id).length
  const shown = filter === 'all' ? all : all.filter((f) => f.categoryId === filter)

  return (
    <div className="files-tab">
      {/* 업로드 영역 */}
      <div
        className={drag ? 'dropzone over' : 'dropzone'}
        onDragOver={(e) => { e.preventDefault(); setDrag(true) }}
        onDragLeave={() => setDrag(false)}
        onDrop={(e) => { e.preventDefault(); setDrag(false); pick(e.dataTransfer.files) }}
        onClick={() => inputRef.current?.click()}
        role="button"
        tabIndex={0}
      >
        <input ref={inputRef} type="file" multiple hidden onChange={(e) => pick(e.target.files)} />
        <strong>{upload.isPending ? '업로드 중…' : '파일을 여기로 끌어다 놓거나 클릭해 선택'}</strong>
        <span>여러 개 한 번에 올릴 수 있습니다 · 아래에서 분류를 먼저 고르세요</span>
      </div>
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
      {upload.error && <ErrorBox error={upload.error} />}

      {/* 분류 필터 */}
      <div className="customer-chips file-chips">
        <button className={filter === 'all' ? 'chip active' : 'chip'} onClick={() => setFilter('all')}>전체 {all.length}</button>
        {categories.data?.map((c) => (
          <button key={c.id} className={filter === c.id ? 'chip active' : 'chip'} onClick={() => setFilter(c.id)}>
            {c.name} {counts(c.id)}
          </button>
        ))}
      </div>

      {/* 목록 */}
      {shown.length === 0 ? (
        <div className="empty" style={{ padding: 36 }}>업로드된 문서가 없습니다.</div>
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
                  {' '}{size(f.sizeBytes)}
                  {f.uploadedByName ? ` · ${f.uploadedByName}` : ''}{f.department ? ` (${f.department})` : ''} · {date(f.uploadedAt)}
                  {f.description ? ` · ${f.description}` : ''}
                </small>
              </div>
              <button className="secondary" onClick={() => downloadFile(`/api/projects/${projectId}/files/${f.id}/download`, f.fileName)}>받기</button>
              <button className="text-button danger" onClick={() => confirm(`"${f.fileName}"을(를) 삭제할까요?`) && del.mutate(f.id)} disabled={del.isPending}>삭제</button>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
