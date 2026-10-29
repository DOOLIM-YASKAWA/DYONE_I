import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useRef, useState } from 'react'
import { api, ApiError, API_BASE, tokenStore } from '../../api/client'
import type { MotosimAdmin, MotosimHub, MotosimNotice } from '../../api/motosimTypes'
import { ErrorBox, Loading } from '../../components/ui'
import { errorText, fmtBytes, fmtDateTime, timeAgo } from './broker'
import { ActivityList } from './MotosimHub'
import { MsDialog } from './MsDialog'

interface PluginVersion {
  id: number; version: string; displayName: string; items: { content: string }[]
  fullUploaded: boolean; updateUploaded: boolean; fullOnNas: boolean; updateOnNas: boolean; updatedAt: string; uploadedByName?: string
}

/**
 * 관리자 센터(원본 AdminActivityOverview): 플러그인 업로드 | 버전 목록 / 작업 현황 · 최근 활동 / 공지 관리 · 저장소 정리.
 * 권한 체크는 아직 없다(권한 단계에서 시스템관리자만 열리게 한다).
 */
export function MotosimAdminCenter({ onClose, onOpenZone }: { onClose(): void; onOpenZone(id: number): void }) {
  const hub = useQuery({ queryKey: ['motosim', 'hub'], queryFn: () => api<MotosimHub>('/api/motosim/hub') })
  const admin = useQuery({ queryKey: ['motosim', 'admin'], queryFn: () => api<MotosimAdmin>('/api/motosim/admin') })
  const stat = (kind: string) => hub.data?.stats.find((s) => s.kind === kind)
  const max = Math.max(1, ...(hub.data?.daily.map((d) => Math.max(d.uploads, d.downloads)) ?? [1]))

  return (
    <MsDialog title="관리자 센터" sub="Smart Cell Plugin 배포 · 작업 현황 · 공지 · 저장소" wide onClose={onClose}>
      <div className="ms-admin">
        <div className="ms-admin-row two">
          <PluginUploadPanel />
          <PluginVersionPanel />
        </div>
        <div className="ms-admin-row side">
          <div className="ms-hub-card">
            <div className="ms-hub-card-head"><h3>작업 현황</h3><small>한국 시간 기준</small></div>
            {hub.isLoading && <Loading />}
            {hub.error && <ErrorBox error={hub.error} />}
            {hub.data && (
              <>
                <div className="ms-tiles">
                  <div className="ms-tile"><span className="up">⬆</span><small>오늘 업로드</small><b>{stat('cell-upload')?.today ?? 0}</b></div>
                  <div className="ms-tile"><span className="down">⬇</span><small>오늘 다운로드</small><b>{stat('cell-download')?.today ?? 0}</b></div>
                </div>
                <dl className="ms-store">
                  <dt>7일</dt><dd>업로드 {stat('cell-upload')?.week ?? 0} · 다운로드 {stat('cell-download')?.week ?? 0} · 삭제 {stat('cell-delete')?.week ?? 0}</dd>
                  <dt>30일</dt><dd>업로드 {stat('cell-upload')?.month ?? 0} · 다운로드 {stat('cell-download')?.month ?? 0} · 삭제 {stat('cell-delete')?.month ?? 0}</dd>
                </dl>
                <div className="ms-chart" aria-label="최근 14일 업로드·다운로드">
                  {hub.data.daily.map((d) => (
                    <div key={d.day} title={`${d.day} 업로드 ${d.uploads} · 다운로드 ${d.downloads}`}>
                      <i className="up" style={{ height: `${(d.uploads / max) * 100}%` }} />
                      <i className="down" style={{ height: `${(d.downloads / max) * 100}%` }} />
                      <small>{d.day.slice(8)}</small>
                    </div>
                  ))}
                </div>
                <div className="ms-legend"><span className="up">업로드</span><span className="down">다운로드</span></div>
              </>
            )}
          </div>
          <div className="ms-hub-card">
            <div className="ms-hub-card-head"><h3>최근 활동</h3><small>최근 30건</small></div>
            {hub.data && <ActivityList items={hub.data.activities} onOpenZone={onOpenZone} />}
          </div>
        </div>
        <div className="ms-admin-row two">
          <NoticePanel notices={hub.data?.notices ?? []} />
          <StoragePanel admin={admin.data} error={admin.error} hub={hub.data} />
        </div>
      </div>
    </MsDialog>
  )
}

const SETUP_NAME = /^SmartCell_Plugin_Setup_(Full|Update)_v(\d+\.\d+\.\d+\.\d+)\.exe$/i

function PluginUploadPanel() {
  const qc = useQueryClient()
  const input = useRef<HTMLInputElement>(null)
  const [files, setFiles] = useState<File[]>([])
  const [state, setState] = useState<{ busy: boolean; text?: string; error?: boolean }>({ busy: false })
  const parsed = files.map((f) => ({ f, m: SETUP_NAME.exec(f.name) }))
  const problem = files.length === 0 ? null
    : parsed.some((p) => !p.m) ? '파일 이름은 SmartCell_Plugin_Setup_Full_v1.2.3.4.exe 형식이어야 합니다.'
    : new Set(parsed.map((p) => p.m![2])).size > 1 ? 'Full·Update 버전이 같아야 합니다.'
    : new Set(parsed.map((p) => p.m![1].toLowerCase())).size !== parsed.length ? 'Full·Update는 하나씩만 올릴 수 있습니다.' : null

  const upload = async () => {
    setState({ busy: true, text: '올리는 중…' })
    try {
      const fd = new FormData()
      files.forEach((f) => fd.append('files', f, f.name))
      const res = await fetch(`${API_BASE}/api/motosim/plugins/smartcell/upload`, { method: 'POST', body: fd, headers: { Authorization: `Bearer ${tokenStore.get() ?? ''}` } })
      const body = await res.json().catch(() => ({}))
      if (!res.ok) throw new ApiError(res.status, body.detail ?? body.title ?? `업로드 실패(${res.status})`)
      setState({ busy: false, text: `v${body.version} 올렸습니다. 버전 목록에서 변경 내역을 적어 주세요.` })
      setFiles([])
      if (input.current) input.current.value = ''
      qc.invalidateQueries({ queryKey: ['motosim', 'plugin-versions'] })
    } catch (e) { setState({ busy: false, text: errorText(e), error: true }) }
  }

  return (
    <div className="ms-hub-card">
      <div className="ms-hub-card-head"><h3>Smart Cell Plugin 업로드</h3><small>NAS _motosim/plugins</small></div>
      <p className="ms-desc">Full(VC++ 포함, 처음 설치용)·Update 설치 파일을 같은 버전으로 하나씩 올립니다. 같은 이름이 있으면 덮어씁니다.</p>
      <input ref={input} type="file" accept=".exe" multiple onChange={(e) => setFiles([...(e.target.files ?? [])].slice(0, 2))} />
      {parsed.map(({ f, m }) => (
        <div key={f.name} className="ms-file-line"><span className={`ms-badge ${m ? (m[1].toLowerCase() === 'full' ? 'blue' : 'amber') : 'rose'}`}>{m ? m[1] : '이름 오류'}</span>{f.name} <small>{fmtBytes(f.size)}</small></div>
      ))}
      {problem && <p className="ms-warn">{problem}</p>}
      <div className="ms-tile-actions"><button className="ms-btn blue sm" disabled={!files.length || !!problem || state.busy} onClick={upload}>올리기</button></div>
      {state.text && <p className={state.error ? 'ms-warn' : 'ms-result'}>{state.text}</p>}
    </div>
  )
}

function PluginVersionPanel() {
  const qc = useQueryClient()
  const list = useQuery({ queryKey: ['motosim', 'plugin-versions'], queryFn: () => api<PluginVersion[]>('/api/motosim/plugins/smartcell/versions') })
  const [edit, setEdit] = useState<PluginVersion | null>(null)
  const [text, setText] = useState('')
  const [remove, setRemove] = useState<PluginVersion | null>(null)
  const save = useMutation({
    mutationFn: () => api(`/api/motosim/plugins/smartcell/versions/${edit!.version}/notes`, { method: 'PUT', json: { items: text.split('\n').map((s) => ({ content: s })) } }),
    onSuccess: () => { setEdit(null); qc.invalidateQueries({ queryKey: ['motosim', 'plugin-versions'] }) },
  })
  const del = useMutation({
    mutationFn: (v: string) => api(`/api/motosim/plugins/smartcell/versions/${v}`, { method: 'DELETE' }),
    onSuccess: () => { setRemove(null); qc.invalidateQueries({ queryKey: ['motosim', 'plugin-versions'] }) },
  })
  return (
    <div className="ms-hub-card">
      <div className="ms-hub-card-head"><h3>Installer Version</h3><small>{list.data?.length ?? 0}개</small></div>
      {list.error && <ErrorBox error={list.error} />}
      <ul className="ms-plugin-list">
        {list.data?.map((v) => (
          <li key={v.id}>
            <div><b>{v.displayName}</b><small>{v.uploadedByName ?? '—'} · {timeAgo(v.updatedAt)} · 변경 내역 {v.items.length}줄</small></div>
            <span className={`ms-badge ${v.fullOnNas ? 'blue' : 'muted'}`} title={v.fullOnNas ? 'NAS에 있음' : v.fullUploaded ? 'Google Drive에만 있음' : '없음'}>Full{!v.fullOnNas && v.fullUploaded ? '(Drive)' : ''}</span>
            <span className={`ms-badge ${v.updateOnNas ? 'amber' : 'muted'}`}>Update{!v.updateOnNas && v.updateUploaded ? '(Drive)' : ''}</span>
            <button className="ms-icon" title="변경 내역" onClick={() => { setEdit(v); setText(v.items.map((i) => i.content).join('\n')) }}>✎</button>
            <button className="ms-icon rose" title="버전 내리기" onClick={() => setRemove(v)}>🗑</button>
          </li>
        ))}
      </ul>
      {edit && (
        <MsDialog title={`변경 내역 v${edit.version}`} sub="한 줄에 하나씩(30줄, 줄마다 300자까지)" onClose={() => setEdit(null)}
          footer={<><button className="ms-btn outline" onClick={() => setEdit(null)}>취소</button><button className="ms-btn blue" disabled={save.isPending} onClick={() => save.mutate()}>저장</button></>}>
          <textarea className="ms-notes-input" rows={12} value={text} onChange={(e) => setText(e.target.value)} />
          {save.error && <p className="ms-warn">{errorText(save.error)}</p>}
        </MsDialog>
      )}
      {remove && (
        <MsDialog title="버전 내리기" small onClose={() => setRemove(null)}
          footer={<><button className="ms-btn outline" onClick={() => setRemove(null)}>취소</button><button className="ms-btn rose" onClick={() => del.mutate(remove.version)}>내리기</button></>}>
          <p className="ms-msg">v{remove.version}을 목록에서 내립니다. 이 버전은 더 이상 설치 대상이 되지 않습니다(NAS 파일은 남겨 둡니다).</p>
        </MsDialog>
      )}
    </div>
  )
}

function NoticePanel({ notices }: { notices: MotosimNotice[] }) {
  const qc = useQueryClient()
  const [edit, setEdit] = useState<{ id?: number; title: string; body: string } | null>(null)
  const done = () => { setEdit(null); qc.invalidateQueries({ queryKey: ['motosim', 'hub'] }) }
  const save = useMutation({
    mutationFn: () => edit!.id
      ? api(`/api/motosim/notices/${edit!.id}`, { method: 'PUT', json: { title: edit!.title, body: edit!.body } })
      : api('/api/motosim/notices', { method: 'POST', json: { title: edit!.title, body: edit!.body } }),
    onSuccess: done,
  })
  const del = useMutation({ mutationFn: (id: number) => api(`/api/motosim/notices/${id}`, { method: 'DELETE' }), onSuccess: done })
  return (
    <div className="ms-hub-card">
      <div className="ms-hub-card-head"><h3>공지 관리</h3><button className="ms-btn blue sm" onClick={() => setEdit({ title: '', body: '' })}>새 공지</button></div>
      {notices.length === 0 && <div className="ms-empty">공지가 없습니다.</div>}
      <ul className="ms-plugin-list">
        {notices.map((n) => (
          <li key={n.id}>
            <div><b>{n.title}</b><small>{n.authorName ?? '관리자'} · {fmtDateTime(n.updatedAt)}</small></div>
            <button className="ms-icon" title="고치기" onClick={() => setEdit({ id: n.id, title: n.title, body: n.body })}>✎</button>
            <button className="ms-icon rose" title="내리기" onClick={() => del.mutate(n.id)}>🗑</button>
          </li>
        ))}
      </ul>
      {edit && (
        <MsDialog title={edit.id ? '공지 고치기' : '새 공지'} onClose={() => setEdit(null)}
          footer={<><button className="ms-btn outline" onClick={() => setEdit(null)}>취소</button>
            <button className="ms-btn blue" disabled={!edit.title.trim() || save.isPending} onClick={() => save.mutate()}>저장</button></>}>
          <label className="ms-field">제목<input maxLength={120} value={edit.title} onChange={(e) => setEdit({ ...edit, title: e.target.value })} /></label>
          <label className="ms-field">내용<textarea maxLength={300} rows={6} value={edit.body} onChange={(e) => setEdit({ ...edit, body: e.target.value })} /></label>
          {save.error && <p className="ms-warn">{errorText(save.error)}</p>}
        </MsDialog>
      )}
    </div>
  )
}

function StoragePanel({ admin, error, hub }: { admin?: MotosimAdmin; error: unknown; hub?: MotosimHub }) {
  const qc = useQueryClient()
  const cleanup = useMutation({
    mutationFn: () => api<{ cancelled: number }>('/api/motosim/admin/cleanup-uploads', { method: 'POST' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['motosim', 'admin'] }),
  })
  return (
    <div className="ms-hub-card">
      <div className="ms-hub-card-head"><h3>저장소 · 올리다 만 업로드</h3>
        <button className="ms-btn outline sm" disabled={cleanup.isPending} onClick={() => cleanup.mutate()} title="24시간 넘게 멈춘 업로드를 취소하고 임시 조각을 지웁니다">멈춘 업로드 정리</button></div>
      {error ? <ErrorBox error={error} /> : null}
      {cleanup.data && <p className="ms-result">{cleanup.data.cancelled}건 정리했습니다.</p>}
      <dl className="ms-store">
        <dt>NAS Cell 용량</dt><dd>{fmtBytes(hub?.storageBytes)}</dd>
        <dt>휴지통(_trash)</dt><dd>{admin ? `${admin.trashCount}개 · ${fmtBytes(admin.trashBytes)}` : '—'}</dd>
        <dt>임시 조각(_tmp)</dt><dd>{fmtBytes(admin?.tempBytes)}</dd>
      </dl>
      {admin && admin.uploads.length === 0 && <div className="ms-empty">진행 중이거나 멈춘 업로드가 없습니다.</div>}
      <ul className="ms-plugin-list">
        {admin?.uploads.map((u) => (
          <li key={u.id}>
            <div><b>{u.fileName}</b><small>{u.createdByName ?? '—'} · {u.zonePath ?? ''} · {timeAgo(u.updatedAt)}</small></div>
            <span className="ms-badge muted">{Math.round((u.received / u.totalSize) * 100)}% · {fmtBytes(u.totalSize)}</span>
          </li>
        ))}
      </ul>
    </div>
  )
}
