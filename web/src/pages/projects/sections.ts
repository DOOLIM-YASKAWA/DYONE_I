import type { Section } from '../../api/types'

/**
 * 프로젝트 화면의 5개 업무 영역. applet = 원본 그룹웨어 WORKS 화면 번호.
 * connected = dyone DB에 데이터가 있는지(진행 상태 = project.project, 모듈라 = modular.build, ES 이슈 = works.doc(281), 랩 테스트 = smartlab.lab_test)
 */
export const sections: { key: Section; name: string; applet: number; connected: boolean }[] = [
  { key: 'progress', name: '프로젝트 진행 상태', applet: 285, connected: true },
  { key: 'modular', name: '모듈라 상황', applet: 376, connected: true },
  { key: 'lab', name: '랩 테스트', applet: 335, connected: true },
  { key: 'journal', name: '어플팀 업무일지', applet: 296, connected: false },
  { key: 'es', name: 'ES_이슈사항 보고', applet: 281, connected: true },
]

export const isSection = (v: string | null): v is Section => sections.some((s) => s.key === v)
