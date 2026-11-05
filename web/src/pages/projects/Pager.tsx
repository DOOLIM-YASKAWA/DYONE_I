import { Fragment } from 'react'

/** 목록 쪽 넘김(처음 · 지금 앞뒤 · 끝만 보이고 사이는 …). 프로젝트 목록 공통 */
export function Pager({ total, page, onPage, pageSize = 50 }: { total: number; page: number; onPage(p: number): void; pageSize?: number }) {
  const pages = Math.max(1, Math.ceil(total / pageSize))
  if (pages <= 1) return null
  const from = (page - 1) * pageSize + 1
  const to = Math.min(total, page * pageSize)
  // 처음 · 지금 앞뒤 · 끝만 보이고 사이는 …
  const nums = [...new Set([1, page - 1, page, page + 1, pages].filter((n) => n >= 1 && n <= pages))].sort((x, y) => x - y)
  return (
    <div className="pl-pager">
      <span><b>{from.toLocaleString()}–{to.toLocaleString()}</b> / {total.toLocaleString()}</span>
      <span className="pl-pages">
        <button aria-label="이전 페이지" disabled={page <= 1} onClick={() => onPage(page - 1)}>‹</button>
        {nums.map((n, i) => (
          <Fragment key={n}>
            {i > 0 && n - nums[i - 1] > 1 && <span className="pl-gap">…</span>}
            <button className={n === page ? 'on' : ''} aria-current={n === page ? 'page' : undefined} onClick={() => onPage(n)}>{n}</button>
          </Fragment>
        ))}
        <button aria-label="다음 페이지" disabled={page >= pages} onClick={() => onPage(page + 1)}>›</button>
      </span>
    </div>
  )
}
