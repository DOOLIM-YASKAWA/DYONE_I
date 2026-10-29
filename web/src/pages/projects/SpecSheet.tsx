import { useEffect, useRef, useState } from 'react'
import jspreadsheet from 'jspreadsheet-ce'
import 'jspreadsheet-ce/dist/jspreadsheet.css'
import 'jsuites/dist/jsuites.css'

/** 시트 저장 줄: 이름 · 공정 칸 글 · 병합 폭(덮인 칸 0) · 기타 사항 (서버 SheetRow) */
export type SheetRow = { key: string; cells: string[]; spans: number[]; note: string }

type Row = { key: string; value: string; split?: string[] | null; span?: number[] | null; note: string; hidden?: boolean; pages?: number[] }

// 표시 꾸밈(**굵게** {{파랑}} [[빨강]] <<초록>>)은 시트에서는 글만
const plain = (s: string) => (s ?? '').replace(/\*\*|\{\{|\}\}|\[\[|\]\]|<<|>>/g, '')
const colName = (i: number) => String.fromCharCode(65 + i)

/**
 * 사양정리 표를 엑셀처럼 편집(jspreadsheet-ce, MIT): 셀 드래그 선택·병합/병합 풀기·행 추가/삭제(우클릭 메뉴)·복사/붙여넣기.
 * 열 = 구분 | 공정 칸(공정이 하나면 '사양' 한 칸) | 기타 사항 | 근거 쪽(읽기 전용, 누르면 오른쪽에 사양서 그 쪽). 참고 사항은 화면 전용이라 시트에 넣지 않는다.
 * 저장하면 행 순서·글·공정 칸 병합·기타 사항이 초안에 그대로 들어가고 pptx도 같은 모양(셀 병합)으로 나온다.
 */
export function SpecSheet({ rows, columns, busy, onSave, onCancel, onPage }: {
  rows: Row[]; columns: string[]; busy: boolean
  onSave(rows: SheetRow[]): Promise<unknown>; onCancel(): void
  /** 근거 쪽 칸을 누르면: 그 줄 이름과 쪽 */
  onPage?(rowKey: string, page: number): void
}) {
  const onPageRef = useRef(onPage)
  onPageRef.current = onPage
  const host = useRef<HTMLDivElement>(null)
  const ws = useRef<jspreadsheet.WorksheetInstance | null>(null)
  const [msg, setMsg] = useState<string | null>(null)
  const n = Math.max(1, columns.length)

  useEffect(() => {
    if (!host.current) return
    const visible = rows.filter((r) => !r.hidden)
    const data = visible.map((r) => {
      const cells = r.split && r.split.length === n ? r.split.map(plain) : [plain(r.value), ...Array(n - 1).fill('')]
      return [r.key, ...cells, plain(r.note), (r.pages ?? []).map((p) => `p${p}`).join(' ')]
    })
    // 병합: 한 칸 줄(공정이 여럿인데 값 하나)은 공정 칸 전체, 공정 칸 병합 폭(span)은 그대로
    const merges: Record<string, [number, number]> = {}
    visible.forEach((r, y) => {
      if (n < 2) return
      if (!(r.split && r.split.length === n)) { merges[`${colName(1)}${y + 1}`] = [n, 1]; return }
      const span = r.span && r.span.length === n ? r.span : null
      span?.forEach((s, j) => { if (s > 1) merges[`${colName(1 + j)}${y + 1}`] = [s, 1] })
    })
    const [sheet] = jspreadsheet(host.current as HTMLDivElement, {
      worksheets: [{
        data,
        columns: [
          { title: '구분', width: 150, wordWrap: true },
          ...columns.map((t) => ({ title: t, width: n > 1 ? 150 : 520, wordWrap: true })),
          { title: '기타 사항', width: 280, wordWrap: true },
          // 근거 쪽: 고칠 수 없고 저장에도 안 들어감 — 누르면 오른쪽 사양서 패널에 첫 쪽(노란 표시)
          { title: '근거 쪽', width: 90, readOnly: true },
        ],
        mergeCells: merges,
        allowInsertColumn: false,
        allowDeleteColumn: false,
        allowRenameColumn: false,
        columnSorting: false,
        wordWrap: true,
        tableOverflow: true,
        tableWidth: '100%',
        tableHeight: '70vh',
        minDimensions: [n + 3, data.length],
      }],
      onselection: (inst, x1, y1, x2, y2) => {
        if (x1 !== n + 2 || x1 !== x2 || y1 !== y2) return
        const key = String(inst.getValueFromCoords(0, y1) ?? '').trim()
        const first = /p(\d+)/.exec(String(inst.getValueFromCoords(n + 2, y1) ?? ''))
        if (key && first) onPageRef.current?.(key, Number(first[1]))
      },
    })
    ws.current = sheet
    const el = host.current
    return () => {
      ws.current = null
      // 개발 모드(StrictMode)는 효과를 두 번 돌려 시트가 겹쳐 그려지므로, 없앨 때 그린 것을 모두 지운다
      try { jspreadsheet.destroy(el as unknown as jspreadsheet.JspreadsheetInstanceElement, true) } catch { /* 이미 없음 */ }
      el.innerHTML = ''
    }
  }, [rows, columns, n])

  const selection = () => {
    const sc = ws.current?.selectedCell
    if (!sc) { setMsg('먼저 칸을 고르세요(끌어서 여러 칸 선택).'); return null }
    const [x1, y1, x2, y2] = sc.map((v) => Number(v))
    return { x1: Math.min(x1, x2), y1: Math.min(y1, y2), x2: Math.max(x1, x2), y2: Math.max(y1, y2) }
  }

  const merge = () => {
    const s = selection()
    if (!s || !ws.current) return
    if (s.x1 < 1 || s.x2 > n) { setMsg('병합은 공정 칸끼리만 됩니다(구분·기타 사항 열 제외).'); return }
    if (s.y1 !== s.y2) { setMsg('병합은 한 줄 안에서 옆 칸끼리만 됩니다.'); return }
    ws.current.setMerge(`${colName(s.x1)}${s.y1 + 1}`, s.x2 - s.x1 + 1, 1)
    setMsg(null)
  }
  const unmerge = () => {
    const s = selection()
    if (!s || !ws.current) return
    ws.current.removeMerge(`${colName(s.x1)}${s.y1 + 1}`)
    setMsg(null)
  }
  const addRow = () => {
    const s = selection()
    ws.current?.insertRow(1, s ? s.y2 : undefined, 0)
  }
  const delRow = () => {
    const s = selection()
    if (s && ws.current && confirm(`${s.y2 - s.y1 + 1}줄을 지울까요? (양식 줄은 '표에서 뺀 칸'으로 갑니다)`)) ws.current.deleteRow(s.y1, s.y2 - s.y1 + 1)
  }

  const save = async () => {
    const sheet = ws.current
    if (!sheet) return
    const data = sheet.getData() as (string | number | null)[][]
    const merges = (sheet.getMerge() ?? {}) as Record<string, [number, number]>
    const out: SheetRow[] = data.map((r, y) => {
      const cells = Array.from({ length: n }, (_, j) => String(r[1 + j] ?? ''))
      const spans = Array(n).fill(1) as number[]
      for (const [cell, [cs]] of Object.entries(merges)) {
        const m = /^([A-Z])(\d+)$/.exec(cell)
        if (!m || Number(m[2]) !== y + 1) continue
        const x = m[1].charCodeAt(0) - 65 - 1
        if (x < 0 || x >= n) continue
        spans[x] = Math.min(cs, n - x)
        for (let k = x + 1; k < x + spans[x]; k++) spans[k] = 0
      }
      return { key: String(r[0] ?? '').trim(), cells, spans, note: String(r[1 + n] ?? '') }
    }).filter((r) => r.key.length > 0)
    await onSave(out)
  }

  return (
    <div className="sd-sheet">
      <div className="sd-sheet-bar">
        <b>스프레드시트로 편집</b>
        <small>끌어서 칸 선택 · 우클릭으로 행 추가/삭제·복사/붙여넣기 · 셀을 두 번 누르면 글 고치기</small>
        <span className="sd-sheet-actions">
          <button className="secondary" onClick={merge} title="고른 공정 칸들을 한 칸으로 합칩니다">셀 병합</button>
          <button className="secondary" onClick={unmerge} title="고른 칸의 병합을 풉니다(글은 첫 칸에)">병합 풀기</button>
          <button className="secondary" onClick={addRow}>행 추가</button>
          <button className="secondary" onClick={delRow}>행 삭제</button>
          <button className="secondary" onClick={onCancel} disabled={busy}>취소</button>
          <button className="primary" onClick={() => void save()} disabled={busy}>{busy ? '저장 중…' : '저장'}</button>
        </span>
      </div>
      {msg && <div className="notice spec-warn">{msg}</div>}
      <div ref={host} />
    </div>
  )
}
