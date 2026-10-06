import { useEffect, useRef, type ReactNode } from 'react'

/** 데모와 같은 상태 배지 색상 규칙 */
export function Badge({ status }: { status: string }) {
  const tone = /완료/.test(status)
    ? 'green'
    : /진행|시운전/.test(status)
      ? 'blue'
      : /접수|예정|준비|홀딩|영업|등록/.test(status)
        ? 'amber'
        : ''
  return <span className={`badge ${tone}`}>{status}</span>
}

export function PageHead({ eyebrow, title, sub, action }: { eyebrow: string; title: string; sub: string; action?: ReactNode }) {
  return (
    <div className="page-head">
      <div>
        <p className="eyebrow">{eyebrow}</p>
        <h1>{title}</h1>
        <p>{sub}</p>
      </div>
      {action}
    </div>
  )
}

export function Stat({ name, value, note }: { name: string; value: ReactNode; note: string }) {
  return (
    <div className="stat">
      <span>{name}</span>
      <strong>{value}</strong>
      <small>{note}</small>
    </div>
  )
}

export const Empty = ({ children }: { children: ReactNode }) => <div className="empty">{children}</div>

export function Loading() {
  return <div className="empty">불러오는 중…</div>
}

export function ErrorBox({ error }: { error: unknown }) {
  return <div className="notice">{error instanceof Error ? error.message : '데이터를 불러오지 못했습니다.'}</div>
}

/** 모바일 폭에서 표가 카드로 바뀔 때 각 칸에 제목을 붙이기 위해 data-label을 단다(mobile.css). */
export function DataTable({ headers, rows, empty }: { headers: string[]; rows: { key: string; cells: ReactNode[] }[]; empty?: string }) {
  if (rows.length === 0) return <Empty>{empty ?? '현재 선택한 프로젝트·상태에 해당하는 기록이 없습니다.'}</Empty>
  return (
    <div className="data-wrap">
      <table>
        <thead>
          <tr>
            {headers.map((h) => (
              <th key={h}>{h}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.key}>
              {row.cells.map((cell, i) => (
                <td key={headers[i]} data-label={headers[i]}>
                  {cell}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

/** 데모의 #editor 대화상자. 열려 있는 동안만 렌더링한다. */
export function Modal({ title, onClose, children, footer }: { title: string; onClose(): void; children: ReactNode; footer?: ReactNode }) {
  const ref = useRef<HTMLDialogElement>(null)
  useEffect(() => {
    const dialog = ref.current
    if (dialog && !dialog.open) dialog.showModal()
    return () => dialog?.close()
  }, [])
  return (
    <dialog id="editor" ref={ref} onCancel={onClose}>
      <div id="editorForm">
        <div className="dialog-head">
          <h2>{title}</h2>
          <button className="icon-btn" type="button" onClick={onClose} aria-label="닫기">
            ×
          </button>
        </div>
        {children}
        <div className="dialog-foot">
          <button type="button" className="secondary" onClick={onClose}>
            닫기
          </button>
          {footer}
        </div>
      </div>
    </dialog>
  )
}