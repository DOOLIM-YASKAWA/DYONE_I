import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useState } from 'react'
import { api } from '../../api/client'
import type { MotosimActivity, MotosimHub, MotosimNotice, MotosimZone } from '../../api/motosimTypes'
import { ErrorBox, Loading } from '../../components/ui'
import {
  broker, brokerWithToken, errorText, fmtBytes, fmtDateTime, isDesktop, lastWork, newJobId, timeAgo, useBrokerEvent,
  type MotosimStatus, type SmartCellStatus, type TransferProgress,
} from './broker'
import { MsDialog } from './MsDialog'

/** 런처 허브(원본 Home.tsx LauncherHubContent): 최근 활동 → 이어서 작업 → 공지 | Smart Cell → MotoSim 다운로드 */
export function MotosimHubDialog({ onClose, onOpenZone, favorites, zones, zonePath, motosim }: {
  onClose(): void; onOpenZone(zoneId: number): void; favorites: number[]; zones: Map<number, MotosimZone>
  zonePath(z: MotosimZone): string[]; motosim?: MotosimStatus
}) {
  const hub = useQuery({ queryKey: ['motosim', 'hub'], queryFn: () => api<MotosimHub>('/api/motosim/hub') })
  const [notice, setNotice] = useState<MotosimNotice | null>(null)
  const [board, setBoard] = useState(false)
  const last = lastWork.get()
  const stat = (kind: string) => hub.data?.stats.find((s) => s.kind === kind)

  return (
    <MsDialog title="런처 허브" sub="최근 활동 · 이어서 작업 · 공지 · Smart Cell Plugin · MotoSim 다운로드" wide onClose={onClose}>
      {hub.isLoading && <Loading />}
      {hub.error && <ErrorBox error={hub.error} />}
      {hub.data && (
        <div className="ms-hub">
          <div className="ms-hub-card">
            <div className="ms-hub-card-head"><h3>최근 활동</h3><button className="ms-link" onClick={() => setBoard(true)}>전체 보기</button></div>
            <ActivityList items={hub.data.activities.slice(0, 4)} onOpenZone={onOpenZone} onOpenNotice={(id) => setNotice(hub.data.notices.find((n) => n.id === id) ?? null)} />
          </div>

          <div className="ms-hub-card">
            <div className="ms-hub-card-head"><h3>이어서 작업</h3>
              <span className="ms-hub-stat">오늘 업로드 <b>{stat('cell-upload')?.today ?? 0}</b> · 다운로드 <b>{stat('cell-download')?.today ?? 0}</b> · 30일 업로드 <b>{stat('cell-upload')?.month ?? 0}</b></span>
            </div>
            <div className="ms-launchpad">
              {last && zones.get(last.zoneId) ? (
                <button className="ms-launch-item main" onClick={() => onOpenZone(last.zoneId)}>
                  <small>마지막 작업 · {timeAgo(last.at)}</small><b>{last.zoneName}</b><span>{last.path}</span>
                </button>
              ) : <div className="ms-launch-item empty">마지막으로 연 Zone이 없습니다.</div>}
              {favorites.slice(0, 6).map((id) => {
                const z = zones.get(id)
                if (!z) return null
                return (
                  <button key={id} className="ms-launch-item" onClick={() => onOpenZone(id)}>
                    <small>★ 즐겨찾기{z.fileCount ? ` · Cell ${z.fileCount}` : ''}</small><b>{z.name}</b><span>{zonePath(z).slice(1, -1).join(' › ')}</span>
                  </button>
                )
              })}
            </div>
          </div>

          <div className="ms-hub-grid">
            <div className="ms-hub-card">
              <div className="ms-hub-card-head"><h3>공지사항</h3><small>{hub.data.notices.length}건</small></div>
              {hub.data.notices.length === 0 && <div className="ms-empty">공지가 없습니다.</div>}
              <ul className="ms-notices">
                {hub.data.notices.slice(0, 5).map((n) => (
                  <li key={n.id}><button onClick={() => setNotice(n)}><b>{n.title}</b><small>{n.authorName ?? '관리자'} · {timeAgo(n.updatedAt)}</small></button></li>
                ))}
              </ul>
            </div>
            <SmartCellTile />
          </div>

          <div className="ms-hub-grid">
            <InstallerCard motosim={motosim} />
            <div className="ms-hub-card">
              <div className="ms-hub-card-head"><h3>저장소</h3><small>NAS</small></div>
              <dl className="ms-store">
                <dt>Cell 파일</dt><dd>{hub.data.cellCount.toLocaleString()}건 · Zone {hub.data.zoneCount}곳</dd>
                <dt>NAS 용량</dt><dd>{fmtBytes(hub.data.storageBytes)}</dd>
                <dt>Drive에만 있음</dt><dd>{hub.data.driveOnlyCount}건 <small>(원본 런처 Google Drive — NAS로 옮기기 전)</small></dd>
              </dl>
            </div>
          </div>
        </div>
      )}
      {notice && (
        <MsDialog title={notice.title} sub={`${notice.authorName ?? '관리자'} · ${fmtDateTime(notice.updatedAt)}`} small onClose={() => setNotice(null)}>
          <p className="ms-msg pre">{notice.body || '(내용 없음)'}</p>
        </MsDialog>
      )}
      {board && hub.data && (
        <MsDialog title="최근 활동" sub="최근 30건" onClose={() => setBoard(false)}>
          <ActivityList items={hub.data.activities} onOpenZone={(id) => { setBoard(false); onOpenZone(id) }} onOpenNotice={(id) => setNotice(hub.data.notices.find((n) => n.id === id) ?? null)} />
        </MsDialog>
      )}
    </MsDialog>
  )
}

const KIND: Record<MotosimActivity['kind'], { label: string; tone: string }> = {
  'cell-upload': { label: 'Cell 업로드', tone: 'success' },
  'cell-download': { label: 'Cell 다운로드', tone: 'info' },
  'cell-delete': { label: 'Cell 삭제', tone: 'warning' },
  notice: { label: '공지사항', tone: 'info' },
}

export function ActivityList({ items, onOpenZone, onOpenNotice }: { items: MotosimActivity[]; onOpenZone?(id: number): void; onOpenNotice?(id: number): void }) {
  if (items.length === 0) return <div className="ms-empty">아직 활동이 없습니다.</div>
  return (
    <ul className="ms-activities">
      {items.map((a) => (
        <li key={a.id}>
          <button disabled={!(a.zoneId && onOpenZone) && !(a.kind === 'notice' && onOpenNotice)}
            onClick={() => (a.kind === 'notice' ? onOpenNotice?.(a.noticeId ?? -1) : a.zoneId && onOpenZone?.(a.zoneId))}>
            <span className={`ms-dot ${KIND[a.kind].tone}`} />
            <span className="ms-act-kind">{KIND[a.kind].label}</span>
            <span className="ms-act-main"><b>{a.summary}</b><small>{[a.zonePath, a.projectCode].filter(Boolean).join(' · ')}</small></span>
            <span className="ms-act-meta">{a.actorName}<small title={fmtDateTime(a.createdAt)}>{timeAgo(a.createdAt)}</small></span>
          </button>
        </li>
      ))}
    </ul>
  )
}

/** Smart Cell Plugin 타일(원본 SmartCellPluginTile) */
function SmartCellTile() {
  const qc = useQueryClient()
  const status = useQuery({ queryKey: ['motosim', 'smartcell'], queryFn: () => brokerWithToken<SmartCellStatus>('smartcell.status'), enabled: isDesktop })
  const notes = useQuery({ queryKey: ['motosim', 'plugin-versions'], queryFn: () => api<{ version: string; items: { content: string }[] }[]>('/api/motosim/plugins/smartcell/versions') })
  const [job, setJob] = useState<{ jobId: string; phase: string; percent?: number } | null>(null)
  const [ask, setAsk] = useState<'install' | 'close' | 'remove' | 'notes' | 'diagnosis' | null>(null)
  const [result, setResult] = useState<string | null>(null)
  const onProgress = useCallback((p: { jobId: string; phase: string; percent?: number }) => setJob((j) => (j && j.jobId === p.jobId ? { ...j, ...p } : j)), [])
  useBrokerEvent('smartcell.progress', onProgress)

  const install = async (closeMotoSim: boolean) => {
    setAsk(null)
    const jobId = newJobId()
    setJob({ jobId, phase: 'preparing' })
    try {
      const r = await brokerWithToken<{ status: string; message?: string }>('smartcell.install', { jobId, closeMotoSim })
      if (r.status === 'ask-close-motosim') { setJob(null); setAsk('close'); return }
      setResult(r.message ?? '설치했습니다.')
    } catch (e) { setResult(errorText(e)) }
    setJob(null)
    qc.invalidateQueries({ queryKey: ['motosim', 'smartcell'] })
  }
  const remove = async () => {
    setAsk(null)
    try { await broker('smartcell.uninstall'); setResult('제거했습니다.') } catch (e) { setResult(errorText(e)) }
    qc.invalidateQueries({ queryKey: ['motosim', 'smartcell'] })
  }

  const s = status.data
  const badge = !isDesktop ? ['대상 아님', 'muted'] : !s ? ['확인 중', 'muted']
    : s.state === 'ok' ? ['최신', 'amber'] : s.state === 'update-ready' ? ['업데이트 있음', 'blue'] : s.state === 'problem' ? ['확인 필요', 'rose'] : ['대상 아님', 'muted']
  const shownVersion = s?.state === 'update-ready' ? s.latestVersion : s?.installedVersion
  const noteItems = notes.data?.find((v) => v.version === shownVersion)?.items ?? []
  const PHASE: Record<string, string> = { preparing: '준비 중', downloading: '다운로드 중', verifying: '파일 확인 중', installing: '설치 중(관리자 권한 창 확인)' }

  return (
    <div className={`ms-hub-card ms-smartcell${s?.state === 'problem' ? ' problem' : ''}`}>
      <div className="ms-hub-card-head"><h3>Smart Cell Plugin</h3><span className={`ms-badge ${badge[1]}`}>{badge[0]}</span></div>
      {!isDesktop ? <p className="ms-desc">설치·업데이트는 DY ONE Windows 앱에서 됩니다.</p> : status.isLoading ? <p className="ms-desc">설치 상태를 확인하는 중입니다.</p> : s && (
        <>
          <div className="ms-versions">
            <span>현재 버전 <code>{s.installedVersion ?? '없음'}</code></span>
            {s.state === 'update-ready' && <span>→ 설치될 버전 <code className="blue">{s.latestVersion}</code></span>}
          </div>
          <p className="ms-desc">{s.message}</p>
          {job ? (
            <div className="ms-progress-line"><span className="ms-spin" /> {PHASE[job.phase] ?? job.phase}{job.percent != null && job.phase === 'downloading' ? ` ${Math.round(job.percent)}%` : ''}</div>
          ) : (
            <div className="ms-tile-actions">
              {s.state === 'update-ready' && <button className="ms-btn blue sm" onClick={() => setAsk('install')}>{s.installedVersion ? '업데이트 시작' : '설치'}</button>}
              {noteItems.length > 0 && <button className="ms-btn outline sm" onClick={() => setAsk('notes')}>변경 내역</button>}
              {s.state === 'problem' && s.latestVersion && <button className="ms-btn outline sm" onClick={() => setAsk('install')}>복구</button>}
              <button className="ms-btn ghost sm" onClick={() => setAsk('diagnosis')}>진단 보기</button>
              {s.installedVersion && <button className="ms-btn ghost sm rose-text" onClick={() => setAsk('remove')}>제거</button>}
            </div>
          )}
          {result && <p className="ms-result">{result}</p>}
        </>
      )}
      {ask === 'install' && (
        <MsDialog title="Smart Cell Plugin 설치" small onClose={() => setAsk(null)}
          footer={<><button className="ms-btn outline" onClick={() => setAsk(null)}>취소</button><button className="ms-btn blue" onClick={() => install(false)}>설치</button></>}>
          <p className="ms-msg">{s?.latestVersion} 버전({s?.channel === 'full' ? 'Full: VC++ 포함' : 'Update'})을 설치합니다. 관리자 권한 확인 창이 뜨면 "예"를 눌러 주세요.</p>
        </MsDialog>
      )}
      {ask === 'close' && (
        <MsDialog title="MotoSim을 종료할까요?" small onClose={() => setAsk(null)}
          footer={<><button className="ms-btn outline" onClick={() => setAsk(null)}>나중에</button><button className="ms-btn blue" onClick={() => install(true)}>종료하고 적용</button></>}>
          <p className="ms-msg">MotoSim이 실행 중이면 플러그인을 바꿀 수 없습니다. 저장하지 않은 작업이 있으면 먼저 저장해 주세요.</p>
        </MsDialog>
      )}
      {ask === 'remove' && (
        <MsDialog title="Smart Cell Plugin 제거" small onClose={() => setAsk(null)}
          footer={<><button className="ms-btn outline" onClick={() => setAsk(null)}>취소</button><button className="ms-btn rose" onClick={remove}>제거</button></>}>
          <p className="ms-msg">이 PC에서 Smart Cell Plugin을 제거합니다(관리자 권한 필요).</p>
        </MsDialog>
      )}
      {ask === 'notes' && (
        <MsDialog title={`변경 내역 v${shownVersion}`} small onClose={() => setAsk(null)}>
          <ul className="ms-note-items">{noteItems.map((i, n) => <li key={n}>{i.content}</li>)}</ul>
        </MsDialog>
      )}
      {ask === 'diagnosis' && s && (
        <MsDialog title="Smart Cell Plugin 진단" small onClose={() => setAsk(null)}>
          <ul className="ms-diag">{s.diagnosis.map((d) => <li key={d.label} className={d.ok ? 'ok' : 'bad'}><b>{d.ok ? '✔' : '✖'} {d.label}</b><small>{d.detail}</small></li>)}</ul>
        </MsDialog>
      )}
    </div>
  )
}

/** MotoSim 설치 파일(NAS _motosim/installers) */
function InstallerCard({ motosim }: { motosim?: MotosimStatus }) {
  const list = useQuery({ queryKey: ['motosim', 'installers'], queryFn: () => api<{ name: string; label: string; versionLabel?: string; size: number; modifiedAt: string }[]>('/api/motosim/installers') })
  const [confirm, setConfirm] = useState<string | null>(null)
  const [state, setState] = useState<{ name: string; text: string } | null>(null)
  const [jobId, setJobId] = useState<string | null>(null)
  const onProgress = useCallback((p: TransferProgress) => {
    if (p.jobId !== jobId) return
    setState((s) => s && { ...s, text: `${p.phase === 'extracting' ? '압축 해제 중' : '다운로드 중'} ${Math.round(p.percent)}%` })
  }, [jobId])
  useBrokerEvent('cell.progress', onProgress)
  const installed = new Set(motosim?.installs.map((i) => i.label))

  const start = async (name: string) => {
    setConfirm(null)
    const id = newJobId()
    setJobId(id)
    setState({ name, text: '준비 중' })
    try {
      const r = await brokerWithToken<{ canceled: boolean; setup?: string | null }>('motosim.installer', { jobId: id, name })
      setState(r.canceled ? null : { name, text: r.setup ? '설치 프로그램(setup.exe)을 실행했습니다.' : '받았습니다(setup.exe를 찾지 못함).' })
    } catch (e) { setState({ name, text: errorText(e) }) }
  }

  return (
    <div className="ms-hub-card">
      <div className="ms-hub-card-head"><h3>MotoSim 다운로드</h3><small>설치 파일</small></div>
      {list.data?.length === 0 && <div className="ms-empty">NAS에 올린 MotoSim 설치 파일이 없습니다.<br /><small>관리자가 FileStorage 루트의 _motosim/installers 폴더에 넣으면 여기에 보입니다.</small></div>}
      <div className="ms-installers">
        {list.data?.map((f, i) => (
          <button key={f.name} className={`ms-btn ${i === 0 ? 'blue' : 'outline'} installer`} disabled={!isDesktop || !!state && state.text.includes('%')}
            onClick={() => setConfirm(f.name)} title={isDesktop ? f.name : 'Windows 앱에서만 받을 수 있습니다'}>
            <b>{f.label}</b>
            <small>{fmtBytes(f.size)}{i === 0 && ' · 최신'}{f.versionLabel && installed.has(f.versionLabel) && ' · 설치됨'}</small>
          </button>
        ))}
      </div>
      {state && <p className="ms-result">{state.name}: {state.text}</p>}
      {confirm && (
        <MsDialog title={installed.has(list.data?.find((f) => f.name === confirm)?.versionLabel ?? '-') ? '다시 다운로드 확인' : 'MotoSim 다운로드 확인'} small onClose={() => setConfirm(null)}
          footer={<><button className="ms-btn outline" onClick={() => setConfirm(null)}>취소</button><button className="ms-btn blue" onClick={() => start(confirm)}>받기</button></>}>
          <p className="ms-msg">{confirm}을(를) 저장할 위치를 고른 뒤 받고, 압축을 풀어 setup.exe를 실행합니다.</p>
        </MsDialog>
      )}
    </div>
  )
}
