import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type ReactNode } from 'react'
import { api } from '../../api/client'
import type { ModularFile, ModularHistoryItem, WorksDocDetail, WorksDocList, WorksField, WorksLayoutNode, WorksMeta, WorksStatus } from '../../api/types'
import { DataTable, ErrorBox, Loading, Modal } from '../../components/ui'
import { useToast } from '../../components/Toast'

/**
 * 그룹웨어 WORKS 앱 화면 — 앱 번호만 주면 그룹웨어 정의(목록 열·폼 배치·상태·선택지)대로 그린다. (첫 대상 281 ES 이슈)
 * 두 벌 관리: 화면 값은 DY ONE 값이다. 그룹웨어에서 바뀐 칸은 동기화 때 반영되고, DY ONE에서 고친 값은 그룹웨어로 보내지 않는다.
 * 고칠 수 있는 칸은 글자·날짜·숫자·선택뿐이고, 사용자·참조 문서·파일 칸은 그룹웨어 값 그대로 보여 준다.
 */

const PAGE_SIZE = 50
const when = (v?: string) => (v ? new Date(v).toLocaleString('ko-KR', { dateStyle: 'short', timeStyle: 'short' }) : '')
const bytes = (n?: number) =>
  n === undefined ? '' : n >= 1 << 20 ? `${(n / (1 << 20)).toFixed(1)} MB` : n >= 1024 ? `${Math.round(n / 1024)} KB` : `${n} B`

function tone(name?: string) {
  if (!name) return ''
  if (/완료/.test(name)) return 'green'
  if (/지연|반려/.test(name)) return 'red'
  if (/접수|대기|홀딩|준비/.test(name)) return 'amber'
  return 'blue'
}

const StatusBadge = ({ status }: { status?: WorksStatus | null }) =>
  status ? <span className={`badge ${tone(status.name)}`}>{status.name}</span> : <span className="badge">상태 없음</span>

const useMeta = (applet: number) =>
  useQuery({ queryKey: ['works-meta', applet], queryFn: () => api<WorksMeta>(`/api/works/apps/${applet}/meta`), staleTime: 5 * 60_000 })

export function WorksAppSection({ appletId, projectCode, openDocId = null, onDocClosed }: { appletId: number; projectCode: string | null; openDocId?: number | null; onDocClosed?(): void }) {
  const meta = useMeta(appletId)
  const [statusId, setStatusId] = useState('')
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(1)
  const [openId, setOpenId] = useState<number | null>(openDocId)
  // 알림을 눌러 같은 화면에서 다른 문서로 오면(주소의 doc만 바뀜) 그 문서를 연다
  const [seenDoc, setSeenDoc] = useState(openDocId)
  if (openDocId !== seenDoc) {
    setSeenDoc(openDocId)
    if (openDocId !== null) setOpenId(openDocId)
  }
  const closeDoc = () => {
    setOpenId(null)
    onDocClosed?.()
  }
  const [creating, setCreating] = useState(false)

  const params = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) })
  if (statusId) params.set('statusId', statusId)
  if (search) params.set('q', search)
  if (projectCode) params.set('projectCode', projectCode)
  const docs = useQuery({
    queryKey: ['works-docs', appletId, params.toString()],
    queryFn: () => api<WorksDocList>(`/api/works/apps/${appletId}/docs?${params}`),
    placeholderData: keepPreviousData,
  })

  if (meta.isLoading) return <Loading />
  if (!meta.data) return <ErrorBox error={meta.error} />
  const m = meta.data
  const byCid = new Map(m.fields.map((f) => [f.cid, f]))
  const columns = (m.viewColumns ?? []).filter((c) => c === 'status' || byCid.has(c))
  // 제목 열: 목록 열 중 첫 글자 칸(그룹웨어 목록에서 문서를 여는 칸)
  const titleCid = columns.find((c) => ['text', 'textarea'].includes(byCid.get(c)?.fieldType ?? '')) ?? columns.find((c) => c !== 'status')
  const counts = new Map((docs.data?.statusCounts ?? []).map((c) => [c.statusId, c.count]))
  const allCount = [...counts.values()].reduce((a, b) => a + b, 0)
  const reset = (fn: () => void) => { fn(); setPage(1) }

  return (
    <div className="modular">
      <div className="modular-toolbar">
        <input
          className="search-input"
          placeholder="내용·고객사·부품 등 검색"
          defaultValue={search}
          aria-label={`${m.name} 검색`}
          onKeyDown={(e) => e.key === 'Enter' && reset(() => setSearch(e.currentTarget.value.trim()))}
        />
        {m.editEnabled && <button className="primary" onClick={() => setCreating(true)}>+ 새 문서</button>}
      </div>

      <div className="status-chips" role="group" aria-label="상태">
        <button className={statusId === '' ? 'active' : ''} onClick={() => reset(() => setStatusId(''))}>전체 <b>{allCount || ''}</b></button>
        {m.statuses.map((s) => (
          <button key={s.id} className={statusId === String(s.id) ? 'active' : ''} onClick={() => reset(() => setStatusId(String(s.id)))}>
            <i className={`dot ${tone(s.name)}`} />{s.name} <b>{counts.get(s.id) ?? 0}</b>
          </button>
        ))}
        {(counts.get(0) ?? 0) > 0 && (
          <button className={statusId === '0' ? 'active' : ''} onClick={() => reset(() => setStatusId('0'))}>상태 없음 <b>{counts.get(0)}</b></button>
        )}
      </div>

      {docs.isLoading ? <Loading /> : docs.error ? <ErrorBox error={docs.error} /> : (
        <>
          <DataTable
            headers={columns.map((c) => (c === 'status' ? '상태' : byCid.get(c)!.label))}
            rows={(docs.data?.items ?? []).map((d) => ({
              key: String(d.id),
              cells: columns.map((c) =>
                c === 'status' ? <StatusBadge status={d.status} />
                  : c === titleCid ? (
                    <button className="row-title" onClick={() => setOpenId(d.id)}>
                      {d.display[c] || '(제목 없음)'}
                      {!d.fromGroupware && <span className="badge blue mini">DY ONE</span>}
                      {d.managedInDyone && <span className="badge blue mini">DY ONE 관리</span>}
                      {d.gwDeleted && <span className="badge red mini">그룹웨어 삭제</span>}
                    </button>
                  ) : d.display[c] ?? '-'),
            }))}
            empty="조건에 맞는 문서가 없습니다."
          />
          <Pager total={docs.data?.total ?? 0} page={page} onPage={setPage} />
        </>
      )}

      <p className="modular-sync">
        {m.syncedAt ? `그룹웨어 ${m.appletId} 마지막 반영 ${new Date(m.syncedAt).toLocaleString('ko-KR')}` : `그룹웨어 ${m.appletId}`}
        {!m.editEnabled && ' · 지금은 DY ONE에서 조회만 합니다.'}
      </p>

      {openId !== null && <DocDialog meta={m} id={openId} onClose={closeDoc} />}
      {creating && <DocForm meta={m} doc={null} onClose={() => setCreating(false)} onSaved={(id) => { setCreating(false); setOpenId(id) }} />}
    </div>
  )
}

/** 상세: 그룹웨어 폼 배치 그대로 + 상태 전환 + 연동 상태 + 활동·이력·첨부 */
function DocDialog({ meta, id, onClose }: { meta: WorksMeta; id: number; onClose(): void }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [editing, setEditing] = useState(false)
  const [tab, setTab] = useState<'form' | 'activity' | 'history' | 'files'>('form')
  const [historyKind, setHistoryKind] = useState<'all' | 'status' | 'edit'>('all')
  const detail = useQuery({ queryKey: ['works-doc', id], queryFn: () => api<WorksDocDetail>(`/api/works/docs/${id}`) })
  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['works-doc', id] })
    void queryClient.invalidateQueries({ queryKey: ['works-docs', meta.appletId] })
  }
  const transition = useMutation({
    mutationFn: (transitionId: number) => api(`/api/works/docs/${id}/transition`, { method: 'POST', json: { transitionId } }),
    onSuccess: () => { toast('상태를 바꿨습니다.'); refresh() },
    onError: (e) => toast(e instanceof Error ? e.message : '상태를 바꾸지 못했습니다.'),
  })
  const manage = useMutation({
    mutationFn: () => api(`/api/works/docs/${id}/manage`, { method: 'POST' }),
    onSuccess: () => { toast('DY ONE 관리로 전환했습니다. 이제 그룹웨어 변경은 반영하지 않습니다.'); refresh() },
    onError: (e) => toast(e instanceof Error ? e.message : '전환하지 못했습니다.'),
  })

  if (!detail.data) return <Modal title={meta.name} onClose={onClose}>{detail.error ? <ErrorBox error={detail.error} /> : <Loading />}</Modal>
  const d = detail.data
  const statusName = (sid: number) => meta.statuses.find((s) => s.id === sid)?.name ?? String(sid)
  const titleCid = (meta.viewColumns ?? []).find((c) => ['text', 'textarea'].includes(meta.fields.find((f) => f.cid === c)?.fieldType ?? ''))

  if (editing) return <DocForm meta={meta} doc={d} onClose={() => setEditing(false)} onSaved={() => { setEditing(false); refresh() }} />

  return (
    <Modal
      title={(titleCid && d.display[titleCid]) || meta.name}
      onClose={onClose}
      footer={d.editable ? <button className="primary" onClick={() => setEditing(true)}>수정</button> : undefined}
    >
      <div className="modular-head">
        <StatusBadge status={d.status} />
        <span className={`badge ${d.managedInDyone || !d.fromGroupware ? 'blue' : ''}`}>
          {!d.fromGroupware ? 'DY ONE 입력' : d.managedInDyone ? 'DY ONE 관리' : '그룹웨어 연동'}
        </span>
        {d.gwDeletedAt && <span className="badge red">그룹웨어에서 삭제됨</span>}
        <small>{meta.name}</small>
      </div>

      {d.fromGroupware && (
        <div className={`modular-sync-note${d.managedInDyone ? ' is-managed' : ''}`}>
          <p>
            {d.managedInDyone
              ? `DY ONE에서 관리하는 문서입니다${d.managedInDyoneBy ? ` (${d.managedInDyoneBy} 전환)` : ''}. 그룹웨어 변경은 반영하지 않습니다.`
              : '그룹웨어에서 바뀐 칸은 DY ONE에도 반영됩니다. DY ONE에서 고친 값은 그룹웨어로 보내지 않으며, 그룹웨어에서 같은 칸을 바꾸면 그룹웨어 값으로 덮입니다.'}
          </p>
          {d.differences.length > 0 && (
            <>
              <b>DY ONE에서 바꾼 칸 {d.differences.length}개 (그룹웨어 값과 다름)</b>
              <ul>
                {d.differences.map((x) => (
                  <li key={x.key}><span>{x.label}</span><em>DY ONE {x.dyOne}</em><small>그룹웨어 {x.groupware}</small></li>
                ))}
              </ul>
            </>
          )}
          {d.editable && !d.managedInDyone && (
            <button
              className="secondary"
              disabled={manage.isPending}
              onClick={() => window.confirm('이 문서를 DY ONE 관리로 전환할까요?\n이후 그룹웨어에서 바뀐 값은 DY ONE에 반영되지 않습니다. 되돌릴 수 없습니다.') && manage.mutate()}
            >
              DY ONE 관리 전환
            </button>
          )}
        </div>
      )}

      <div className="modular-flow">
        <span>현재 상태에서 할 수 있는 전환</span>
        {d.transitions.length === 0 && <em>없음</em>}
        {d.transitions.map((t) => (
          <button
            key={t.id}
            className="secondary"
            disabled={!d.editable || transition.isPending}
            title={d.editable ? `${t.action} → ${statusName(t.to)}` : '입력 기능이 꺼져 있습니다.'}
            onClick={() => window.confirm(`'${t.action}' → ${statusName(t.to)}(으)로 바꿀까요?`) && transition.mutate(t.id)}
          >
            {t.action} <small>→ {statusName(t.to)}</small>
          </button>
        ))}
      </div>

      <div className="project-tabs modular-tabs" role="tablist">
        {([['form', '정보', null], ['activity', '활동 기록', d.activities.length], ['history', '변경 이력', d.history.length], ['files', '첨부', d.files.length]] as const).map(([k, label, n]) => (
          <button key={k} role="tab" aria-selected={tab === k} className={tab === k ? 'active' : ''} onClick={() => setTab(k)}>
            {label} {n !== null && <small>{n}</small>}
          </button>
        ))}
      </div>

      {tab === 'form' && (
        <>
          {meta.layout ? <Layout node={meta.layout} meta={meta} render={(f) => <ReadField field={f} doc={d} />} /> : <p className="empty">폼 배치 정보가 없습니다.</p>}
          <p className="modular-sync">
            등록 {d.createdByName ?? '-'} · {when(d.createdAt) || '-'} / 수정 {d.updatedByName ?? '-'} · {when(d.updatedAt) || '-'}
            {d.gwUrl && <>{' · '}<a href={d.gwUrl} target="_blank" rel="noopener noreferrer">그룹웨어 {meta.appletId} 열기 ↗</a></>}
          </p>
        </>
      )}

      {tab === 'activity' && (
        <div className="modular-activities">
          {d.activities.length === 0 && <div className="empty">활동 기록이 없습니다.</div>}
          {d.activities.map((a) => (
            <article key={a.id}>
              <header><b>{a.by ?? '작성자 미상'}</b><small>{when(a.at)}</small></header>
              <p>{a.body ?? '(내용 없음)'}</p>
              {a.files.length > 0 && <FileList files={a.files} />}
              {a.comments.length > 0 && (
                <ul className="modular-comments">
                  {a.comments.map((c) => <li key={c.id}><b>{c.by ?? '작성자 미상'}</b> <small>{when(c.at)}</small><p>{c.body ?? '(내용 없음)'}</p></li>)}
                </ul>
              )}
            </article>
          ))}
        </div>
      )}

      {tab === 'history' && <HistoryList items={d.history} kind={historyKind} onKind={setHistoryKind} />}

      {tab === 'files' && (
        <div className="modular-activities">
          {d.files.length === 0 ? <div className="empty">첨부 파일이 없습니다.</div> : <FileList files={d.files} showSource />}
          {d.files.some((x) => !x.stored) && <p className="modular-sync">파일 목록은 DY ONE에 옮겼고, 파일 본체는 아직 그룹웨어 서버에 있습니다. 내려받기는 그룹웨어에서 하세요.</p>}
        </div>
      )}
    </Modal>
  )
}

/** 그룹웨어 폼 배치 그리기: label(구역 제목) · columns(N열) · column · hr · 필드 */
function Layout({ node, meta, render }: { node: WorksLayoutNode; meta: WorksMeta; render(field: WorksField): ReactNode }) {
  const field = node.cid ? meta.fields.find((f) => f.cid === node.cid) : undefined
  const kids = (node.children ?? []).map((c, i) => <Layout key={c.cid ?? i} node={c} meta={meta} render={render} />)
  switch (node.type) {
    case 'canvas': return <div className="works-form">{kids}</div>
    case 'label': return <h3 className="works-label">{node.label}</h3>
    case 'hr': return <hr className="works-hr" />
    case 'columns': return <div className="works-columns" style={{ ['--cols' as string]: Number(node.count) || (node.children?.length ?? 1) }}>{kids}</div>
    case 'column': return <div className="works-column">{kids}</div>
    default: return field ? <>{render(field)}</> : <>{kids}</>
  }
}

function ReadField({ field, doc }: { field: WorksField; doc: WorksDocDetail }) {
  const value = doc.display[field.cid]
  return (
    <div className="works-field">
      <dt>{field.label}</dt>
      <dd className={field.valueType === 'TEXT' ? 'is-long' : undefined}>{value === null || value === undefined || value === '' ? '-' : value}</dd>
    </div>
  )
}

/** 입력 폼(새 문서·수정): 고칠 수 있는 칸만 입력, 나머지는 그룹웨어 값 그대로 표시 */
function DocForm({ meta, doc, onClose, onSaved }: { meta: WorksMeta; doc: WorksDocDetail | null; onClose(): void; onSaved(id: number): void }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  const [values, setValues] = useState<Record<string, string>>(() =>
    Object.fromEntries(meta.fields.filter((f) => f.editable).map((f) => {
      const v = doc ? doc.values[f.cid] : f.options?.filter((o) => o.isDefault).map((o) => o.code).join(',') || null
      return [f.cid, v ?? '']
    })))
  const set = (cid: string, v: string) => setValues((s) => ({ ...s, [cid]: v }))

  const save = useMutation({
    mutationFn: () => {
      // 수정은 바뀐 칸만 보낸다
      const body = Object.fromEntries(Object.entries(values)
        .filter(([cid, v]) => !doc || (doc.values[cid] ?? '') !== v)
        .map(([cid, v]) => [cid, v === '' ? null : v]))
      return doc
        ? api<{ id: number }>(`/api/works/docs/${doc.id}`, { method: 'PUT', json: { values: body } })
        : api<{ id: number }>(`/api/works/apps/${meta.appletId}/docs`, { method: 'POST', json: { values: body } })
    },
    onSuccess: (r) => {
      toast(doc ? '저장했습니다.' : '등록했습니다.')
      void queryClient.invalidateQueries({ queryKey: ['works-docs', meta.appletId] })
      onSaved(r.id)
    },
    onError: (e) => toast(e instanceof Error ? e.message : '저장하지 못했습니다.'),
  })

  // 필수 칸은 새 문서에서만 막는다(그룹웨어 기존 문서에는 필수 칸이 비어 있는 경우가 많다)
  const missing = doc ? [] : meta.fields.filter((f) => f.editable && f.required && !values[f.cid]).map((f) => f.label)

  return (
    <Modal
      title={doc ? `${meta.name} 수정` : `${meta.name} 새 문서`}
      onClose={onClose}
      footer={<button className="primary" disabled={save.isPending || missing.length > 0} title={missing.length ? `필수: ${missing.join(', ')}` : undefined} onClick={() => save.mutate()}>저장</button>}
    >
      {meta.layout && <Layout node={meta.layout} meta={meta} render={(f) => f.editable
        ? <Input field={f} value={values[f.cid] ?? ''} onChange={(v) => set(f.cid, v)} />
        : <div className="works-field is-readonly"><dt>{f.label}</dt><dd>{doc?.display[f.cid] ?? '-'}<small> · 그룹웨어 값</small></dd></div>} />}
    </Modal>
  )
}

function Input({ field: f, value, onChange }: { field: WorksField; value: string; onChange(v: string): void }) {
  const label = <>{f.label}{f.required && ' *'}</>
  if (f.valueType === 'SELECT')
    return (
      <label className="field works-field">{label}
        <select value={value} onChange={(e) => onChange(e.target.value)}>
          <option value="">-</option>
          {f.options?.map((o) => <option key={o.code} value={o.code}>{o.label}</option>)}
        </select>
      </label>
    )
  if (f.valueType === 'SELECTS') {
    const picked = new Set(value ? value.split(',') : [])
    const toggle = (code: string) => {
      if (picked.has(code)) picked.delete(code)
      else picked.add(code)
      onChange([...picked].map(Number).sort((a, b) => a - b).join(','))
    }
    return (
      <fieldset className="modular-checks works-field"><legend>{label}</legend>
        {f.options?.map((o) => (
          <label key={o.code}><input type="checkbox" checked={picked.has(String(o.code))} onChange={() => toggle(String(o.code))} />{o.label}</label>
        ))}
      </fieldset>
    )
  }
  if (f.valueType === 'TEXT')
    return <label className="field works-field">{label}<textarea rows={4} value={value} onChange={(e) => onChange(e.target.value)} /></label>
  const type = f.valueType === 'DATE' ? 'date' : f.valueType === 'DATETIME' ? 'datetime-local' : f.valueType === 'NUMBER' ? 'number' : 'text'
  const shown = f.valueType === 'DATETIME' ? value.replace(' ', 'T') : value
  return (
    <label className="field works-field">{label}
      <input type={type} value={shown} onChange={(e) => onChange(f.valueType === 'DATETIME' ? e.target.value.replace('T', ' ') : e.target.value)} />
    </label>
  )
}

function HistoryList({ items, kind, onKind }: { items: ModularHistoryItem[]; kind: 'all' | 'status' | 'edit'; onKind(k: 'all' | 'status' | 'edit'): void }) {
  const shown = kind === 'all' ? items : items.filter((h) => h.kind === kind || (kind === 'edit' && h.kind === 'overwrite'))
  return (
    <div className="modular-activities">
      <div className="segmented" role="group" aria-label="이력 종류">
        {([['all', '전체'], ['status', '상태 변경'], ['edit', '정보 변경']] as const).map(([k, label]) => (
          <button key={k} className={kind === k ? 'active' : ''} onClick={() => onKind(k)}>{label}</button>
        ))}
      </div>
      {shown.length === 0 && <div className="empty">이력이 없습니다.</div>}
      {shown.map((h, i) => (
        <article key={`${h.source}-${i}`} className={h.kind === 'status' ? 'is-status' : h.kind === 'overwrite' ? 'is-overwrite' : h.source === 'dyone' ? 'is-dyone' : undefined}>
          <header>
            <b>{h.title ?? '변경'}</b>
            {h.kind === 'status' && h.to && (
              <span className="status-move"><span className={`badge ${tone(h.from)}`}>{h.from ?? '-'}</span> → <span className={`badge ${tone(h.to)}`}>{h.to}</span></span>
            )}
            <small>{h.by ?? ''} · {when(h.at)} · {h.source === 'dyone' ? 'DY ONE' : '그룹웨어'}</small>
          </header>
          {h.kind !== 'status' && h.detail && <p>{h.detail}</p>}
        </article>
      ))}
    </div>
  )
}

function FileList({ files, showSource }: { files: ModularFile[]; showSource?: boolean }) {
  return (
    <ul className="modular-files">
      {files.map((f) => (
        <li key={f.id}>
          <span className="file-ext">{(f.ext ?? '').toUpperCase() || 'FILE'}</span>
          <span className="file-name">{f.name ?? '(이름 없음)'}</span>
          <small>{bytes(f.size)}{showSource ? ` · ${f.source === 'doc' ? '첨부 칸' : '활동 기록'}` : ''}{f.stored ? '' : ' · 그룹웨어 보관'}</small>
        </li>
      ))}
    </ul>
  )
}

function Pager({ total, page, onPage }: { total: number; page: number; onPage(p: number): void }) {
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE))
  if (pages <= 1) return null
  return (
    <div className="pager">
      <button className="secondary" disabled={page <= 1} onClick={() => onPage(page - 1)}>← 이전</button>
      <span>{page} / {pages}</span>
      <button className="secondary" disabled={page >= pages} onClick={() => onPage(page + 1)}>다음 →</button>
    </div>
  )
}
