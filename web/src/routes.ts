// enabled: 현재 노출하는 메뉴. 이번 단계는 로그인 + 홈만 노출하고 나머지는 숨긴다.
// 나머지 화면(프로젝트·AI·IT·식단·방문차량·출입)은 코드는 그대로 두고 다음 단계에서 다시 켠다.
// (관련: YTVH-421 하위 작업 — 프로젝트 조회/SMARTWORK 연계 등)
export const routes = [
  { path: '/home', icon: '⌂', name: '홈', enabled: true },
  { path: '/projects', icon: '▦', name: '프로젝트', enabled: true },
  { path: '/modular', icon: '▣', name: '모듈라', enabled: true },
  { path: '/org', icon: '☷', name: '조직도', enabled: true },
  { path: '/works/281', icon: '⚑', name: 'ES 이슈', enabled: true },
  { path: '/smartlab', icon: '⚗', name: 'SmartLab', enabled: true },
  { path: '/srs-analyzer', icon: '▧', name: 'SRS Analyzer', enabled: true },
  { path: '/process', icon: '⚙', name: '공정 관리', enabled: true },
  { path: '/motosim', icon: '◈', name: 'MotoSim', enabled: true },
  { path: '/chat', icon: '✦', name: 'AI 챗봇', enabled: false },
  { path: '/it', icon: '⌘', name: 'IT 요청', enabled: false },
  { path: '/meals', icon: '◉', name: '오늘의 식단', enabled: false },
  { path: '/vehicles', icon: '▱', name: '방문 차량', enabled: false },
  { path: '/access', icon: '⌁', name: '모바일 출입', enabled: false },
] as const

export const enabledRoutes = routes.filter((r) => r.enabled)

export const formatKoreanDate = (d: Date) =>
  d.toLocaleDateString('ko-KR', { year: 'numeric', month: 'long', day: 'numeric', weekday: 'long' })
