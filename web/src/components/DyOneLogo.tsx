import { useId } from 'react'

// DY ONE INTERNAL 로고 — 이미지 대신 SVG로 직접 그림(확대해도 흐려지지 않음, 11-04 J안).
// 원본 로고를 픽셀 단위로 재서 획을 정수 픽셀에 맞췄다(높이 24px에서 가장자리가 번지지 않게). INTERNAL은 글꼴에 기대지 않고 선으로 그림.
// tone: brand = 파란 바탕(상단 막대) · dark = 흰 바탕(메뉴 서랍 등)
const O = 'M2 0H23A5 5 0 0 1 28 5V19A5 5 0 0 1 23 24H2A2 2 0 0 1 0 22V2A2 2 0 0 1 2 0Z M6 5V19H21A1 1 0 0 0 22 18V6A1 1 0 0 0 21 5Z'
const N = 'M30 24V1A1 1 0 0 1 31 0H48A6 6 0 0 1 54 6V24H48V7A2 2 0 0 0 46 5H36V24Z'
const E = 'M57 6A6 6 0 0 1 63 0H75A6 6 0 0 1 81 6V14H63V19H81V24H63A6 6 0 0 1 57 18Z M63 6.5A1.5 1.5 0 0 1 64.5 5H73.5A1.5 1.5 0 0 1 75 6.5V9H63Z'
const T =
  'M91.6 9.2V16.8 M95.6 16.8V9.2L100.9 16.8V9.2 M103.1 9.2H108.9M106 9.2V16.8 M116.2 9.2H111.9V16.8H116.4M111.9 13H115.7 M119.9 16.8V9.2H123.1A2 2 0 0 1 123.1 13.2H119.9M122.5 13.2L125.3 16.8 M128.2 16.8V9.2L133.5 16.8V9.2 M136.1 16.8L139.2 9.2L142.3 16.8M137.3 14.1H141.1 M145.5 9.2V16.8H150.3'

const TONES = {
  brand: { o: ['#D4EDFF', '#86C2FF'], ne: ['#FFFFFF', '#E4F0FF'], line: 'rgba(255,255,255,.32)', t: '#E3EEFF' },
  dark: { o: ['#5AA2F5', '#0056B9'], ne: ['#0C1A33', '#0C1A33'], line: 'rgba(12,26,51,.2)', t: '#56637C' },
} as const

export function DyOneLogo({ height = 24, tone = 'brand' }: { height?: number; tone?: keyof typeof TONES }) {
  const id = useId().replace(/:/g, '')
  const c = TONES[tone]
  return (
    <svg viewBox="0 0 151 24" height={height} width={(height * 151) / 24} role="img" aria-label="DY ONE INTERNAL" style={{ display: 'block' }} shapeRendering="geometricPrecision">
      <defs>
        <linearGradient id={`${id}o`} x1="0" y1="0" x2="0" y2="1"><stop offset="0" stopColor={c.o[0]} /><stop offset="1" stopColor={c.o[1]} /></linearGradient>
        <linearGradient id={`${id}n`} x1="0" y1="0" x2="0" y2="1"><stop offset="0" stopColor={c.ne[0]} /><stop offset="1" stopColor={c.ne[1]} /></linearGradient>
      </defs>
      <path d={O} fill={`url(#${id}o)`} fillRule="evenodd" />
      <path d={N} fill={`url(#${id}n)`} />
      <path d={E} fill={`url(#${id}n)`} fillRule="evenodd" />
      <path d="M86.5 5V19" stroke={c.line} strokeWidth="1" />
      <path d={T} fill="none" stroke={c.t} strokeWidth="1.5" strokeLinecap="square" strokeLinejoin="miter" />
    </svg>
  )
}
