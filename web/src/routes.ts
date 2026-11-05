import type { IconName } from './components/icons'

// enabled: 현재 노출하는 메뉴. 이번 단계는 로그인 + 홈만 노출하고 나머지는 숨긴다.
// 나머지 화면(프로젝트·AI·IT·식단·방문차량·출입)은 코드는 그대로 두고 다음 단계에서 다시 켠다.
// (관련: YTVH-421 하위 작업 — 프로젝트 조회/SMARTWORK 연계 등)
// icon = components/icons.tsx 이름. group = 위쪽 탭 묶음(11-05): main 탭 하나 · eng '엔지니어링' · svc '사내 서비스' · life 숨김(예전 생활)
// 위쪽 탭 순서(11-05 사용자 결정): 프로젝트 | 다운로드 | 자료 검색 | 엔지니어링 | AI 두리미 | 사내 서비스 | 관리 — Layout.tsx TABS
export type MenuGroup = 'main' | 'eng' | 'svc' | 'life'
// desc = 큰 판 펼침(11-05 A1 · 유리 남색)에서 이름 아래 보이는 한 줄 설명
export const routes: readonly { path: string; icon: IconName; name: string; short?: string; desc?: string; group: MenuGroup; enabled: boolean }[] = [
  { path: '/home', icon: 'home', name: '홈', group: 'main', enabled: false }, // 11-04 홈 제거 — 첫 화면은 /projects(프로젝트 메인)
  { path: '/projects', icon: 'projects', name: '프로젝트', group: 'main', enabled: true },
  { path: '/journal', icon: 'journal', name: '프로젝트 진행 일지', short: '진행 일지', group: 'main', enabled: false }, // 11-05 메뉴에서 뺌(코드는 남김)
  { path: '/download', icon: 'download', name: '다운로드', group: 'main', enabled: true }, // 11-05 빈 화면(준비 중)
  { path: '/search', icon: 'search', name: '자료 검색', group: 'main', enabled: true }, // 11-05 빈 화면(준비 중)
  { path: '/ai', icon: 'spark', name: 'AI 두리미', group: 'main', enabled: true }, // 11-05 빈 화면(준비 중)
  { path: '/spec', icon: 'spec', name: '사양서 분석', desc: '사양서 비교 및 사양 정리 초안 작성', group: 'eng', enabled: true },
  { path: '/modular', icon: 'calendarBar', name: '모듈라 진행 일지', desc: '모듈라 빌드 진행·일정·글', group: 'eng', enabled: true },
  { path: '/org', icon: 'org', name: '조직도', desc: '부서·사람 찾기, 연락처', group: 'svc', enabled: true },
  { path: '/works/281', icon: 'wrench', name: 'CS 이슈 정리', desc: '현장 이슈 기록과 처리', group: 'eng', enabled: true },
  { path: '/smartlab', icon: 'lab', name: 'SmartLab', desc: '랩 도장 테스트 측정값 조회·분석', group: 'eng', enabled: true },
  { path: '/srs-analyzer', icon: 'cubePath', name: 'SRS Analyzer', desc: '공정별 로봇 측정 이력·통계 분석', group: 'eng', enabled: true },
  { path: '/process', icon: 'tree', name: '고객사 및 공정 관리', desc: '공정 편집툴', group: 'eng', enabled: true },
  { path: '/motosim', icon: 'cubePlay', name: 'MotoSim Launcher', desc: '시뮬레이션 Cell 파일 관리·실행', group: 'eng', enabled: true },
  { path: '/chat', icon: 'spark', name: 'AI 챗봇', group: 'life', enabled: false },
  { path: '/it', icon: 'it', name: 'IT 요청', group: 'life', enabled: false },
  { path: '/meals', icon: 'meals', name: '수담푸드 메뉴', desc: '오늘 점심 메뉴', group: 'svc', enabled: true },
  { path: '/vehicles', icon: 'car', name: '방문 차량', group: 'life', enabled: false },
  { path: '/access', icon: 'badge', name: '모바일 출입', group: 'life', enabled: false },
]

export const enabledRoutes = routes.filter((r) => r.enabled)

// 협력사(kind=partner)에게 보이는 메뉴. 서버도 이 화면들의 API만 협력사에게 연다(Platform/ProjectAccess.cs).
export const partnerPaths: readonly string[] = ['/projects']
export const visibleRoutes = (partner: boolean) => (partner ? enabledRoutes.filter((r) => partnerPaths.includes(r.path)) : enabledRoutes)

export const formatKoreanDate = (d: Date) =>
  d.toLocaleDateString('ko-KR', { year: 'numeric', month: 'long', day: 'numeric', weekday: 'long' })
