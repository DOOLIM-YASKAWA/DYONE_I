import type { IconName } from './components/icons'

// enabled: 현재 노출하는 메뉴. 이번 단계는 로그인 + 홈만 노출하고 나머지는 숨긴다.
// 나머지 화면(프로젝트·AI·IT·식단·방문차량·출입)은 코드는 그대로 두고 다음 단계에서 다시 켠다.
// (관련: YTVH-421 하위 작업 — 프로젝트 조회/SMARTWORK 연계 등)
// icon = components/icons.tsx 이름. group = 위쪽 메뉴 묶음(D안): main 바로 보임 · eng '엔지니어링' 펼침 · life '생활' 펼침
export type MenuGroup = 'main' | 'eng' | 'life'
export const routes: readonly { path: string; icon: IconName; name: string; short?: string; group: MenuGroup; enabled: boolean }[] = [
  { path: '/home', icon: 'home', name: '홈', group: 'main', enabled: true },
  { path: '/projects', icon: 'projects', name: '프로젝트', group: 'main', enabled: true },
  { path: '/journal', icon: 'journal', name: '프로젝트 진행 일지', short: '진행 일지', group: 'main', enabled: true },
  { path: '/spec', icon: 'spec', name: '사양서 분석', group: 'eng', enabled: true },
  { path: '/modular', icon: 'modular', name: '모듈라', group: 'eng', enabled: true },
  { path: '/org', icon: 'org', name: '조직도', group: 'main', enabled: true },
  { path: '/works/281', icon: 'issue', name: 'ES 이슈', group: 'eng', enabled: true },
  { path: '/smartlab', icon: 'lab', name: 'SmartLab', group: 'eng', enabled: true },
  { path: '/srs-analyzer', icon: 'srs', name: 'SRS Analyzer', group: 'eng', enabled: true },
  { path: '/process', icon: 'process', name: '공정 관리', group: 'eng', enabled: true },
  { path: '/motosim', icon: 'robot', name: 'MotoSim', group: 'eng', enabled: true },
  { path: '/chat', icon: 'spark', name: 'AI 챗봇', group: 'life', enabled: false },
  { path: '/it', icon: 'it', name: 'IT 요청', group: 'life', enabled: false },
  { path: '/meals', icon: 'meals', name: '수담푸드 메뉴', group: 'life', enabled: true },
  { path: '/vehicles', icon: 'car', name: '방문 차량', group: 'life', enabled: false },
  { path: '/access', icon: 'badge', name: '모바일 출입', group: 'life', enabled: false },
]

export const enabledRoutes = routes.filter((r) => r.enabled)

// 협력사(kind=partner)에게 보이는 메뉴. 서버도 이 화면들의 API만 협력사에게 연다(Platform/ProjectAccess.cs).
export const partnerPaths: readonly string[] = ['/projects']
export const visibleRoutes = (partner: boolean) => (partner ? enabledRoutes.filter((r) => partnerPaths.includes(r.path)) : enabledRoutes)

export const formatKoreanDate = (d: Date) =>
  d.toLocaleDateString('ko-KR', { year: 'numeric', month: 'long', day: 'numeric', weekday: 'long' })
