import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { api, downloadFile } from '../../api/client'
import type { MotosimCellFile, MotosimNode, MotosimProject, MotosimTree, MotosimZone } from '../../api/motosimTypes'
import { ErrorBox, Loading } from '../../components/ui'
import '../../styles/motosim.css'
import {
  broker, brokerWithToken, errorText, fmtBytes, fmtDateTime, isDesktop, lastWork, newJobId, useBrokerEvent, useRunPath, zoneVcl,
  type CellPlan, type MotosimStatus, type RunEvent, type TransferProgress,
} from './broker'
import { MotosimAdminCenter } from './MotosimAdmin'
import { MotosimHubDialog } from './MotosimHub'
import { MsDialog } from './MsDialog'

/**
 * MotoSim (MotoSim Launcher 이관) — 원본 런처 Dashboard 화면을 그대로 옮긴 모양.
 * 왼쪽 공정 트리(plant.*) · 오른쪽 Cell 카드 + 히스토리. 업로드·다운로드·실행은 Windows 앱(Local Broker), 파일은 NAS.
 * 권한(게스트·작업자·관리자)은 아직 없다 — 로그인한 직원 누구나 올리고 지울 수 있다.
 */
type Level = 'customer' | 'factory' | 'booth'
const key = (level: Level, id: number) => `${level}:${id}`

export interface Transfer {
  jobId: string
  direction: 'upload' | 'download'
  fileName: string
  stage: 'preparing' | 'compressing' | 'uploading' | 'finishing' | 'downloading' | 'extracting' | 'success' | 'failed' | 'cancelled'
  message?: string
  done?: number
  total?: number
  percent?: number
  minimized?: boolean
  result?: { folder?: string; vclPath?: string; zoneId?: number }
}

export function MotosimPage() {
  const qc = useQueryClient()
  const tree = useQuery({ queryKey: ['motosim', 'tree'], queryFn: () => api<MotosimTree>('/api/motosim/tree') })
  const favorites = useQuery({ queryKey: ['motosim', 'favorites'], queryFn: () => api<number[]>('/api/motosim/favorites') })
  const status = useQuery({ queryKey: ['motosim', 'status'], queryFn: () => broker<MotosimStatus>('motosim.status'), enabled: isDesktop })
  const [runPath, setRunPath] = useRunPath()
  const [zoneId, setZoneId] = useState<number | null>(null)
  const [search, setSearch] = useState('')
  const [favOnly, setFavOnly] = useState(false)
  const [open, setOpen] = useState<Set<string>>(new Set())
  const [transfer, setTransfer] = useState<Transfer | null>(null)
  const [run, setRun] = useState<RunEvent | null>(null)
  const [dialog, setDialog] = useState<'hub' | 'admin' | 'version' | null>(() => (sessionStorage.getItem('motosim-hub-seen') ? null : 'hub'))
  const [message, setMessage] = useState<{ title: string; body: ReactNode } | null>(null)
  const idx = useMemo(() => (tree.data ? index(tree.data) : null), [tree.data])
  const fav = useMemo(() => new Set(favorites.data ?? []), [favorites.data])

  useEffect(() => { if (dialog === 'hub') sessionStorage.setItem('motosim-hub-seen', '1') }, [dialog])

  // 전송 진행 이벤트 → 오른쪽 아래 전송 창
  const onProgress = useCallback((p: TransferProgress) => {
    setTransfer((t) => {
      if (!t || t.jobId !== p.jobId || ['success', 'failed', 'cancelled'].includes(t.stage)) return t
      const stage = ({ compressing: 'compressing', upload: 'uploading', finishing: 'finishing', download: 'downloading', extracting: 'extracting' } as const)[p.phase]
      return { ...t, stage, done: p.done, total: p.total, percent: p.percent }
    })
  }, [])
  useBrokerEvent('cell.progress', onProgress)
  const onRun = useCallback((e: RunEvent) => setRun((r) => (r && r.runId !== e.runId ? r : e)), [])
  useBrokerEvent('motosim.run', onRun)

  const selectedInstall = status.data?.installs.find((i) => i.path === runPath) ?? status.data?.installs[0]
  const versionLabel = runPath && !status.data?.installs.some((i) => i.path === runPath) ? '사용자 지정' : selectedInstall?.label

  const launch = async (vclPath: string) => {
    try {
      const runId = newJobId()
      setRun({ runId, stage: 'cleaning' })
      await broker('motosim.launch', { vclPath, runId, exePath: runPath ?? undefined })
    } catch (e) {
      setRun(null)
      setMessage({ title: 'MotoSim 실행 실패', body: errorText(e) })
    }
  }

  // 헤더 [▶ MotoSim 실행하기]: 아무 .vcl이나 골라 실행
  const runAnyVcl = async () => {
    try {
      const plan = await broker<CellPlan | { canceled: true }>('cell.pickVcl')
      if (!plan.canceled) await launch(plan.vclPath)
    } catch (e) {
      setMessage({ title: 'MotoSim 실행 실패', body: errorText(e) })
    }
  }

  if (tree.isLoading) return <Loading />
  if (tree.error || !tree.data || !idx) return <ErrorBox error={tree.error} />

  const q = search.trim().toLowerCase()
  const visibleZones = new Set(
    tree.data.zones.filter((z) => (!favOnly || fav.has(z.id)) && (!q || idx.zonePath(z).join(' ').toLowerCase().includes(q))).map((z) => z.id),
  )
  const filtering = !!q || favOnly
  const zonesOf = (boothId: number) => (idx.zonesByBooth.get(boothId) ?? []).filter((z) => visibleZones.has(z.id))
  const boothsOf = (factoryId: number) => (idx.childrenOf.get(key('factory', factoryId)) ?? []).filter((b) => !filtering || zonesOf(b.id).length > 0)
  const factoriesOf = (customerId: number) => (idx.childrenOf.get(key('customer', customerId)) ?? []).filter((f) => !filtering || boothsOf(f.id).length > 0)
  const customers = tree.data.customers.filter((c) => !filtering || factoriesOf(c.id).length > 0)
  const isOpen = (k: string) => filtering || open.has(k)
  const toggle = (k: string) => setOpen((s) => { const n = new Set(s); if (n.has(k)) n.delete(k); else n.add(k); return n })
  const zone = zoneId != null ? idx.zones.get(zoneId) : undefined
  const totalCells = tree.data.zones.filter((z) => z.fileCount > 0).length

  const selectZone = (z: MotosimZone) => {
    setZoneId(z.id)
    lastWork.set({ zoneId: z.id, zoneName: z.name, path: idx.zonePath(z).slice(0, -1).join(' › '), at: new Date().toISOString() })
  }
  const openTo = (zid: number) => {
    const z = idx.zones.get(zid)
    if (!z) return
    const b = idx.booths.get(z.boothId), f = b && idx.factories.get(b.parentId!)
    setOpen((s) => new Set([...s, ...(f ? [key('customer', f.parentId!), key('factory', f.id)] : []), key('booth', z.boothId)]))
    selectZone(z)
  }

  const group = (level: Level, n: MotosimNode, depth: number, kids: ReactNode, count: number) => {
    const k = key(level, n.id)
    return (
      <li key={k}>
        <div className="ms-row" onDoubleClick={() => toggle(k)}>
          <button className="ms-chevron" onClick={() => toggle(k)} aria-label={`${n.name} ${isOpen(k) ? '접기' : '펼치기'}`} aria-expanded={isOpen(k)} disabled={filtering}>
            {isOpen(k) ? '▾' : '▸'}
          </button>
          <span className="ms-folder" aria-hidden>▤</span>
          <button className="ms-name" onClick={() => toggle(k)} title={LEVEL_LABEL[level]}>{n.name}</button>
          {count > 0 && <span className="ms-count">{count}</span>}
        </div>
        {isOpen(k) && <div className="ms-children" style={{ marginLeft: depth === 0 ? 10 : 10 }}>{kids}</div>}
      </li>
    )
  }

  return (
    <div className="ms">
      <header className="ms-header">
        <div className="ms-header-left">
          <button className="ms-ghost" onClick={() => setDialog('hub')}>▦ 런처 허브</button>
          <span className="ms-divider" />
          <h1>MotoSim Launcher <small>DY ONE</small></h1>
        </div>
        <div className="ms-header-right">
          <button className="ms-btn dark" onClick={() => setDialog('admin')}>⛨ 관리자</button>
          <button className="ms-btn blue" onClick={runAnyVcl} disabled={!isDesktop} title={isDesktop ? '내 PC의 .vcl을 골라 실행' : 'Windows 앱에서만 실행할 수 있습니다'}>▶ MotoSim 실행하기</button>
          <span className="ms-pill">모토심 버전 : <b>{isDesktop ? (versionLabel ?? '설치 안 됨') : '—'}</b></span>
          <button className="ms-btn dark" onClick={() => setDialog('version')} disabled={!isDesktop}>⚙ 버전 변경</button>
          <span className="ms-pill">총 Cell <b>{totalCells}</b>개</span>
        </div>
      </header>

      {!isDesktop && <div className="ms-web-note">PC 웹에서는 조회·브라우저 다운로드·삭제만 됩니다. Cell 업로드·MotoSim 실행은 DY ONE Windows 앱에서 해 주세요.</div>}
      {run && <RunBanner run={run} onClose={() => setRun(null)} />}

      <div className="ms-main">
        <aside className="ms-card ms-tree">
          <div className="ms-tree-tools">
            <button className="ms-icon" onClick={() => setZoneId(null)} title="선택 해제" aria-label="선택 해제">⊖</button>
            <button className={`ms-icon star${favOnly ? ' on' : ''}`} onClick={() => setFavOnly((v) => !v)} title="즐겨찾기 폴더 열기" aria-label="즐겨찾기만" aria-pressed={favOnly}>★</button>
            <button className="ms-icon" onClick={() => setOpen(new Set())} title="모두 접기" aria-label="모두 접기">⇕</button>
            <input className="ms-search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="공장·부스·Zone 검색" aria-label="공정 검색" />
          </div>
          <div className="ms-tree-box">
            {customers.length === 0 && <div className="ms-empty">{favOnly ? '즐겨찾기한 Zone이 없습니다.' : '일치하는 공정이 없습니다.'}</div>}
            <ul className="ms-tree-list">
              {customers.map((c) => group('customer', c, 0,
                <ul>{factoriesOf(c.id).map((f) => group('factory', f, 1,
                  <ul>{boothsOf(f.id).map((b) => group('booth', b, 2,
                    <ul>
                      {zonesOf(b.id).length === 0 && <li className="ms-none">등록된 Zone 없음</li>}
                      {zonesOf(b.id).map((z) => (
                        <li key={z.id}>
                          <div className={`ms-row zone${zoneId === z.id ? ' on' : ''}`}>
                            <span className="ms-chevron" />
                            <button className="ms-name" onClick={() => selectZone(z)} aria-pressed={zoneId === z.id}>{z.name}</button>
                            {fav.has(z.id) && <span className="ms-fav" aria-label="즐겨찾기">★</span>}
                            {z.fileCount > 0 && <span className="ms-count">{z.fileCount}</span>}
                          </div>
                        </li>
                      ))}
                    </ul>, idx.fileCount(key('booth', b.id))))}
                  </ul>, idx.fileCount(key('factory', f.id))))}
                </ul>, idx.fileCount(key('customer', c.id))))}
            </ul>
          </div>
        </aside>

        <section className="ms-detail">
          {zone ? (
            <ZoneDetail
              key={zone.id}
              zone={zone}
              path={idx.zonePath(zone)}
              favorite={fav.has(zone.id)}
              busy={!!transfer && !['success', 'failed', 'cancelled'].includes(transfer.stage)}
              running={run?.stage === 'running' || run?.stage === 'launching'}
              onTransfer={setTransfer}
              onLaunch={launch}
              onMessage={setMessage}
              onChanged={() => { qc.invalidateQueries({ queryKey: ['motosim'] }) }}
            />
          ) : (
            <div className="ms-card ms-welcome">
              <h2>작업 폴더 설정</h2>
              <p>왼쪽 트리에서 Zone을 고르면 Cell 정보와 업로드 히스토리가 나옵니다.<br />트리 옆 숫자는 그 공정 아래에 올라간 Cell 파일 수입니다.</p>
            </div>
          )}
        </section>
      </div>

      {transfer && (
        <TransferOverlay
          t={transfer}
          onMinimize={(m) => setTransfer({ ...transfer, minimized: m })}
          onCancel={() => broker('job.cancel', { jobId: transfer.jobId })}
          onClose={() => setTransfer(null)}
          onOpenFolder={(p) => broker('cell.openFolder', { path: p })}
          onRun={(p) => { setTransfer(null); launch(p) }}
        />
      )}
      {dialog === 'hub' && <MotosimHubDialog onClose={() => setDialog(null)} onOpenZone={(id) => { setDialog(null); openTo(id) }} favorites={favorites.data ?? []} zones={idx.zones} zonePath={idx.zonePath} motosim={status.data} />}
      {dialog === 'admin' && <MotosimAdminCenter onClose={() => setDialog(null)} onOpenZone={(id) => { setDialog(null); openTo(id) }} />}
      {dialog === 'version' && (
        <VersionDialog status={status.data} selected={selectedInstall?.path ?? null} custom={runPath && !status.data?.installs.some((i) => i.path === runPath) ? runPath : null}
          onSelect={(p) => { setRunPath(p); setDialog(null) }} onClose={() => setDialog(null)} onError={(e) => setMessage({ title: '버전 변경', body: e })} />
      )}
      {message && (
        <MsDialog title={message.title} onClose={() => setMessage(null)} small footer={<button className="ms-btn blue" onClick={() => setMessage(null)}>확인</button>}>
          <p className="ms-msg">{message.body}</p>
        </MsDialog>
      )}
    </div>
  )
}

function ZoneDetail({ zone, path, favorite, busy, running, onTransfer, onLaunch, onMessage, onChanged }: {
  zone: MotosimZone; path: string[]; favorite: boolean; busy: boolean; running: boolean
  onTransfer(t: Transfer | null | ((t: Transfer | null) => Transfer | null)): void; onLaunch(vcl: string): void
  onMessage(m: { title: string; body: ReactNode }): void; onChanged(): void
}) {
  const qc = useQueryClient()
  const files = useQuery({ queryKey: ['motosim', 'files', zone.id], queryFn: () => api<MotosimCellFile[]>(`/api/motosim/zones/${zone.id}/files`) })
  const projects = useQuery({ queryKey: ['motosim', 'projects', zone.id], queryFn: () => api<MotosimProject[]>(`/api/motosim/zones/${zone.id}/projects`) })
  const [localVcl, setLocalVcl] = useState<string | null>(() => zoneVcl.get(zone.id))
  const [plan, setPlan] = useState<CellPlan | null>(null)
  const [checked, setChecked] = useState<Set<number>>(new Set())
  const [confirmDelete, setConfirmDelete] = useState(false)

  // 기억한 .vcl이 PC에 아직 있는지
  useEffect(() => {
    if (!isDesktop || !localVcl) return
    broker<{ exists: boolean }>('cell.exists', { vclPath: localVcl }).then((r) => { if (!r.exists) { zoneVcl.remove(zone.id); setLocalVcl(null) } }).catch(() => {})
  }, [zone.id, localVcl])

  const favorite$ = useMutation({
    mutationFn: () => api(`/api/motosim/favorites/${zone.id}`, { method: favorite ? 'DELETE' : 'PUT' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['motosim', 'favorites'] }),
  })
  const delete$ = useMutation({
    mutationFn: async (ids: number[]) => { for (const id of ids) await api(`/api/motosim/files/${id}/delete`, { method: 'POST' }) },
    onSuccess: () => { setChecked(new Set()); setConfirmDelete(false); onChanged() },
    onError: (e) => { setConfirmDelete(false); onMessage({ title: '삭제 실패', body: errorText(e) }) },
  })

  const pickForUpload = async () => {
    if (running) return onMessage({ title: '업로드할 수 없습니다', body: 'MotoSim이 실행 중입니다. MotoSim을 닫은 뒤 업로드해 주세요.' })
    try {
      const r = await broker<CellPlan | { canceled: true }>('cell.pickVcl')
      if (!r.canceled) setPlan(r)
    } catch (e) {
      onMessage({ title: 'Cell 확인 실패', body: errorText(e) })
    }
  }

  const upload = async (p: CellPlan, projectId: number, commitMessage: string) => {
    setPlan(null)
    const jobId = newJobId()
    const fileName = `${p.folderName}.zip`
    onTransfer({ jobId, direction: 'upload', fileName, stage: 'preparing' })
    try {
      await brokerWithToken('cell.upload', { jobId, vclPath: p.vclPath, zoneId: zone.id, projectId, commitMessage, fileName })
      zoneVcl.set(zone.id, p.vclPath)
      setLocalVcl(p.vclPath)
      onTransfer((t) => (t?.jobId === jobId ? { ...t, stage: 'success', percent: 100, message: 'NAS에 올리고 히스토리에 남겼습니다.' } : t))
      onChanged()
    } catch (e) {
      const msg = errorText(e)
      onTransfer((t) => (t?.jobId === jobId ? { ...t, stage: msg.includes('취소') ? 'cancelled' : 'failed', message: msg } : t))
    }
  }

  const download = async (f: MotosimCellFile, pick: boolean) => {
    if (!isDesktop) {
      try { await downloadFile(`/api/motosim/files/${f.id}/download`, f.fileName); await api(`/api/motosim/files/${f.id}/downloaded`, { method: 'POST' }) }
      catch (e) { onMessage({ title: '다운로드 실패', body: errorText(e) }) }
      return
    }
    const jobId = newJobId()
    onTransfer({ jobId, direction: 'download', fileName: f.fileName, stage: 'downloading', percent: 0 })
    try {
      const r = await brokerWithToken<{ canceled: boolean; folder?: string; vclPath?: string }>('cell.download',
        { jobId, cellFileId: f.id, fileName: f.fileName, cellName: zone.name, folder: pick ? 'pick' : undefined })
      if (r.canceled) return onTransfer(null)
      if (r.vclPath) { zoneVcl.set(zone.id, r.vclPath); setLocalVcl(r.vclPath) }
      onTransfer((t) => (t?.jobId === jobId ? { ...t, stage: 'success', percent: 100, message: r.vclPath ? `받아서 풀었습니다: ${r.folder}` : `받았지만 .vcl을 찾지 못했습니다: ${r.folder}`, result: { folder: r.folder, vclPath: r.vclPath, zoneId: zone.id } } : t))
    } catch (e) {
      const msg = errorText(e)
      onTransfer((t) => (t?.jobId === jobId ? { ...t, stage: msg.includes('취소') ? 'cancelled' : 'failed', message: msg } : t))
    }
  }

  const latest = files.data?.find((f) => f.isLatest)
  const nasFiles = files.data?.filter((f) => f.source === 'nas') ?? []
  const runLatest = () => (localVcl ? onLaunch(localVcl) : latest ? download(latest, false) : undefined)

  return (
    <>
      <div className="ms-cell-grid">
        <div className="ms-card ms-cell-main">
          <p className="ms-path">{path.slice(0, -1).join(' › ')}</p>
          <div className="ms-cell-title">
            <h2>{zone.name}</h2>
            <button className={`ms-star${favorite ? ' on' : ''}`} onClick={() => favorite$.mutate()} aria-pressed={favorite} title={favorite ? '즐겨찾기 해제' : '즐겨찾기'}>{favorite ? '★' : '☆'}</button>
          </div>
          <div className="ms-chips">
            <div className="ms-chip"><span>패키지 크기</span><b>{fmtBytes(zone.latest?.fileSize)}</b></div>
            <div className="ms-chip"><span>마지막 업로드 시간</span><b>{zone.latest ? fmtDateTime(zone.latest.uploadedAt) : '—'}</b></div>
          </div>
        </div>
        <div className="ms-card ms-cell-actions">
          <button className="ms-btn soft big" onClick={pickForUpload} disabled={!isDesktop || busy}
            title={isDesktop ? '' : 'Windows 앱에서만 업로드할 수 있습니다'}>⬆ Cell 업로드</button>
          <button className="ms-btn blue big" onClick={runLatest} disabled={!isDesktop || busy || (!localVcl && !latest) || (!localVcl && latest?.source !== 'nas')}>
            ▶ {localVcl ? '이 Cell 실행' : '최신 Cell 받기'}
          </button>
          <p className="ms-local">
            {!isDesktop ? 'PC 작업은 Windows 앱에서 됩니다.'
              : localVcl ? <>내 PC: <button className="ms-link" onClick={() => broker('cell.openFolder', { path: localVcl })}>{localVcl}</button></>
              : latest?.source === 'drive' ? '최신 Cell이 원본 런처 Google Drive에 있어 아직 받을 수 없습니다.'
              : '이 PC에 받은 Cell이 없습니다. 최신 Cell을 받으면 다운로드 폴더에 풉니다.'}
          </p>
          {projects.data && projects.data.length === 0 && <p className="ms-warn">이 Zone에 연결된 프로젝트가 없어 업로드할 수 없습니다(프로젝트 공정 배정 필요).</p>}
        </div>
      </div>

      <div className="ms-card ms-history">
        <div className="ms-history-head">
          <h3>히스토리 Cell 연결 <small>{files.data?.length ?? 0}건</small></h3>
          {nasFiles.length > 0 && (
            <div className="ms-history-tools">
              <label><input type="checkbox" checked={checked.size === nasFiles.length && nasFiles.length > 0}
                onChange={(e) => setChecked(e.target.checked ? new Set(nasFiles.map((f) => f.id)) : new Set())} /> 전체 선택 ({checked.size}/{nasFiles.length})</label>
              <button className="ms-btn rose sm" disabled={checked.size === 0 || delete$.isPending} onClick={() => setConfirmDelete(true)}>선택 삭제</button>
            </div>
          )}
        </div>
        {files.isLoading && <div className="ms-empty">zip 목록을 불러오는 중입니다.</div>}
        {files.error && <ErrorBox error={files.error} />}
        {files.data && files.data.length === 0 && <div className="ms-empty">연결 가능한 zip 파일이 없습니다.</div>}
        <div className="ms-history-list">
          {files.data?.map((f) => (
            <div key={f.id} className={`ms-hrow${f.isLatest ? ' newest' : ''}`}>
              <div className="ms-hrow-top">
                {f.source === 'nas' ? (
                  <input type="checkbox" aria-label={`${f.fileName} 선택`} checked={checked.has(f.id)}
                    onChange={(e) => setChecked((s) => { const n = new Set(s); if (e.target.checked) n.add(f.id); else n.delete(f.id); return n })} />
                ) : <span className="ms-cb-space" />}
                <div className="ms-hrow-main">
                  <b>{f.fileName}</b>{f.isLatest && <span className="ms-newest">Newest</span>}
                  <small>{[f.uploadedByName, f.uploadedByDepartment].filter(Boolean).join(' · ') || '—'} • {fmtDateTime(f.uploadedAt)}{f.projectCode && <> • <span title={f.projectName}>{f.projectCode}</span></>}</small>
                </div>
                <div className="ms-hrow-side">
                  {f.motosimVersion && <span className="ms-ver">ver. {f.motosimVersion}</span>}
                  <span className="ms-size">{fmtBytes(f.fileSize)}</span>
                  {f.source === 'drive' ? <span className="ms-drive" title="원본 런처의 Google Drive에 있습니다. NAS로 옮긴 뒤 받을 수 있습니다.">Drive에 있음</span> : (
                    <>
                      <button className="ms-btn outline sm" onClick={() => download(f, false)} disabled={busy}>⬇ 다운로드</button>
                      {isDesktop && <button className="ms-btn ghost sm" onClick={() => download(f, true)} disabled={busy} title="받을 폴더 고르기">…</button>}
                    </>
                  )}
                </div>
              </div>
              {f.commitMessage && <div className="ms-memo">{f.commitMessage}</div>}
            </div>
          ))}
        </div>
      </div>

      {plan && <UploadDialog plan={plan} zone={zone} projects={projects.data ?? []} onClose={() => setPlan(null)} onUpload={upload} />}
      {confirmDelete && (
        <MsDialog title="Cell 파일 삭제" small onClose={() => setConfirmDelete(false)}
          footer={<><button className="ms-btn outline" onClick={() => setConfirmDelete(false)}>취소</button>
            <button className="ms-btn rose" disabled={delete$.isPending} onClick={() => delete$.mutate([...checked])}>{delete$.isPending ? '삭제 중…' : `${checked.size}건 삭제`}</button></>}>
          <p className="ms-msg">선택한 Cell 파일 {checked.size}건을 히스토리에서 지웁니다.<br />NAS 파일은 휴지통 폴더(_trash)로 옮겨 두고, 최신 파일을 지우면 바로 이전 파일이 최신이 됩니다.</p>
        </MsDialog>
      )}
    </>
  )
}

function UploadDialog({ plan, zone, projects, onClose, onUpload }: {
  plan: CellPlan; zone: MotosimZone; projects: MotosimProject[]; onClose(): void; onUpload(p: CellPlan, projectId: number, message: string): void
}) {
  const [projectId, setProjectId] = useState<number | ''>(projects.length === 1 ? projects[0].id : '')
  const [message, setMessage] = useState('')
  const excluded = plan.fileCount - plan.includedFileCount
  return (
    <MsDialog title="커밋 메시지 작성" onClose={onClose}
      footer={<><button className="ms-btn outline" onClick={onClose}>취소</button>
        <button className="ms-btn blue" disabled={projectId === ''} onClick={() => onUpload(plan, Number(projectId), message.trim())}>업로드</button></>}>
      <p className="ms-desc">업로드 시 히스토리에 저장될 커밋 메시지를 입력해 주세요.</p>
      <dl className="ms-plan">
        <dt>Zone</dt><dd>{zone.name}</dd>
        <dt>Cell</dt><dd>{plan.folderName}.zip <small>{plan.vclPath}</small></dd>
        <dt>MotoSim</dt><dd>{plan.motosimVersion ?? '알 수 없음(VCL VERSION 없음)'}</dd>
        <dt>올릴 파일</dt><dd>{plan.includedFileCount.toLocaleString()}개 · {fmtBytes(plan.includedBytes)} <small>(전체 {plan.fileCount.toLocaleString()}개 · {fmtBytes(plan.totalBytes)})</small></dd>
        {excluded > 0 && (
          <><dt>zip에서 뺌</dt><dd>
            {plan.excludedBackupCount > 0 && <div>백업 파일 {plan.excludedBackupCount}개 (*.vcl.bak · VRC.bin.bak · *_bk_날짜.zip)</div>}
            {plan.excludedFolders.length > 0 && <div>안 쓰는 로봇 폴더: {plan.excludedFolders.join(', ')}</div>}
          </dd></>
        )}
        {plan.removedModelGroups.length > 0 && <><dt>.vcl 정리</dt><dd>안 쓰는 MODELGROUP {plan.removedModelGroups.length}개를 zip 안의 .vcl에서만 뺍니다 ({plan.removedModelGroups.join(', ')})</dd></>}
      </dl>
      <p className="ms-safe">내 PC의 Cell 폴더는 바꾸지 않습니다. 정리는 서버에 올리는 zip에만 적용됩니다.</p>
      <label className="ms-field">프로젝트
        <select value={projectId} onChange={(e) => setProjectId(e.target.value ? Number(e.target.value) : '')}>
          <option value="">{projects.length ? '프로젝트 선택' : '이 Zone에 연결된 프로젝트가 없습니다'}</option>
          {projects.map((p) => <option key={p.id} value={p.id}>{p.code} {p.name}</option>)}
        </select>
      </label>
      <label className="ms-field">커밋 메시지
        <textarea value={message} onChange={(e) => setMessage(e.target.value)} placeholder="예: 로봇 폴더 정리 후 커밋" rows={5} />
      </label>
    </MsDialog>
  )
}

function VersionDialog({ status, selected, custom, onSelect, onClose, onError }: {
  status?: MotosimStatus; selected: string | null; custom: string | null; onSelect(path: string | null): void; onClose(): void; onError(m: string): void
}) {
  const pick = async () => {
    try {
      const r = await broker<{ canceled: boolean; path?: string }>('motosim.pickExe')
      if (!r.canceled && r.path) onSelect(r.path)
    } catch (e) { onError(errorText(e)) }
  }
  return (
    <MsDialog title="MotoSim 버전 선택" small onClose={onClose} footer={<button className="ms-btn outline" onClick={onClose}>취소</button>}>
      {!status?.installs.length && <p className="ms-msg">C:\Program Files\MOTOMAN 아래에 설치된 MotoSim이 없습니다.</p>}
      <div className="ms-version-grid">
        {status?.installs.map((i) => (
          <button key={i.path} className={`ms-version${!custom && selected === i.path ? ' on' : ''}`} onClick={() => onSelect(i.path)} title={i.path}>
            MotoSim {i.label}{!custom && selected === i.path && <span>선택됨</span>}
          </button>
        ))}
      </div>
      {custom && <p className="ms-desc">사용자 지정: {custom} <button className="ms-link" onClick={() => onSelect(null)}>기본으로</button></p>}
      <button className="ms-btn outline wide" onClick={pick}>사용자 MotoSim 버전 선택</button>
    </MsDialog>
  )
}

const STAGE_LABEL: Record<Transfer['stage'], string> = {
  preparing: '준비 중', compressing: '압축 중', uploading: '업로드 중', finishing: '서버 처리 중', downloading: '다운로드 중',
  extracting: '압축 해제 중', success: '완료', failed: '오류 발생', cancelled: '취소됨',
}

function TransferOverlay({ t, onMinimize, onCancel, onClose, onOpenFolder, onRun }: {
  t: Transfer; onMinimize(m: boolean): void; onCancel(): void; onClose(): void; onOpenFolder(p: string): void; onRun(vcl: string): void
}) {
  const done = ['success', 'failed', 'cancelled'].includes(t.stage)
  const icon = t.direction === 'upload' ? '⬆' : '⬇'
  if (t.minimized) return <button className="ms-transfer-pill" onClick={() => onMinimize(false)}>{icon} Cell {t.direction} · {STAGE_LABEL[t.stage]}{t.percent != null && !done ? ` ${Math.round(t.percent)}%` : ''}</button>
  const showBar = !done && t.stage !== 'finishing' && t.stage !== 'preparing'
  return (
    <div className="ms-transfer" role="status">
      <div className="ms-transfer-head">
        <span className="ms-transfer-icon">{icon}</span>
        <div><small>CELL {t.direction.toUpperCase()}</small><b>{t.fileName}</b></div>
        {!done && <button className="ms-x" onClick={() => onMinimize(true)} aria-label="최소화">–</button>}
        {!done && <button className="ms-x rose" onClick={onCancel} aria-label="취소">×</button>}
        {done && <button className="ms-x" onClick={onClose} aria-label="닫기">×</button>}
      </div>
      <p className={`ms-transfer-stage ${t.stage}`}>
        {t.stage === 'success' ? '✔' : t.stage === 'failed' ? '!' : t.stage === 'cancelled' ? '×' : <span className="ms-spin" />} {STAGE_LABEL[t.stage]}
        {t.message && <small>{t.message}</small>}
      </p>
      {showBar && (
        <>
          <div className="ms-bar"><i style={{ width: `${t.percent ?? 0}%` }} /></div>
          <div className="ms-bar-text"><span>{t.done != null && t.total ? `${fmtBytes(t.done)} / ${fmtBytes(t.total)}` : ''}</span><span>{Math.round(t.percent ?? 0)}%</span></div>
        </>
      )}
      {t.stage === 'success' && t.result?.folder && (
        <div className="ms-transfer-actions">
          <button className="ms-btn outline sm" onClick={() => onOpenFolder(t.result!.folder!)}>폴더 열기</button>
          {t.result.vclPath && <button className="ms-btn blue sm" onClick={() => onRun(t.result!.vclPath!)}>▶ MotoSim 실행</button>}
        </div>
      )}
    </div>
  )
}

const RUN_LABEL: Record<RunEvent['stage'], string> = {
  cleaning: '이 Cell을 잡고 있는 MotoSim 프로세스 정리 중', launching: 'MotoSim 실행 중', running: 'MotoSim 실행 중',
  closing: 'MotoSim 종료 정리 중', closed: 'MotoSim을 닫았습니다', failed: 'MotoSim 실행 오류',
}

function RunBanner({ run, onClose }: { run: RunEvent; onClose(): void }) {
  const end = run.stage === 'closed' || run.stage === 'failed'
  return (
    <div className={`ms-run ${run.stage}`} role="status">
      {!end && <span className="ms-spin" />} {RUN_LABEL[run.stage]}{run.label ? ` (MotoSim ${run.label})` : ''}
      {run.stage === 'closed' && run.cleanupCount ? ` · 남은 프로세스 ${run.cleanupCount}개 정리` : ''}
      {run.message && run.stage === 'failed' && ` · ${run.message}`}
      {end && <button className="ms-x" onClick={onClose} aria-label="닫기">×</button>}
    </div>
  )
}

const LEVEL_LABEL: Record<Level, string> = { customer: '고객사', factory: '공장', booth: '부스' }

function index(t: MotosimTree) {
  const childrenOf = new Map<string, MotosimNode[]>()
  const push = (k: string, n: MotosimNode) => { const l = childrenOf.get(k); if (l) l.push(n); else childrenOf.set(k, [n]) }
  t.factories.forEach((f) => push(key('customer', f.parentId!), f))
  t.booths.forEach((b) => push(key('factory', b.parentId!), b))
  const zonesByBooth = new Map<number, MotosimZone[]>()
  t.zones.forEach((z) => { const l = zonesByBooth.get(z.boothId); if (l) l.push(z); else zonesByBooth.set(z.boothId, [z]) })
  const customers = new Map(t.customers.map((c) => [c.id, c]))
  const factories = new Map(t.factories.map((f) => [f.id, f]))
  const booths = new Map(t.booths.map((b) => [b.id, b]))
  const zones = new Map(t.zones.map((z) => [z.id, z]))
  // 상위 공정별 Cell 파일 합계
  const files = new Map<string, number>()
  const add = (k: string, n: number) => files.set(k, (files.get(k) ?? 0) + n)
  t.zones.forEach((z) => {
    const b = booths.get(z.boothId), f = b && factories.get(b.parentId!)
    add(key('booth', z.boothId), z.fileCount)
    if (f) { add(key('factory', f.id), z.fileCount); add(key('customer', f.parentId!), z.fileCount) }
  })
  return {
    childrenOf, zonesByBooth, zones, booths, factories,
    fileCount: (k: string) => files.get(k) ?? 0,
    zonePath: (z: MotosimZone) => {
      const b = booths.get(z.boothId), f = b && factories.get(b.parentId!), c = f && customers.get(f.parentId!)
      return [c?.name, f?.name, b?.name, z.name].filter((x): x is string => !!x)
    },
  }
}
