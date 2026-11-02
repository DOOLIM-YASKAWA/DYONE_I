/**
 * 선 아이콘 한 벌(D안 디자인, 2026-11-02). 메뉴·버튼에서 특수문자(⌂ ▦ ✎ …) 대신 쓴다.
 * 24×24 칸에 선 굵기 1.7, 색은 글자색(currentColor)을 따른다.
 */
export const ICON = {
  home: 'M3 10.5 12 3l9 7.5V20a1 1 0 0 1-1 1h-5v-6h-6v6H4a1 1 0 0 1-1-1z',
  projects: 'M3 3h7v9H3zM14 3h7v5h-7zM14 12h7v9h-7zM3 16h7v5H3z',
  journal: 'M5 3h11l3 3v15H5zM9 8h6M9 12h6M9 16h3',
  modular: 'M12 2 3 7v10l9 5 9-5V7zM3 7l9 5 9-5M12 12v10',
  org: 'M9 3h6v5H9zM3 16h6v5H3zM15 16h6v5h-6zM12 8v4M6 16v-4h12v4',
  issue: 'M5 21V4M5 4h11l-2 4 2 4H5',
  lab: 'M9 3h6M10 3v6l-5 9a2 2 0 0 0 1.7 3h10.6a2 2 0 0 0 1.7-3l-5-9V3M7.5 15h9',
  spec: 'M14 3H6v18h12V7zM14 3v4h4M9 13.5l2 2 4-4.5',
  srs: 'M14 3H6v18h12V7zM14 3v4h4M11 16a2.5 2.5 0 1 0 0-5 2.5 2.5 0 0 0 0 5zM13 15.5l2.5 2.5',
  process: 'M4 5h6v4H4zM14 15h6v4h-6zM7 9v4a2 2 0 0 0 2 2h5M17 15v-4a2 2 0 0 0-2-2h-5',
  robot: 'M4 21h9M8 21v-3M8 18l4-7M12 11l5-3M17 8l2.5 3M17 8l-1.5-3.5H19',
  meals: 'M7 3v8M5 3v5a2 2 0 0 0 4 0V3M7 11v10M17 3c-2 0-3 2.5-3 5.5S15 13 17 13v8M17 3v18',
  shield: 'M12 3 4 6v6c0 4.5 3.4 8 8 9 4.6-1 8-4.5 8-9V6z',
  spark: 'M12 3l2 5 5 2-5 2-2 5-2-5-5-2 5-2zM19 17l.8 2 2 .8-2 .8-.8 2-.8-2-2-.8 2-.8z',
  it: 'M4 5h16v11H4zM9 20h6M12 16v4',
  car: 'M3 16v-4l2-5h14l2 5v4zM7 16v2M17 16v2M7 13h.01M17 13h.01',
  badge: 'M5 3h14v18H5zM9 9a3 3 0 1 0 6 0 3 3 0 0 0-6 0M8 17c1-2 2.4-3 4-3s3 1 4 3',
  bell: 'M6 16v-5a6 6 0 1 1 12 0v5l2 2H4zM10 21h4',
  menu: 'M4 6h16M4 12h16M4 18h16',
  close: 'M6 6l12 12M18 6 6 18',
  down: 'M6 9l6 6 6-6',
} as const
export type IconName = keyof typeof ICON

export function Icon({ name, size = 18, className }: { name: IconName; size?: number; className?: string }) {
  return (
    <svg className={className} width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7"
      strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d={ICON[name]} />
    </svg>
  )
}
