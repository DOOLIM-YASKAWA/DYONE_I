import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Fragment, useEffect, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { API_BASE, api, downloadFile, tokenStore } from '../../api/client'
import { ErrorBox, Loading, Modal } from '../../components/ui'
import { SpecSheet, type SheetRow } from './SpecSheet'

// ---- API 타입(서버 Modules/Spec/SpecDraftModule.cs, DyOne.Spec/Draft/SummaryDraft.cs)
type Candidate = {
  recordId: string; model: string; role: string; year: string; projectFolder: string; projectCode?: string | null
  sourceKind: 'summary' | 'drawing'; fileName: string; excerpt: string; context: string; reasons: string[]
}
/** applied: 채택·입력한 모델을 칸 값에 넣은 글(바꾸거나 취소하면 서버가 칸에서 뺀다) */
type Decision = { kind: 'candidate' | 'default' | 'custom' | 'none'; recordId?: string | null; text?: string | null; by: string; at: string; applied?: string | null }
type Slot = {
  id: string; kind: string; role: string; label: string; status: 'given' | 'existing' | 'unspecified'
  /** 사양서 지정 | 기설치 이력 | 공장 이력 | 경향 규칙 | 선정 필요 (서버 ModelPicker) */
  how: string; why: string; specModel: string
  specText: string; specPage?: number | null; candidates: Candidate[]; decision?: Decision | null
}
/** split: 공정별 값(processes 순서). 비어 있으면 value가 모든 공정에 같은 값 → 한 칸으로 합침 */
type Row = {
  key: string; value: string; split?: string[] | null; note: string; source?: string[] | null; pages: number[]; slots: Slot[]
  /** 참고 사항(화면만, pptx에는 안 들어감): 초안이 덧붙인 확인 안내·계산·기준 설명 */
  remark?: string
  /** 표에서 뺀 줄(pptx에도 안 들어감, 표 아래 '뺀 칸'에서 되살림) */
  hidden?: boolean
  /** 공정 칸 병합 폭(스프레드시트처럼): span[i]칸 차지, 덮인 칸 0 */
  span?: number[] | null
  /** 사용자가 추가한 줄 */
  custom?: boolean
  /** 표에서 직접 고친 줄(초안을 다시 만들어도 고친 글 유지) */
  edited?: boolean; editedBy?: string | null; editedAt?: string | null
}
/** 표 칸 고치기: value(한 칸) | split(공정별 칸, index) | note(기타 사항) */
type CellEdit = { key: string; field: 'value' | 'split' | 'note' | 'remark' | 'reset' | 'hide' | 'show' | 'merge' | 'unmerge' | 'merge-right' | 'merge-range' | 'unmerge-cell' | 'addrow' | 'deleterow'; index?: number; text: string }
type Process = { no: number; label: string; code: string; spray: number; opener: number; page: number; title: string }
type Check = { text: string; pages: number[] }
type Draft = {
  fileId: number; fileName: string; createdAt: string; engine: 'ai' | 'rule'; engineNote?: string | null
  context: { plant: string; process: string; parts: string[] }
  conditions?: {
    plant: string; process: string; part: string; area: string
    paint: string; paintNote: string; charge: string; chargeNote: string; roles: string[]
  } | null
  title: string; total: string; colHeader: string; rows: Row[]; etc: Check[]; pcs: Check[]
  /** 제공 항목(스페어·공구·키락·로봇 커버·교육 — 사양서 문장) */
  supply?: Check[]
  /** 표 위 그림(사양서 레이아웃·전체 시스템 구성도 영역) — pptx 2장 표 위에도 같은 그림 */
  layoutImage?: { page: number; clip: number[] } | null
  processes?: Process[] | null
  /** 사양서 공정별 설비사양 표로 열을 나눈 초안(여러 공정 프로젝트) */
  processTable?: boolean
  scheduleImage?: { page: number } | null; scheduleNote: string
}
type PageInfo = { page: number; chars: number; head: string }
type CompareRow = { key: string; thisValue: string; pastValue: string; pastNote: string }
type DraftResponse = {
  draft?: Draft | null; aiConfigured: boolean; isPdf: boolean
  equipmentCount: number; equipmentBuiltAt?: string | null; equipmentRunning: boolean
  pages: PageInfo[]; unusedPages: number[]
  similar?: { id: string; projectFolder: string; year: string; summaryFile?: string | null; rows: CompareRow[] } | null
}
type EquipStatus = { count: number; projects: number; builtAt?: string | null; running: boolean; done: number; total: number; lastError?: string | null; canRefresh?: boolean }

/** 오른쪽 근거 패널에 띄울 것: 사양서 쪽 또는 모델 자리 */
/** marks: 그 쪽에서 노란색으로 칠할 글(쪽 번호를 누른 칸의 값·원문 조각) */
type Focus = { type: 'page'; page: number; marks?: string[] } | { type: 'slot'; slotId: string } | null

const CIRCLED = '①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮'
const day = (v?: string | null) => (v ? new Date(v).toLocaleString('ko-KR', { dateStyle: 'short', timeStyle: 'short' }) : '-')

/** 선택된 후보: 결정 없음·기본 사양 → 1순위 후보, 채택 → 그 후보, 직접 입력·선정 필요 → 없음 (서버 DraftBuilder.Chosen과 같은 규칙) */
const chosen = (s: Slot) =>
  s.status === 'given' ? undefined : s.decision == null ? s.candidates[0] : s.decision.kind === 'candidate' || s.decision.kind === 'default' ? s.candidates.find((c) => c.recordId === s.decision!.recordId) : undefined

/** 근거 번호: 고른 후보의 프로젝트·출처마다 하나, 표 순서대로 (pptx '모델 제안 근거' 장과 같은 번호) */
function numbering(d: Draft) {
  const map = new Map<string, string>()
  for (const s of d.rows.flatMap((r) => r.slots)) {
    const c = chosen(s)
    if (!c) continue
    const k = `${c.projectFolder}|${c.sourceKind}|${c.fileName}`
    if (!map.has(k) && map.size < CIRCLED.length) map.set(k, CIRCLED[map.size])
  }
  return (c: Candidate) => map.get(`${c.projectFolder}|${c.sourceKind}|${c.fileName}`) ?? ''
}

/** 초안 글 표시(**굵게** {{파랑}} [[빨강]] <<초록>>) → 화면 */
function Marked({ text }: { text: string }) {
  const out: ReactNode[] = []
  let bold = false, tone = '', buf = '', key = 0
  const flush = () => {
    if (!buf) return
    const el = <span key={key++} className={tone}>{buf}</span>
    out.push(bold ? <b key={key++}>{el}</b> : el)
    buf = ''
  }
  for (let i = 0; i < text.length; i++) {
    const two = text.slice(i, i + 2)
    if (['**', '{{', '}}', '[[', ']]', '<<', '>>'].includes(two)) {
      flush()
      if (two === '**') bold = !bold
      else tone = two === '{{' ? 'sd-blue' : two === '[[' ? 'sd-red' : two === '<<' ? 'sd-green' : ''
      i++
      continue
    }
    buf += text[i]
  }
  flush()
  return <>{out}</>
}

/**
 * 사양정리 초안(영업부 V5 양식): 값은 이번 고객 사양서에서만(파란 쪽 번호를 누르면 원문),
 * 모델이 '기설치와 동일'이거나 정해지지 않은 칸은 비슷한 과거 프로젝트 모델 중 1순위를 기본 사양으로 칸에 넣고 '참고'에서 다른 후보·직접 입력.
 */
export function SpecSummaryDraft({ projectId, fileId }: { projectId: number; fileId: number }) {
  const qc = useQueryClient()
  const key = ['spec-draft', projectId, fileId]
  const q = useQuery({ queryKey: key, queryFn: () => api<DraftResponse>(`/api/projects/${projectId}/spec-draft?fileId=${fileId}`) })
  const [tab, setTab] = useState<'draft' | 'compare'>('draft')
  const [focus, setFocus] = useState<Focus>(null)
  // 오른쪽 사양서 패널: 쪽 번호를 누르면 펴지고, 접으면 표가 화면 전체를 쓴다(2026-10-28 사용자)
  const [sideOpen, setSideOpen] = useState(false)
  const openFocus = (f: Focus) => { setFocus(f); if (f) setSideOpen(true) }
  const [pptxBusy, setPptxBusy] = useState(false)
  const [pptxErr, setPptxErr] = useState<unknown>(null)
  /** '참고' 팝업을 연 줄(Robot·FGP·Panel …) */
  const [suggestRow, setSuggestRow] = useState<string | null>(null)

  const make = useMutation({
    mutationFn: () => api<DraftResponse>(`/api/projects/${projectId}/spec-draft?fileId=${fileId}`, { method: 'POST' }),
    onSuccess: (data) => { qc.setQueryData(key, data); setFocus(null) },
  })
  const decide = useMutation({
    mutationFn: (body: { slotId: string; kind: string; recordId?: string; text?: string }) =>
      api<Draft>(`/api/projects/${projectId}/spec-draft/decision?fileId=${fileId}`, { method: 'PUT', json: body }),
    onSuccess: (draft) => qc.setQueryData<DraftResponse>(key, (old) => (old ? { ...old, draft } : old)),
  })
  const edit = useMutation({
    mutationFn: (body: CellEdit) =>
      api<Draft>(`/api/projects/${projectId}/spec-draft/cell?fileId=${fileId}`, { method: 'PUT', json: body }),
    onSuccess: (draft) => qc.setQueryData<DraftResponse>(key, (old) => (old ? { ...old, draft } : old)),
  })

  // 스프레드시트 편집(엑셀처럼): 저장하면 시트 모양 그대로 초안에
  const [sheetMode, setSheetMode] = useState(false)
  const saveSheet = useMutation({
    mutationFn: (rows: SheetRow[]) =>
      api<Draft>(`/api/projects/${projectId}/spec-draft/sheet?fileId=${fileId}`, { method: 'PUT', json: { rows } }),
    onSuccess: (draft) => { qc.setQueryData<DraftResponse>(key, (old) => (old ? { ...old, draft } : old)); setSheetMode(false) },
  })

  const data = q.data
  const d = data?.draft
  const pptx = async () => {
    setPptxBusy(true); setPptxErr(null)
    try { await downloadFile(`/api/projects/${projectId}/spec-draft/pptx?fileId=${fileId}`, '사양정리 초안.pptx') }
    catch (e) { setPptxErr(e) } finally { setPptxBusy(false) }
  }

  if (q.isLoading) return <Loading />
  if (q.error) return <ErrorBox error={q.error} />
  if (!data) return null

  const slots = d?.rows.flatMap((r) => r.slots) ?? []
  const fromSpec = d?.rows.filter((r) => r.value).length ?? 0
  const pending = slots.filter((s) => (s.decision == null || s.decision.kind === 'default') && s.candidates.length > 0).length
  const needed = slots.filter((s) => s.decision?.kind === 'none' || (s.decision == null && s.candidates.length === 0)).length
  const no = d ? numbering(d) : () => ''
  const focusSlot = focus?.type === 'slot' ? slots.find((s) => s.id === focus.slotId) : undefined

  return (
    <section className="spec-section sd">
      <EquipmentBar status={data} />
      <div className="sd-head">
        <div>
          <h3>사양정리 초안 <small>영업부 V5 양식 · 값은 이번 사양서에서만</small></h3>
          {d ? (
            <div className="sd-counts">
              <span className={`badge mini ${d.engine === 'ai' ? 'blue' : 'amber'}`}>{d.engine === 'ai' ? '사내 AI 작성' : '규칙 작성'}</span>
              <span><b className="sd-blue">사양서 값</b> {fromSpec}/{d.rows.length}칸</span>
              <span><b className="sd-green">기본 사양·채택</b> {slots.length - needed}건{pending > 0 ? ` (기본 사양 확인 대기 ${pending})` : ''}</span>
              <span><b className="sd-red">선정 필요</b> {needed}건</span>
              <small>{day(d.createdAt)} 작성</small>
            </div>
          ) : (
            <p className="spec-hint">아직 초안이 없습니다. '초안 만들기'를 누르면 이 사양서로 사양정리 초안을 만듭니다.</p>
          )}
        </div>
        <div className="actions">
          <button className={d ? 'secondary' : 'primary'} disabled={make.isPending} onClick={() => make.mutate()}>
            {make.isPending ? '만드는 중…' : d ? '초안 다시 만들기' : '초안 만들기'}
          </button>
          {d && <button className="primary" disabled={pptxBusy} onClick={pptx}>{pptxBusy ? 'pptx 만드는 중…' : 'pptx 받기'}</button>}
        </div>
      </div>
      {make.error && <ErrorBox error={make.error} />}
      {pptxErr != null && <ErrorBox error={pptxErr} />}
      {decide.error && <ErrorBox error={decide.error} />}
      {edit.error && <ErrorBox error={edit.error} />}
      {d?.engineNote && <div className={`notice ${d.engine === 'rule' ? 'spec-warn' : ''}`}>{d.engineNote}</div>}

      {d && (
        <>
          <div className="sd-legend">
            <span><i className="sd-dot blue" />사양서 값 (쪽 번호를 누르면 원문)</span>
            <span><i className="sd-dot green" />기본 사양(과거 프로젝트 1순위, '참고'로 근거·다른 후보)</span>
            <span><i className="sd-dot red" />선정·확인 필요</span>
          </div>
          <div className="project-tabs sd-tabs">
            <button className={tab === 'draft' ? 'active' : ''} onClick={() => setTab('draft')}>초안</button>
            <button className={tab === 'compare' ? 'active' : ''} onClick={() => setTab('compare')}>
              사양서와 비교 {data.unusedPages.length > 0 && <small>안 쓴 쪽 {data.unusedPages.length}</small>}
            </button>
            {tab === 'draft' && !sheetMode && (
              <button className="sd-side-toggle sd-sheet-open" onClick={() => setSheetMode(true)} title="표를 엑셀처럼 편집합니다(셀 병합·행 추가/삭제·복사/붙여넣기)">
                ▦ 스프레드시트로 편집
              </button>
            )}
            <button className="sd-side-toggle" onClick={() => setSideOpen((o) => !o)} title="오른쪽 사양서 원문 패널을 펴거나 접습니다">
              {sideOpen ? '사양서 접기 ⟩' : '⟨ 사양서 펴기'}
            </button>
          </div>
          <div className={`sd-split${sideOpen ? '' : ' closed'}`}>
            <div className="sd-main">
              {tab === 'draft'
                ? <>
                  {d.layoutImage && <LayoutPicture projectId={projectId} fileId={fileId} stamp={d.createdAt} page={d.layoutImage.page}
                    onPage={(page) => openFocus({ type: 'page', page })} />}
                  {sheetMode && (
                    <>
                      {saveSheet.error && <ErrorBox error={saveSheet.error} />}
                      <SpecSheet rows={d.rows} busy={saveSheet.isPending}
                        columns={(d.processes?.length ?? 0) >= 2 ? d.processes!.map((p) => (d.processTable ? (p.code || p.label || p.title) : p.title)) : [d.colHeader || '사양']}
                        onSave={(rows) => saveSheet.mutateAsync(rows)} onCancel={() => setSheetMode(false)}
                        onPage={(rowKey, page) => { const r = d.rows.find((x) => x.key === rowKey); openFocus({ type: 'page', page, marks: r ? rowMarks(r, page) : undefined }) }} />
                    </>
                  )}
                  {!sheetMode && <DraftView d={d} onFocus={openFocus} busy={edit.isPending || make.isPending}
                    onSuggest={setSuggestRow}
                    onEdit={(e) => edit.mutateAsync(e)}
                    // 되돌리기: 직접 고침을 풀고 초안을 다시 만들어 그 줄을 사양서 값으로(다른 줄의 고친 글·채택은 유지)
                    onReset={async (rowKey) => { await edit.mutateAsync({ key: rowKey, field: 'reset', text: '' }); await make.mutateAsync() }} />}
                </>
                : <CompareView data={data} onPage={(page) => openFocus({ type: 'page', page })} />}
            </div>
            {sideOpen && <aside className="sd-side">
              <button className="sd-side-close" onClick={() => setSideOpen(false)} title="사양서 패널 접기">접기 ✕</button>
              {focus == null && <div className="sd-side-empty">참고 사항 칸의 쪽 번호를 누르면<br />여기에 사양서 원문이 열립니다.<br />사양서에 없어 기본 사양으로 넣은 칸은 '참고'를 누르면 후보·근거를 보고 다른 후보로 바꿀 수 있습니다.</div>}
              {focus?.type === 'page' && <PageView projectId={projectId} fileId={fileId} page={focus.page} marks={focus.marks} pages={data.pages} isPdf={data.isPdf} onPage={(page) => setFocus({ type: 'page', page })} />}
              {focusSlot && (
                <SlotView slot={focusSlot} no={no} busy={decide.isPending}
                  onPage={(page) => setFocus({ type: 'page', page })}
                  onDecide={(kind, recordId, text) => decide.mutate({ slotId: focusSlot.id, kind, recordId, text })} />
              )}
            </aside>}
          </div>
          {suggestRow && (() => {
            // 칸의 '참고' 팝업: 그 줄의 모델 자리마다 후보·근거(1순위는 기본 사양으로 이미 칸에 있음), 다른 후보를 채택하면 칸이 바뀌고 팝업이 닫힌다
            const row = d.rows.find((r) => r.key === suggestRow)
            const open = row?.slots.filter((s) => s.status !== 'given') ?? []
            return (
              <Modal title={`${suggestRow} 참고`} onClose={() => setSuggestRow(null)}>
                <div className="sd-suggest">
                  <p className="spec-hint">사양서에 없어 1순위 후보를 기본 사양으로 칸에 넣어 두었습니다. 근거 파일을 열어 확인하고, 다른 후보의 '이 모델 채택'을 누르면 칸이 그 모델로 바뀝니다.</p>
                  {open.length === 0 && <div className="empty">이 칸에는 참고할 후보가 없습니다.</div>}
                  {open.map((s) => (
                    <SlotView key={s.id} slot={s} no={no} busy={decide.isPending}
                      onPage={(page) => { setSuggestRow(null); openFocus({ type: 'page', page }) }}
                      onDecide={(kind, recordId, text) => decide.mutate({ slotId: s.id, kind, recordId, text },
                        { onSuccess: () => { if (kind === 'candidate' || kind === 'custom') setSuggestRow(null) } })} />
                  ))}
                </div>
              </Modal>
            )
          })()}
        </>
      )}
    </section>
  )
}

function EquipmentBar({ status }: { status: DraftResponse }) {
  const qc = useQueryClient()
  const live = useQuery({
    queryKey: ['spec-equipment-status'],
    queryFn: () => api<EquipStatus>('/api/spec/equipment/status'),
    refetchInterval: (query) => (query.state.data?.running ? 2000 : false),
  })
  const refresh = useMutation({
    mutationFn: () => api('/api/spec/equipment/refresh', { method: 'POST' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['spec-equipment-status'] }),
  })
  const s = live.data
  const count = s?.count ?? status.equipmentCount
  return (
    <div className="sd-equip">
      <span>
        <b>설비 이력</b> {count.toLocaleString()}건
        {s?.projects ? ` · NAS 프로젝트 ${s.projects.toLocaleString()}개` : ''} · {day(s?.builtAt ?? status.equipmentBuiltAt)} 기준
        {s?.running && ` · 읽는 중 ${s.done}/${s.total}`}
        {s?.lastError && <span className="sd-red"> · 오류: {s.lastError}</span>}
      </span>
      <small>과거 사양정리 표와 도면 제목(ROBOT MANIPULATOR FOR MPX-3500 …)에서 모은 로봇·도장기·FGP·CCV 모델. 기본 사양·참고 후보에 씁니다.</small>
      {/* 매일 새벽 Worker가 새로 만든다. 즉시 다시 읽기는 관리자만 */}
      {s?.canRefresh && (
        <button className="text-button" disabled={s.running || refresh.isPending} onClick={() => refresh.mutate()}>
          {s.running ? '읽는 중…' : count === 0 ? 'NAS에서 설비 이력 읽기' : '다시 읽기'}
        </button>
      )}
    </div>
  )
}

/** 근거 줄 "문구 ← 사양서 원문 (p7)" → 문구·원문·쪽 */
function parseSource(s: string) {
  const m = /^(.*?) ← ([\s\S]*?)\s*\(p([\d,p]+)\)\s*$/.exec(s)
  if (!m) return null
  return { label: m[1].trim(), text: m[2].replace(/^…|…$/g, '').trim(), pages: m[3].split(/,p?/).map(Number).filter((n) => n > 0) }
}

/** 칸 쪽 번호를 눌렀을 때 그 쪽에서 칠할 글: 칸 값(공정별 칸 포함) + 그 쪽 근거 원문 */
function rowMarks(r: Row, p: number) {
  const src = (r.source ?? []).map(parseSource).filter((x) => x && x.pages.includes(p)).map((x) => x!.text)
  return [r.value, ...(r.split ?? []), ...src].filter((t) => t && t.trim().length > 0)
}

/** 기타 사항 줄마다 근거 쪽 번호(근거 줄의 문구가 그 줄과 같을 때). 누르면 그 쪽에서 원문 자리를 칠한다 */
function NoteLines({ r, onPage }: { r: Row; onPage(p: number, marks?: string[]): void }) {
  const src = (r.source ?? []).map(parseSource).filter((x): x is NonNullable<ReturnType<typeof parseSource>> => x != null)
  return (
    <>
      {r.note.split('\n').filter((l) => l.trim()).map((line, i) => {
        const plain = plainText(line).trim()
        const hits = src.filter((x) => plainText(x.label).trim() === plain || plain.startsWith(plainText(x.label).trim()))
        const pages = [...new Set(hits.flatMap((x) => x.pages))].sort((a, b) => a - b)
        return (
          <div key={i} className="sd-note-line">
            <Marked text={line} />
            {pages.length > 0 && <Pages pages={pages} onPage={(p) => onPage(p, hits.filter((x) => x.pages.includes(p)).map((x) => x.text))} />}
          </div>
        )
      })}
    </>
  )
}

function Pages({ pages, onPage }: { pages: number[]; onPage(p: number): void }) {
  return (
    <>
      {pages.map((p) => (
        <button key={p} className="sd-page" onClick={() => onPage(p)} title={`사양서 ${p}쪽 보기`}>p{p}</button>
      ))}
    </>
  )
}


/** 표시용 표시(**·{{·[[·<<)를 뺀 글 — 고치기 입력란에 쓴다 */
const plainText = (s: string) => s.replace(/\*\*|\{\{|\}\}|\[\[|\]\]|<<|>>/g, '')

/**
 * 표 칸 직접 고치기(스프레드시트처럼): 칸을 두 번 누르거나 골라서 Enter·F2 → 칸 안에서 바로 고친다.
 * Enter 저장(Shift+Enter 줄바꿈), Esc 취소, 칸 밖을 누르면 저장. 저장하면 그 줄은 '직접 고침' — 초안을 다시 만들어도 고친 글 그대로.
 * editing을 넘기면 부모(표)가 고치기 시작·끝을 정한다.
 */
function EditableCell({ text, empty, busy, onSave, children, view, editing, onDone }: {
  text: string; empty?: ReactNode; busy: boolean; onSave(text: string): Promise<unknown>; children?: ReactNode
  /** 보기 모양을 바꿀 때(기타 사항 줄마다 쪽 번호) — 없으면 글 그대로 */
  view?: ReactNode
  editing?: boolean
  onDone?(): void
}) {
  const [draft, setDraft] = useState<string | null>(null)
  const done = useRef(false)
  useEffect(() => {
    if (editing) { done.current = false; setDraft(plainText(text)) } else setDraft(null)
  }, [editing, text])
  const finish = async (keep: boolean) => {
    if (done.current) return
    done.current = true
    if (keep && draft != null && draft.trim() !== plainText(text).trim()) await onSave(draft)
    setDraft(null)
    onDone?.()
  }
  if (draft != null)
    return (
      <textarea className="sd-inline-edit" autoFocus value={draft} disabled={busy}
        rows={Math.min(8, Math.max(1, draft.split('\n').length))}
        onChange={(e) => setDraft(e.target.value)}
        onFocus={(e) => e.currentTarget.select()}
        onBlur={() => void finish(true)}
        onKeyDown={(e) => {
          e.stopPropagation()
          if (e.key === 'Escape') void finish(false)
          if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); void finish(true) }
        }} />
    )
  return (
    <div className="sd-cell">
      {text ? view ?? <Marked text={text} /> : empty}
      {children}
    </div>
  )
}

/**
 * 칸의 '참고' 버튼: 사양서에 모델·값이 없어 1순위 후보를 기본 사양으로 넣은 자리(로봇·도장기·FGP·세정장치·과거 사양정리 참고)가 있으면 칸 안에 보인다.
 * 누르면 팝업에서 후보·근거를 보고 다른 후보로 바꾼다. 사람이 다 정했으면 '확정'.
 */
function RefButton({ r, onClick }: { r: Row; onClick(): void }) {
  const open = r.slots.filter((s) => s.status !== 'given')
  if (open.length === 0) return null
  const done = open.filter((s) => s.decision && s.decision.kind !== 'none' && s.decision.kind !== 'default').length
  const count = open.reduce((n, s) => n + s.candidates.length, 0)
  const label = done === open.length ? '참고 · 확정' : count > 0 ? `참고 ${count}건` : '참고 없음 — 직접 입력'
  return <button className={`sd-suggest-btn${done === open.length ? ' done' : ''}`} onClick={onClick}>{label}</button>
}

type Sel = { y: number; c1: number; c2: number }
type SheetAction = 'edit' | 'merge' | 'unmerge' | 'mergeRow' | 'addRow' | 'deleteRow' | 'page' | 'ref' | 'reset'

/** 표 위 도구 막대(스프레드시트처럼): 고른 칸과 할 수 있는 일 */
function SheetToolbar({ sel, rows, n, multi, busy, colName, onAction }: {
  sel: Sel | null; rows: Row[]; n: number; multi: boolean; busy: boolean; colName(c: number): string; onAction(a: SheetAction): void
}) {
  const r = sel ? rows[sel.y] : null
  const split = !!r && multi && r.split?.length === n
  const sp = split ? (r!.span?.length === n ? r!.span! : Array(n).fill(1)) as number[] : null
  const b = (a: SheetAction, label: string, title: string, disabled = false) => (
    <button className="secondary" disabled={busy || !sel || disabled} title={title} onClick={() => onAction(a)}>{label}</button>
  )
  return (
    <div className="sd-toolbar">
      <span className="sd-toolbar-sel">{sel && r ? <><b>{r.key}</b> · {sel.c1 === sel.c2 ? colName(sel.c1) : `${colName(sel.c1)} ~ ${colName(sel.c2)}`}</> : '칸을 눌러 고르세요 (Shift = 범위, 두 번 누르기·Enter = 고치기, 우클릭 = 메뉴)'}</span>
      {b('edit', '고치기', '고른 칸을 고칩니다 (Enter·F2·두 번 누르기)')}
      {b('merge', '셀 병합', '고른 공정 칸들을 한 칸으로 (Shift로 범위 선택)', !split || !sel || sel.c2 >= n || sel.c2 <= sel.c1)}
      {b('unmerge', split ? '병합 풀기' : '칸 나누기', '합친 칸을 나눕니다 — 글은 첫 칸에만', !multi || !sel || sel.c1 >= n || (split && (sp![sel.c1] ?? 1) <= 1))}
      {b('addRow', '+ 행 추가', '고른 줄 아래에 새 줄')}
      {b('deleteRow', '행 삭제', '추가한 줄은 지우고, 양식 줄은 표·pptx에서 뺍니다')}
      {b('page', '원문 보기', '이 줄의 사양서 근거 쪽', !r || r.pages.length === 0)}
    </div>
  )
}

/** 우클릭 메뉴 */
function SheetMenu({ x, y, items, onPick, onClose }: {
  x: number; y: number; items: { a: SheetAction; label: string; disabled?: boolean }[]; onPick(a: SheetAction): void; onClose(): void
}) {
  useEffect(() => {
    const close = () => onClose()
    window.addEventListener('mousedown', close)
    window.addEventListener('scroll', close, true)
    return () => { window.removeEventListener('mousedown', close); window.removeEventListener('scroll', close, true) }
  }, [onClose])
  return (
    <div className="sd-menu" style={{ left: Math.min(x, window.innerWidth - 220), top: Math.min(y, window.innerHeight - 320) }}
      onMouseDown={(e) => e.stopPropagation()}>
      {items.map((it) => (
        <button key={it.a} disabled={it.disabled} onClick={() => onPick(it.a)}>{it.label}</button>
      ))}
    </div>
  )
}

function DraftView({ d, onFocus, busy, onEdit, onReset, onSuggest }: {
  d: Draft; onFocus(f: Focus): void; busy: boolean
  onEdit(e: CellEdit): Promise<unknown>; onReset(rowKey: string): Promise<unknown>; onSuggest(rowKey: string): void
}) {
  const page = (p: number, marks?: string[]) => onFocus({ type: 'page', page: p, marks })
  // 사양서가 공정을 나눴으면(1공정 프론트 도어 FRT / 2공정 리어 도어 RR) 값 열을 공정별로
  const multi = (d.processes?.length ?? 0) >= 2
  const head = d.colHeader.replace(/\s*\([^)]*\/[^)]*\)\s*$/, '').trim()
  // 공정별 설비사양 표(여러 공정)는 공정 이름이 곧 열 제목
  const colTitle = (p: Process) => (d.processTable ? (p.code || p.label || p.title) : !head ? p.title : `${head} (${p.code || p.title})`)
  // ---- 스프레드시트처럼 칸 고르기 ----
  const visible = d.rows.filter((r) => !r.hidden)
  const n = multi ? d.processes!.length : 1
  /** 고른 칸: 줄 y, 열 c1~c2(0..n-1 공정 칸, n 기타 사항, n+1 참고 사항) — 범위는 한 줄 안에서만 */
  const [sel, setSel] = useState<Sel | null>(null)
  const [editing, setEditing] = useState<{ y: number; c: number } | null>(null)
  const [menu, setMenu] = useState<{ x: number; y: number } | null>(null)
  const gridRef = useRef<HTMLDivElement>(null)
  const colName = (c: number) => (c < n ? (multi ? colTitle(d.processes![c]) : '값') : c === n ? '기타 사항' : '참고 사항')
  const inSel = (y: number, c: number, c2 = c) => !!sel && sel.y === y && c <= sel.c2 && c2 >= sel.c1
  // 줄 y에서 열 c를 덮는 칸의 시작(병합·한 칸 줄)
  const anchorOf = (y: number, c: number) => {
    const r = visible[y]
    if (c >= n) return { c1: c, c2: c }
    if (!(multi && r.split?.length === n)) return { c1: 0, c2: n - 1 }
    const sp = r.span?.length === n ? r.span : null
    let a = c
    while (sp && a > 0 && sp[a] === 0) a--
    return { c1: a, c2: a + (sp ? Math.max(1, sp[a]) : 1) - 1 }
  }
  const select = (y: number, c1: number, c2: number, extend: boolean) => {
    setMenu(null)
    if (extend && sel && sel.y === y && c1 < n && sel.c1 < n) setSel({ y, c1: Math.min(sel.c1, c1), c2: Math.max(sel.c2, c2) })
    else setSel({ y, c1, c2 })
    gridRef.current?.focus({ preventScroll: true })
  }
  const onGridKey = (e: React.KeyboardEvent) => {
    if (!sel || editing) return
    const move = (dy: number, dc: number) => {
      e.preventDefault()
      const y = Math.max(0, Math.min(visible.length - 1, sel.y + dy))
      const c = dc > 0 ? sel.c2 + 1 : dc < 0 ? sel.c1 - 1 : sel.c1
      const cc = Math.max(0, Math.min(n + 1, c))
      const a = anchorOf(y, cc)
      if (e.shiftKey && dy === 0 && cc < n && sel.c1 < n) setSel({ y, c1: Math.min(sel.c1, a.c1), c2: Math.max(sel.c2, a.c2) })
      else setSel({ y, ...a })
    }
    if (e.key === 'ArrowDown') move(1, 0)
    else if (e.key === 'ArrowUp') move(-1, 0)
    else if (e.key === 'ArrowRight' || (e.key === 'Tab' && !e.shiftKey)) move(0, 1)
    else if (e.key === 'ArrowLeft' || (e.key === 'Tab' && e.shiftKey)) move(0, -1)
    else if (e.key === 'Enter' || e.key === 'F2') { e.preventDefault(); setEditing({ y: sel.y, c: sel.c1 }) }
    else if (e.key === 'Escape') { setSel(null); setMenu(null) }
  }
  // 도구 막대·우클릭 메뉴 동작
  const act = async (a: SheetAction) => {
    if (!sel) return
    const r = visible[sel.y]
    switch (a) {
      case 'edit': setEditing({ y: sel.y, c: sel.c1 }); break
      case 'merge': await onEdit({ key: r.key, field: 'merge-range', index: sel.c1, text: String(sel.c2) }); break
      case 'unmerge':
        if (!(r.split?.length === n)) await onEdit({ key: r.key, field: 'unmerge', index: n, text: '' })
        else await onEdit({ key: r.key, field: 'unmerge-cell', index: sel.c1, text: '' })
        setSel({ y: sel.y, c1: sel.c1, c2: sel.c1 })
        break
      case 'mergeRow': await onEdit({ key: r.key, field: 'merge', text: '' }); setSel({ y: sel.y, c1: 0, c2: n - 1 }); break
      case 'addRow': {
        const name = prompt('새 줄 이름 (예: 오프너 클리너)')?.trim()
        if (name) await onEdit({ key: r.key, field: 'addrow', text: name })
        break
      }
      case 'deleteRow':
        if (confirm(`${r.key} 줄을 ${r.custom ? '지울' : '표에서 뺄'}까요?${r.custom ? '' : ' (표 아래 \'뺀 칸\'에서 되살릴 수 있습니다)'}`))
          { await onEdit({ key: r.key, field: 'deleterow', text: '' }); setSel(null) }
        break
      case 'page': if (r.pages.length > 0) page(r.pages[0], rowMarks(r, r.pages[0])); break
      case 'ref': onSuggest(r.key); break
      case 'reset': if (confirm(`${r.key} 줄을 사양서 값으로 되돌릴까요?`)) await onReset(r.key); break
    }
  }
  const menuItems = (): { a: SheetAction; label: string; disabled?: boolean }[] => {
    if (!sel) return []
    const r = visible[sel.y]
    const split = multi && r.split?.length === n
    const sp = split ? (r.span?.length === n ? r.span! : Array(n).fill(1)) as number[] : null
    return [
      { a: 'edit', label: '고치기 (Enter)' },
      { a: 'merge', label: '셀 병합', disabled: !split || sel.c2 >= n || sel.c2 <= sel.c1 },
      { a: 'unmerge', label: split ? '병합 풀기' : '칸 나누기', disabled: !multi || sel.c1 >= n || (split && (sp![sel.c1] ?? 1) <= 1) },
      { a: 'mergeRow', label: '줄 전체 합치기', disabled: !split },
      { a: 'addRow', label: '아래에 행 추가' },
      { a: 'deleteRow', label: r.custom ? '행 삭제' : '행 빼기(표·pptx에서)' },
      { a: 'page', label: '사양서 원문 보기', disabled: r.pages.length === 0 },
      { a: 'ref', label: '참고(후보·근거)', disabled: r.slots.every((x) => x.status === 'given') },
      { a: 'reset', label: '사양서 값으로 되돌리기', disabled: !r.edited },
    ]
  }
  return (
    <>
      <div className="sd-meta">
        <span><em>제목</em>{d.title}</span>
        <span><em>공정</em>{d.colHeader || '-'}</span>
        <span><em>대수</em><Marked text={d.total.replace('\n', ' ')} /></span>
      </div>
      {d.conditions && <Conditions c={d.conditions} />}
      <SheetToolbar sel={sel} rows={visible} n={n} multi={multi} busy={busy} colName={colName}
        onAction={(a) => void act(a)} />
      {/* 스프레드시트처럼: 칸 누르기 = 고르기(Shift = 같은 줄 범위), 두 번 누르기·Enter·F2 = 고치기, 방향키 = 이동, 우클릭 = 메뉴 */}
      <div className="data-wrap sd-grid" tabIndex={0} ref={gridRef} onKeyDown={onGridKey}>
        {/* 공정 열이 많으면(여러 공정) 열마다 최소 폭을 두고 가로로 넘기게 — 사양서 패널을 펴도 글자가 세로로 쪼개지지 않게 */}
        <table className="sd-table" style={multi ? { minWidth: 120 + (d.processes?.length ?? 0) * 110 + 340 } : undefined}>
          <thead>
            <tr>
              <th>구분</th>
              {multi
                ? (d.processes ?? []).map((p) => <th key={p.no + p.label}>{colTitle(p)}</th>)
                : <th>{d.colHeader || '사양'}</th>}
              <th className="sd-note-head">기타 사항</th>
              <th className="sd-remark-head" title="화면에서만 보입니다. pptx에는 넣지 않습니다.">참고 사항 <small>(pptx 제외)</small></th>
            </tr>
          </thead>
          <tbody>
            {visible.map((r, y) => {
              const split = multi && r.split?.length === n
              const spans = split ? (r.span?.length === n ? r.span! : Array(n).fill(1)) as number[] : null
              const cellProps = (c: number, c2 = c) => ({
                className: inSel(y, c, c2) ? 'sd-sel' : undefined,
                onMouseDown: (e: React.MouseEvent) => { if (e.button === 0) select(y, c, c2, e.shiftKey) },
                onDoubleClick: () => { select(y, c, c2, false); setEditing({ y, c }) },
                onContextMenu: (e: React.MouseEvent) => { e.preventDefault(); if (!inSel(y, c, c2)) select(y, c, c2, false); setMenu({ x: e.clientX, y: e.clientY }) },
              })
              const isEditing = (c: number) => editing?.y === y && editing.c === c
              return (
                <tr key={r.key}>
                  <td className="sd-rowhead">
                    <b>{r.key}</b>
                    {r.custom && <small className="sd-custom">추가한 줄</small>}
                    {r.edited && (
                      <div className="sd-edited" title={`${r.editedBy ?? ''} · ${day(r.editedAt)}`}>
                        직접 고침
                        <button className="text-button" disabled={busy} title="고친 글·병합을 버리고 사양서 값으로 되돌립니다"
                          onClick={() => { if (confirm(`${r.key} 줄을 사양서 값으로 되돌릴까요?`)) void onReset(r.key) }}>되돌리기</button>
                      </div>
                    )}
                  </td>
                  {split ? (
                    // 공정별 값: 칸마다(병합 폭 span, 덮인 칸은 그리지 않음)
                    (r.split ?? []).map((v, c) => spans![c] === 0 ? null : (
                      <td key={c} colSpan={spans![c]} {...cellProps(c, c + spans![c] - 1)}>
                        <EditableCell text={v} busy={busy} editing={isEditing(c)} onDone={() => setEditing(null)}
                          onSave={(text) => onEdit({ key: r.key, field: 'split', index: c, text })} />
                      </td>
                    ))
                  ) : (
                    // 모든 공정에 같은 값: 한 칸
                    <td colSpan={n} {...cellProps(0, n - 1)}>
                      <EditableCell text={r.value} busy={busy} editing={isEditing(0)} onDone={() => setEditing(null)}
                        empty={<span className="sd-red">사양서에서 찾지 못함 — 확인 필요</span>}
                        onSave={(text) => onEdit({ key: r.key, field: 'value', text })} />
                    </td>
                  )}
                  <td {...cellProps(n)}>
                    {/* 기타 사항: 줄마다 근거 쪽 번호(누르면 그 쪽에서 원문 자리를 노란색으로) */}
                    <EditableCell text={r.note} busy={busy} editing={isEditing(n)} onDone={() => setEditing(null)}
                      onSave={(text) => onEdit({ key: r.key, field: 'note', text })}
                      view={r.note ? <NoteLines r={r} onPage={page} /> : undefined} />
                  </td>
                  {/* 참고 사항(화면만, pptx 제외): 근거 쪽·확인 안내·계산 설명·모델 자리·'참고' 버튼 */}
                  <td {...cellProps(n + 1)} className={`sd-remark${inSel(y, n + 1) ? ' sd-sel' : ''}`}>
                    {r.pages.length > 0 && <div className="sd-pages"><em>근거</em> <Pages pages={r.pages} onPage={(p) => page(p, rowMarks(r, p))} /></div>}
                    <EditableCell text={r.remark ?? ''} busy={busy} editing={isEditing(n + 1)} onDone={() => setEditing(null)}
                      onSave={(text) => onEdit({ key: r.key, field: 'remark', text })} />
                    {r.slots.some((s) => s.status === 'existing') && (
                      <div className="sd-spec-says">
                        사양서: 기설치와 동일 모델
                        {r.slots.find((s) => s.specPage)?.specPage && <Pages pages={[r.slots.find((s) => s.specPage)!.specPage!]} onPage={page} />}
                      </div>
                    )}
                    {r.slots.filter((s) => s.status === 'given').map((s) => (
                      <div key={s.id} className="sd-spec-says">사양서 지정: {s.label} <b className="sd-blue">{s.specModel}</b>
                        {s.specPage && <Pages pages={[s.specPage]} onPage={page} />}</div>
                    ))}
                    <RefButton r={r} onClick={() => onSuggest(r.key)} />
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
      {menu && sel && (
        <SheetMenu x={menu.x} y={menu.y} items={menuItems()} onPick={(a) => { setMenu(null); void act(a) }} onClose={() => setMenu(null)} />
      )}
      {d.rows.some((r) => r.hidden) && (
        <div className="sd-hidden-rows">
          <span>표에서 뺀 칸 <small>(pptx에도 안 들어감)</small></span>
          {d.rows.filter((r) => r.hidden).map((r) => (
            <button key={r.key} className="secondary" disabled={busy} title="다시 표에 넣습니다"
              onClick={() => void onEdit({ key: r.key, field: 'show', text: '' })}>{r.key} 되살리기</button>
          ))}
        </div>
      )}
      {/* 3장 이후 목록(pptx 장): PCS·제공 항목은 사양서 문장에서, 기타는 회의·협의 결과라 사람이 채운다 */}
      <Checklist title="PCS 사항" items={d.pcs} onPage={page} empty="사양서에서 PCS·건드롭·지관·폐신너 요구를 찾지 못했습니다. 현장·협의 내용을 채워 주세요." />
      <Checklist title="제공 항목" items={d.supply ?? []} onPage={page} empty="사양서에서 스페어·특수공구·키락·로봇 커버 같은 제공 요구를 찾지 못했습니다." />
      <Checklist title="기타 사항" items={d.etc} onPage={page} empty="기타 사항은 회의·협의 결과(결정·조건·주의)라 사양서로 채우지 않습니다. 직접 적어 주세요." />
    </>
  )
}

function Checklist({ title, items, onPage, empty }: { title: string; items: Check[]; onPage(p: number): void; empty: string }) {
  return (
    <div className="sd-checks">
      <h4>{title} <small>{items.length}건</small></h4>
      {items.length === 0 ? <p className="spec-hint">{empty}</p> : (
        <ul>
          {items.map((c, i) => (
            <li key={i}>□ <Marked text={c.text} /> <Pages pages={c.pages} onPage={onPage} /></li>
          ))}
        </ul>
      )}
    </div>
  )
}

const HOW_TONE: Record<string, string> = { '과거 사양정리 참고': 'green', '사양서 지정': 'blue', '기설치 이력': 'green', '공장 이력': 'green', '경향 규칙': 'amber', '선정 필요': 'red' }

/** 사양서에서 읽은 조건(모델을 정한 기준) */
function Conditions({ c }: { c: NonNullable<Draft['conditions']> }) {
  const item = (k: string, v: string) => <span><em>{k}</em>{v || <span className="sd-red">못 읽음</span>}</span>
  return (
    <div className="sd-cond">
      <b>모델을 정한 기준</b>
      {item('공장', c.plant)}{item('공정', c.process)}{item('외판/내판', c.part)}{item('부위', c.area)}
      {item('도료', c.paint)}{item('하전', c.charge)}
      <small>{c.paintNote} · 하전 문장 {c.chargeNote}</small>
    </div>
  )
}

function SlotView({ slot: s, no, busy, onPage, onDecide }: {
  slot: Slot; no: (c: Candidate) => string; busy: boolean; onPage(p: number): void
  onDecide(kind: string, recordId?: string, text?: string): void
}) {
  const [custom, setCustom] = useState('')
  const pick = chosen(s)
  return (
    <div className="sd-slot">
      <h4>{s.kind === 'row' ? `${s.label} — 과거 사양정리 참고` : `${s.label} 모델`}</h4>
      <div className="sd-why">
        <div className="sd-how"><span className={`badge mini ${HOW_TONE[s.how] ?? ''}`}>{s.how}</span> {s.why}</div>
        {s.specText && (
          <blockquote>{s.specText} {s.specPage && <Pages pages={[s.specPage]} onPage={onPage} />}</blockquote>
        )}
        {s.kind === 'row' ? (
          <small>사양서에 이 칸 내용이 없어, 과거 프로젝트 당사 사양정리의 같은 칸을 그대로 보여 줍니다(만든 값 아님). 찾는 순서: 같은 공장·같은 공정 → 같은 공정·외/내판 → 같은 공장 → 2022년 이후 최근. 파일 이름을 눌러 원본을 확인하세요.</small>
        ) : (
          <small>정하는 순서: ① 사양서 지정 → ② 기설치 이력(사양서가 '기설치와 동일'이면 같은 공장 최근) → ③ 같은 공장 이력 → ④ 경향 규칙(도장기 = 도료·하전, 오프너 = 도어 MPX2600·후드 MPX3500, 스프레이·FGP·세정장치 = 2022년 이후 보통) → ⑤ 선정 필요.
            같은 공장 이력으로 골랐어도, 다른 공장에서 요즘(2022년 이후 같은 공정·외/내판) 많이 쓰는 모델은 '최근 추세' 후보로 함께 보여 줍니다.</small>
        )}
      </div>
      {s.decision && (
        <div className="sd-decided">
          {s.decision.kind === 'candidate' ? '채택' : s.decision.kind === 'default' ? '기본 사양(1순위)' : s.decision.kind === 'custom' ? `직접 입력: ${s.decision.text}` : '선정 필요로 둠'}
          {s.decision.applied && <span> · 표 칸에 <b>{s.decision.applied}</b> 넣음</span>}
          <small>{s.decision.by} · {day(s.decision.at)}</small>
          {s.decision.kind !== 'default' && <button className="text-button" disabled={busy} title="기본 사양(1순위)으로 돌아갑니다" onClick={() => onDecide('reset')}>결정 취소</button>}
        </div>
      )}
      {s.status === 'given' && <div className="empty">사양서에 모델이 적혀 있어 사양서 값을 씁니다.</div>}
      {s.status !== 'given' && s.candidates.length === 0 && <div className="empty">비슷한 과거 이력이 없습니다. 직접 입력하거나 선정 필요로 두세요.</div>}
      {s.candidates.map((c, i) => {
        const isPick = pick?.recordId === c.recordId
        return (
          <div key={c.recordId} className={`sd-cand${isPick ? ' pick' : ''}`}>
            <div className="sd-cand-head">
              <span className="sd-rank">{i + 1}</span>
              <b className="sd-green">{c.model}</b>
              {isPick && no(c) && <span className="badge mini green">근거 {no(c)}</span>}
              {i === 0 && <span className="badge mini">기본 사양</span>}
              {c.excerpt && <small className="sd-count">{c.excerpt}</small>}
            </div>
            <div className="sd-cand-proj">{c.year} · {c.projectFolder}</div>
            <div className="badge-row">{c.reasons.map((r) => <span key={r} className="badge mini">{r}</span>)}</div>
            <div className="sd-cand-src">
              <em>{c.sourceKind === 'summary' ? '당사 사양정리' : '도면'}</em>
              {/* 근거 원본(NAS 읽기 전용): 설비 이력 기록 또는 라이브러리 사양정리(lib:{id}) */}
              <button className="text-button" onClick={() => downloadFile(c.recordId.startsWith('lib:')
                ? `/api/spec/library/${c.recordId.slice(4)}/file?kind=summary`
                : `/api/spec/equipment/${c.recordId}/file`, c.fileName)}>{c.fileName}</button>
            </div>
            {c.context && <div className="sd-cand-ex">다른 사례: {c.context.split('\n').map((l) => <span key={l}>{l}</span>)}</div>}
            <div className="actions">
              <button className={isPick && s.decision ? 'secondary' : 'primary'} disabled={busy || (isPick && s.decision?.kind === 'candidate')}
                onClick={() => onDecide('candidate', c.recordId)}>
                {isPick && s.decision?.kind === 'candidate' ? '채택됨' : '이 모델 채택'}
              </button>
            </div>
          </div>
        )
      })}
      <div className="sd-custom">
        <input placeholder="직접 입력 (예: MPX3500)" value={custom} onChange={(e) => setCustom(e.target.value)} />
        <button className="secondary" disabled={busy || !custom.trim()} onClick={() => { onDecide('custom', undefined, custom.trim()); setCustom('') }}>입력</button>
        <button className="secondary" disabled={busy || s.decision?.kind === 'none'} onClick={() => onDecide('none')}>선정 필요로 두기</button>
      </div>
    </div>
  )
}

/** 사양서 한 쪽: PDF면 그림, 아니면 그 쪽 글 */
/**
 * 표 위 그림: 사양서 레이아웃·전체 시스템 구성도 영역(초안의 layoutImage)을 잘라 보여 준다 — pptx 2장 표 위 그림과 같다.
 * 누르면 그 쪽 전체가 오른쪽에 열린다.
 */
function LayoutPicture({ projectId, fileId, stamp, page, onPage }: {
  projectId: number; fileId: number; stamp: string; page: number; onPage(p: number): void
}) {
  const [img, setImg] = useState<string | null>(null)
  useEffect(() => {
    let url: string | null = null
    let alive = true
    const headers = new Headers()
    const token = tokenStore.get()
    if (token) headers.set('Authorization', `Bearer ${token}`)
    fetch(`${API_BASE}/api/projects/${projectId}/spec-draft/layout?fileId=${fileId}&t=${encodeURIComponent(stamp)}`, { headers })
      .then(async (res) => {
        if (!res.ok || !res.headers.get('content-type')?.includes('image')) return
        url = URL.createObjectURL(await res.blob())
        if (alive) setImg(url)
      })
      .catch(() => undefined)
    return () => { alive = false; if (url) URL.revokeObjectURL(url) }
  }, [projectId, fileId, stamp])
  if (!img) return null
  return (
    <button className="sd-layout" onClick={() => onPage(page)} title={`사양서 ${page}쪽 레이아웃 — 누르면 원문 쪽을 봅니다`}>
      <img src={img} alt="사양서 레이아웃" />
      <small>사양서 {page}쪽 레이아웃 · pptx 표 위에도 들어갑니다</small>
    </button>
  )
}

/** 사양서 한 쪽을 받아 온다. PDF면 그림(dpi 해상도), 아니면 그 쪽 글 */
function usePageImage(projectId: number, fileId: number, page: number, dpi: number, marks?: string[]) {
  const markKey = JSON.stringify(marks ?? [])
  const [img, setImg] = useState<string | null>(null)
  const [lines, setLines] = useState<string[] | null>(null)
  const [err, setErr] = useState<string | null>(null)
  useEffect(() => {
    let url: string | null = null
    let alive = true
    setImg(null); setLines(null); setErr(null)
    const headers = new Headers()
    const token = tokenStore.get()
    if (token) headers.set('Authorization', `Bearer ${token}`)
    const q = (JSON.parse(markKey) as string[]).map((m) => `&mark=${encodeURIComponent(m.slice(0, 200))}`).join('')
    fetch(`${API_BASE}/api/projects/${projectId}/spec-draft/page?fileId=${fileId}&page=${page}&dpi=${dpi}${q}`, { headers })
      .then(async (res) => {
        if (!res.ok) throw new Error(`쪽을 불러오지 못했습니다. (${res.status})`)
        if (res.headers.get('content-type')?.includes('image')) {
          url = URL.createObjectURL(await res.blob())
          if (alive) setImg(url)
        } else {
          const j = await res.json()
          if (alive) setLines(j.lines ?? [])
        }
      })
      .catch((e: Error) => alive && setErr(e.message))
    return () => { alive = false; if (url) URL.revokeObjectURL(url) }
  }, [projectId, fileId, page, dpi, markKey])
  return { img, lines, err }
}

function PageView({ projectId, fileId, page, marks, pages, isPdf, onPage }: {
  projectId: number; fileId: number; page: number; marks?: string[]; pages: PageInfo[]; isPdf: boolean; onPage(p: number): void
}) {
  const { img, lines, err } = usePageImage(projectId, fileId, page, 110, marks)
  const [zoomed, setZoomed] = useState(false)
  const max = pages.length ? pages[pages.length - 1].page : page
  return (
    <div className="sd-pageview">
      <div className="sd-pagebar">
        <button className="secondary" disabled={page <= 1} onClick={() => onPage(page - 1)}>‹</button>
        <b>사양서 {page}쪽</b> <small>/ {max}</small>
        <button className="secondary" disabled={page >= max} onClick={() => onPage(page + 1)}>›</button>
        {img && <button className="secondary sd-zoom-open" onClick={() => setZoomed(true)} title="크게 보기">⤢ 크게</button>}
      </div>
      {err && <div className="notice spec-warn">{err}</div>}
      {!img && !lines && !err && <Loading />}
      {img && (
        <button className="sd-page-img" onClick={() => setZoomed(true)} title="누르면 크게 봅니다">
          <img src={img} alt={`사양서 ${page}쪽`} />
        </button>
      )}
      {zoomed && <PageZoom projectId={projectId} fileId={fileId} page={page} marks={marks} max={max} onPage={onPage} onClose={() => setZoomed(false)} />}
      {lines && (
        <div className="sd-pagetext">
          {!isPdf && <small>PDF가 아니라 글만 보여 줍니다.</small>}
          {lines.map((l, i) => <p key={i}>{l}</p>)}
        </div>
      )}
    </div>
  )
}

/**
 * 사양서 쪽 크게 보기(화면 전체). 고해상도(200dpi)로 다시 받아 확대·축소·이동한다.
 * 확대: +/− 버튼, Ctrl+휠, 키보드 + −. 이동: 끌기·스크롤. 쪽 넘기기: ‹ › 버튼·← →. 닫기: Esc·바깥 누르기·✕.
 */
function PageZoom({ projectId, fileId, page, marks, max, onPage, onClose }: {
  projectId: number; fileId: number; page: number; marks?: string[]; max: number; onPage(p: number): void; onClose(): void
}) {
  const { img, err } = usePageImage(projectId, fileId, page, 200, marks)
  const [scale, setScale] = useState<number | 'fit'>('fit')
  const [natural, setNatural] = useState<{ w: number; h: number } | null>(null)
  const box = useRef<HTMLDivElement>(null)
  const drag = useRef<{ x: number; y: number; left: number; top: number } | null>(null)
  // 보이는 영역 폭(창 크기를 바꾸면 '폭 맞춤'도 다시 계산)
  const [boxW, setBoxW] = useState(0)
  useEffect(() => {
    const el = box.current
    if (!el) return
    const ro = new ResizeObserver(() => setBoxW(el.clientWidth))
    ro.observe(el)
    return () => ro.disconnect()
  }, [])
  // 200dpi 그림 기준 배율. 'fit' = 화면 폭에 맞춤(원본보다 키우지는 않음)
  const fit = natural && boxW ? Math.min((boxW - 32) / natural.w, 1) : 1
  const current = scale === 'fit' ? fit : scale
  const zoomBy = (f: number) => setScale(Math.min(4, Math.max(0.2, current * f)))
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
      else if (e.key === 'ArrowLeft' && page > 1) onPage(page - 1)
      else if (e.key === 'ArrowRight' && page < max) onPage(page + 1)
      else if (e.key === '+' || e.key === '=') zoomBy(1.25)
      else if (e.key === '-') zoomBy(0.8)
    }
    window.addEventListener('keydown', onKey)
    const prev = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => { window.removeEventListener('keydown', onKey); document.body.style.overflow = prev }
  })
  // body에 바로 띄운다 — 미리보기 칸(sticky) 안에 그리면 그 쌓임 순서에 갇혀 표 머리줄·도구 막대가 위로 올라온다
  return createPortal(
    <div className="sd-zoom" role="dialog" aria-label={`사양서 ${page}쪽 크게 보기`} onClick={(e) => { if (e.target === e.currentTarget) onClose() }}>
      <div className="sd-zoom-bar">
        <button className="secondary" disabled={page <= 1} onClick={() => onPage(page - 1)} title="이전 쪽 (←)">‹</button>
        <b>사양서 {page}쪽</b> <small>/ {max}</small>
        <button className="secondary" disabled={page >= max} onClick={() => onPage(page + 1)} title="다음 쪽 (→)">›</button>
        <span className="sd-zoom-gap" />
        <button className="secondary" onClick={() => zoomBy(0.8)} title="축소 (−, Ctrl+휠)">−</button>
        <span className="sd-zoom-pct">{Math.round(current * 100)}%</span>
        <button className="secondary" onClick={() => zoomBy(1.25)} title="확대 (+, Ctrl+휠)">+</button>
        <button className="secondary" onClick={() => setScale('fit')} title="화면 폭에 맞춤">폭 맞춤</button>
        <button className="secondary" onClick={() => setScale(1)} title="원본 크기">원본</button>
        <button className="secondary" onClick={onClose} title="닫기 (Esc)">✕ 닫기</button>
      </div>
      <div
        ref={box}
        className="sd-zoom-body"
        onClick={(e) => { if (e.target === e.currentTarget) onClose() }}
        onWheel={(e) => { if (e.ctrlKey) { e.preventDefault(); zoomBy(e.deltaY < 0 ? 1.1 : 0.9) } }}
        onMouseDown={(e) => {
          if (e.button !== 0 || !box.current || e.target === e.currentTarget) return
          drag.current = { x: e.clientX, y: e.clientY, left: box.current.scrollLeft, top: box.current.scrollTop }
          e.preventDefault()
        }}
        onMouseMove={(e) => {
          if (!drag.current || !box.current) return
          box.current.scrollLeft = drag.current.left - (e.clientX - drag.current.x)
          box.current.scrollTop = drag.current.top - (e.clientY - drag.current.y)
        }}
        onMouseUp={() => { drag.current = null }}
        onMouseLeave={() => { drag.current = null }}
      >
        {err && <div className="notice spec-warn">{err}</div>}
        {!img && !err && <div className="sd-zoom-loading">크게 보기용 그림을 불러오는 중…</div>}
        {img && (
          <img
            src={img}
            alt={`사양서 ${page}쪽`}
            draggable={false}
            onLoad={(e) => setNatural({ w: e.currentTarget.naturalWidth, h: e.currentTarget.naturalHeight })}
            style={natural ? { width: natural.w * current, height: natural.h * current } : undefined}
          />
        )}
      </div>
    </div>,
    document.body,
  )
}

function CompareView({ data, onPage }: { data: DraftResponse; onPage(p: number): void }) {
  const info = new Map(data.pages.map((p) => [p.page, p]))
  const s = data.similar
  return (
    <>
      <div className="sd-checks">
        <h4>초안에 안 쓰인 사양서 쪽 <small>{data.unusedPages.length}쪽 · 빠뜨린 요구 사항이 없는지 훑어보세요</small></h4>
        {data.unusedPages.length === 0 ? <p className="spec-hint">글이 있는 모든 쪽이 초안 어딘가의 근거로 쓰였습니다.</p> : (
          <ul className="sd-unused">
            {data.unusedPages.map((p) => (
              <li key={p}><button className="sd-page" onClick={() => onPage(p)}>p{p}</button> {info.get(p)?.head}</li>
            ))}
          </ul>
        )}
      </div>
      <div className="sd-checks">
        <h4>비슷한 과거 프로젝트와 값 비교 {s && <small>{s.year} · {s.projectFolder}{s.summaryFile ? ` · ${s.summaryFile}` : ''}</small>}</h4>
        {!s ? <p className="spec-hint">사양정리 표가 있는 비슷한 과거 프로젝트를 찾지 못했습니다.</p> : (
          <div className="data-wrap">
            <table className="sd-table">
              <thead><tr><th>구분</th><th>이번 초안 (사양서)</th><th>과거 사양정리</th><th>과거 기타 사항</th></tr></thead>
              <tbody>
                {s.rows.map((r) => (
                  <Fragment key={r.key}>
                    <tr className={r.thisValue && r.pastValue && r.thisValue !== r.pastValue ? 'sd-diff' : ''}>
                      <td><b>{r.key}</b></td>
                      <td>{r.thisValue ? <Marked text={r.thisValue} /> : <span className="spec-none">-</span>}</td>
                      <td>{r.pastValue || <span className="spec-none">-</span>}</td>
                      <td className="spec-note">{r.pastNote}</td>
                    </tr>
                  </Fragment>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <p className="spec-hint">과거 값은 참고용입니다. 초안에는 옮기지 않습니다(값은 이번 사양서에서만).</p>
      </div>
    </>
  )
}
