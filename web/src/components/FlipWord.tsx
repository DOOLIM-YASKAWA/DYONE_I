import { useEffect, useRef, useState } from 'react'

/**
 * 로그인 로고 옆 낱말: INTERNAL ↔ INTEGRATION을 3초마다 글자판 넘김으로 바꾼다(11-05 시안 docs/시안/nav-variants/login-logo.html C · I 강조 1).
 * 두 낱말 모두 I로 시작하므로 I는 넘기지 않고 노랑·굵게·세로로 길게 강조하고, 나머지 글자는 작고 흐리게.
 * 아래 풀이(사내 업무 플랫폼 ↔ 통합 업무 플랫폼)도 같은 때에 같은 글자판 넘김으로 바뀐다(같은 글자 '업무 플랫폼'은 그대로).
 * 움직임 줄이기(prefers-reduced-motion)면 바꾸지 않는다. size='sm' = 메인 화면 위쪽 막대(로고 높이 22px, 풀이 없음 — 11-05 H1).
 */
const WORDS = ['INTERNAL', 'INTEGRATION'] as const
const SUBS = ['사내 업무 플랫폼', '통합 업무 플랫폼'] as const
const STEP = 60 // 글자 하나씩 넘어가는 간격(ms)

type Cell = { cur: string; old: string | null; n: number }

/** 낱말 묶음을 글자 칸으로 나눠, k가 바뀌면 칸마다 STEP 간격으로 넘긴다. skip = 넘기지 않는 칸 */
function useFlipCells(list: readonly string[], k: number, skip: number[] = []) {
  const len = Math.max(...list.map((w) => [...w].length))
  const [cells, setCells] = useState<Cell[]>(() => Array.from({ length: len }, (_, j) => ({ cur: [...list[0]][j] ?? '', old: null, n: 0 })))
  const timers = useRef<number[]>([])
  useEffect(() => {
    const w = [...list[k]]
    for (let j = 0; j < len; j++) {
      if (skip.includes(j)) continue
      timers.current.push(window.setTimeout(() => {
        setCells((cs) => cs.map((c, i) => (i === j && c.cur !== (w[j] ?? '') ? { cur: w[j] ?? '', old: c.cur, n: c.n + 1 } : c)))
        timers.current.push(window.setTimeout(() => setCells((cs) => cs.map((c, i) => (i === j ? { ...c, old: null } : c))), 400))
      }, j * STEP))
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [k])
  useEffect(() => () => timers.current.forEach(clearTimeout), [])
  return cells
}

function Cells({ cells, lead }: { cells: Cell[]; lead?: boolean }) {
  return (
    <>
      {cells.map((c, j) => (
        <i key={j} className={[lead && j === 0 ? 'lead' : '', c.cur === ' ' && c.old === null ? 'sp' : ''].filter(Boolean).join(' ')} aria-hidden="true">
          {c.old !== null && <b className="out">{c.old}</b>}
          <b key={c.n} className={c.n > 0 && c.old !== null ? 'in' : ''}>{c.cur}</b>
        </i>
      ))}
    </>
  )
}

export function FlipWord({ size = 'lg', sub = true }: { size?: 'lg' | 'sm'; sub?: boolean }) {
  const [k, setK] = useState(0)
  useEffect(() => {
    if (window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) return
    const id = window.setInterval(() => setK((x) => 1 - x), 3000)
    return () => window.clearInterval(id)
  }, [])
  const word = useFlipCells(WORDS, k, [0])
  const subCells = useFlipCells(SUBS, k)

  return (
    <span className={`dy-flip${size === 'sm' ? ' sm' : ''}`}>
      <span className="dy-flip-word" aria-label={WORDS[k]}><Cells cells={word} lead /></span>
      {sub && <span className="dy-flip-sub" aria-label={SUBS[k]}><Cells cells={subCells} /></span>}
    </span>
  )
}
