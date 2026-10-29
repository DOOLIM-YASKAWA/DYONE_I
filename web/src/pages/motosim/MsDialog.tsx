import { useEffect, type ReactNode } from 'react'

/** 원본 런처 모양 대화상자(가운데, 둥근 흰 카드). wide = 런처 허브·관리자 센터 크기 */
export function MsDialog({ title, sub, onClose, children, footer, small, wide }: {
  title: string; sub?: string; onClose(): void; children: ReactNode; footer?: ReactNode; small?: boolean; wide?: boolean
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])
  return (
    <div className="ms ms-overlay" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose() }}>
      <div className={`ms-dialog${small ? ' small' : ''}${wide ? ' wide' : ''}`} role="dialog" aria-modal="true" aria-label={title}>
        <div className="ms-dialog-head">
          <div><h2>{title}</h2>{sub && <p>{sub}</p>}</div>
          <button className="ms-x" onClick={onClose} aria-label="닫기">×</button>
        </div>
        <div className="ms-dialog-body">{children}</div>
        {footer && <div className="ms-dialog-foot">{footer}</div>}
      </div>
    </div>
  )
}
