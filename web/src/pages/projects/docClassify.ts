import type { Code } from '../../api/types'

/**
 * 프로젝트 자료 자동 분류 제안(규칙 기반, 사람이 확인).
 * 규칙은 NAS 프로젝트 폴더 13칸에 사람들이 실제로 넣은 파일로 정했다(2018~2026 프로젝트 173개 조사,
 * docs/project-docs/NAS 13칸 분류 기준). 확장자·이름 낱말·하위 폴더 이름이 어느 칸에 몰리는지를 보고 골랐다.
 * 규칙마다 '원하는 분류 코드' 순서를 둔다 — 앞 코드가 document_category에 없으면 다음 코드(마지막은 etc).
 */
interface Rule {
  label: string
  codes: string[]
  folder?: RegExp // 끌어다 놓은 폴더 경로의 폴더 이름
  name?: RegExp // 파일 이름 낱말
  ext?: string[] // 확장자(소문자, 점 없이)
}

const RULES: Rule[] = [
  // 1. 제안서: 제안·견적·입찰·공정 검토·시뮬레이션(OLP·모토심) 자료가 여기에 모인다
  { label: '제안서', codes: ['quote'], folder: /제안|견적|입찰|검토\s*자료|모토심|motosim|OLP|승인\s*체크/i, name: /제안|견적|quot|입찰|proposal|공정\s*검토|layout[_\s-]*simulation|시뮬/i },
  // 2. 사양서: 고객 사양서·사양정리·사양설명회·요약
  { label: '사양서', codes: ['spec'], folder: /사양/, name: /사양|\bspec|specification|사양설명회|요약/i },
  // 3. 수행 계획서: 공사 수행 계획·킥오프·일정
  { label: '수행 계획서', codes: ['schedule', 'report'], folder: /수행\s*계획|킥\s*오프|kick/i, name: /수행\s*계획|공사\s*수행|킥\s*오프|kick[\s_-]*off|일정표|schedule/i },
  // 4. 도면: CAD(AutoCAD·SolidWorks·STEP)와 도면 pdf, 레이아웃·ASSY·브래킷·배관도
  { label: '도면', codes: ['design'], folder: /도면|제작도|조립도|부품도|상세도|승인도|drawing|layout/i, name: /도면|제작도|조립도|부품도|상세도|승인도|layout|drawing|assy|bkt|bracket|diagram|배관|section|전장|결선|회로/i,
    ext: ['dwg', 'dwl', 'dwl2', 'dxf', 'step', 'stp', 'igs', 'iges', 'sldprt', 'sldasm', 'slddrw', 'cgr', 'p2m', 'p2s', 'catpart'] },
  // 5. 모듈라빌드: OLT·모듈라(오타 '모듈러' 포함)
  { label: '모듈라빌드', codes: ['modula'], folder: /모듈[라러]|modula|OLT/i, name: /모듈[라러]|modula|\bOLT/i },
  // 6. 티칭 DATA: 로봇 JOB·조건 파일·로봇 데이터, JOB 폴더
  { label: '티칭 DATA', codes: ['teach'], folder: /티칭|teach|\bJOB\b/i, name: /티칭|teach/i, ext: ['jbi', 'jbr', 'cnd', 'wrl'] },
  // 7. PLC DATA: 미쓰비시 GX Works·LS XG·지멘스 TIA·도장기 EVO(폴더 이름 오타 'PLC TATA'가 129건)
  { label: 'PLC DATA', codes: ['plc'], folder: /PLC|EVO/i, name: /PLC|\bIP\b|IP\s*리스트|네트워크|network|cycle/i,
    ext: ['gxw', 'gx3', 'gpj', 'wpg', 'wdv', 'xgdb', 'xmls', 'dbf', 'mdx', 'l5x', 'l5k', 'acd', 'ap17', 'zap17', 'zap20', 'ezlog', 'xch'] },
  // 8. HMI DATA: HMI 화면(.PGX)·알람 로그(.alog)·HMI 프로젝트 파일
  { label: 'HMI DATA', codes: ['hmi', 'plc'], folder: /HMI/i, name: /HMI|ghost|alarm|알람/i,
    ext: ['pgx', 'alog', 'gtw', 'gtx', 'dpa', 'dvx', 'cmt', 'hkd', 'dbx', 'amm', 'gsh', 'gtd', 'lde', 'mcp', 'rcp', 'ttd', 'adb', 'crl', 'dim', 'fte'] },
  // 9. 커미셔닝 DATA: 시운전·체크시트·화면 캡처(.svg)
  { label: '커미셔닝 DATA', codes: ['commission'], folder: /커미셔닝|commission|시운전|check\s*sheet|체크\s*시트/i, name: /커미셔닝|commission|시운전|체크\s*시트|check\s*sheet/i, ext: ['svg', 'vr'] },
  // 10. 매뉴얼(오타 '메뉴얼' 포함)·취급설명서·교육 자료
  { label: '매뉴얼', codes: ['manual', 'etc'], folder: /[매메]뉴얼|manual|교육\s*자료/i, name: /[매메]뉴얼|manual|취급\s*설명|user\s*guide/i },
  // 11. 회의록·미팅
  { label: '회의록', codes: ['report'], folder: /회의록|미팅|meeting/i, name: /회의록|미팅|meeting|minutes|협의/i },
  // 12. 완료보고서·교육 실시 보고서·작업일보
  { label: '완료보고서', codes: ['final', 'report'], folder: /완료\s*보고/, name: /완료\s*보고|실시\s*보고|결과\s*보고|작업\s*일보/i },
  // 13. 기타자료: 현장 사진(HEIC·카톡 사진)
  { label: '사진', codes: ['etc'], name: /kakaotalk|^IMG[_-]|^\d{8}_\d{6}/i, ext: ['heic'] },
  // 현장 백업: SRS 백업·로봇 컨트롤러 백업(CMOS·ALL.PRM)·이름에 백업
  { label: '현장 백업', codes: ['backup', 'etc'], folder: /백업|backup|STORAGE/i, name: /SRS[_\s-]*BACKUP|백업|backup|CMOS|ALL\.PRM/i },
]

/** NAS 프로젝트 폴더를 통째로 끌어다 놓았을 때: 'N. 이름' 폴더 번호 → 종류(사람이 이미 나눈 칸이라 가장 믿는다) */
const SLOT_CODE: Record<number, string> = { 1: 'quote', 2: 'spec', 3: 'schedule', 4: 'design', 5: 'modula', 6: 'teach', 7: 'plc', 8: 'hmi', 9: 'commission', 10: 'manual', 11: 'report', 12: 'final', 13: 'etc' }
/** 번호만 믿으면 틀리는 옛 배치(예: '1. 사양서', '2. 도면')를 걸러 내려고 이름 낱말도 같이 본다 */
const SLOT_NAME: Record<number, RegExp> = { 1: /제안/, 2: /사양/, 3: /수행|계획/, 4: /도면/, 5: /모듈/, 6: /티칭/, 7: /PLC|PLA/i, 8: /HMI/i, 9: /커미셔닝/, 10: /[매메]뉴얼/, 11: /회의록/, 12: /완료/, 13: /기타/ }

/**
 * 종류 도움말(올릴 때·필터에 보인다). 근거: NAS 프로젝트 폴더 13칸 실제 내용 조사(docs/project-docs).
 * what = 무엇을 넣나, ex = 실제로 들어 있던 파일 예
 */
export const CATEGORY_HELP: Record<string, { what: string; ex: string }> = {
  quote: { what: '수주 전 자료 — 제안서·견적·입찰, 공정 검토, 레이아웃 시뮬레이션(OLP·모토심)', ex: '공정 검토_260622.pptx, LAYOUT SIMULATION 검토.pptx, 견적서' },
  spec: { what: '고객 사양서와 우리 사양정리·사양설명회 자료', ex: '[설비사양서]…pdf, 사양서_광명1….pptx, 수주설명회 자료' },
  schedule: { what: '공사 수행 계획서, 킥오프, 일정표', ex: '공사수행계획서_20260317.pptx, KICK-OFF.pptx, 킥오프미팅.xlsx' },
  design: { what: '레이아웃·기계·전기 도면(CAD와 도면 pdf), 로봇 배치', ex: '.dwg·.sldprt·.step, BOOTH LAYOUT, 배관도·ASSY pdf' },
  modula: { what: '모듈라 빌드(OLT) 자료', ex: 'OLT 설치 LAYOUT.dwg, 모듈라 백업자료.zip, OLT 사진·영상' },
  teach: { what: '로봇에 가르친 동작(JOB)·로봇 설정·도장 조건, 경로용 차체 3D', ex: '.JBI(JOB), .CND·.DAT·.PRM, Paint Studio 설정 csv, 차체 .igs·.hsf' },
  plc: { what: 'PLC 프로그램, 도장기 컨트롤러(EVO) 백업, IP 리스트', ex: 'GX Works .gxw·.gpj, LS .xgdb, EVO 설정, IP 리스트.xlsx' },
  hmi: { what: '현장 조작 화면(HMI) 프로젝트와 알람 기록', ex: '화면 .PGX(ALARM·GHOST·INTERLOCK…), 알람 로그 .alog' },
  commission: { what: '시운전(커미셔닝) 기간 결과물 — 조정한 테이블·최종 티칭·체크시트·인수인계', ex: '커미셔닝 데이터.zip, 페인트 테이블 csv, 체크시트, 화면 캡처 .svg' },
  manual: { what: '장비·시스템 매뉴얼, 취급설명서, 교육 자료', ex: 'DX200_취급설명서.pdf, 서보 드라이브 매뉴얼, 교육 교재' },
  report: { what: '회의록·미팅·협의 기록', ex: '[회의록]….pptx, weekly meeting.xlsx, 승인 협의 요구안' },
  final: { what: '완료보고서, 교육 실시 보고서, 작업일보', ex: '교육실시보고서.pdf, 작업일보_20260809.pptx' },
  etc: { what: '위 칸에 맞지 않는 자료, 현장 사진', ex: '카톡 사진·HEIC, 참고 자료' },
  backup: { what: '시운전 뒤 주기적으로 뜨는 현장 백업(커미셔닝 결과물과 구분)', ex: 'SRS_BACKUP_*.zip+.ini, 로봇 CMOS·ALL.PRM, PLC·HMI 백업 폴더' },
}

/** 분류 코드 → 그 자료가 보통 속하는 단계 코드. 단계가 기록되지 않은 예전 파일을 단계 보기에 넣을 때도 쓴다. */
export const CATEGORY_STAGE: Record<string, string> = {
  proposal: 'opportunity',
  spec: 'opportunity',
  quote: 'opportunity',
  contract: 'order',
  drawing: 'design',
  design: 'design',
  robot_sim: 'design',
  electric: 'design',
  plc: 'design',
  hmi: 'design',
  modula: 'modula',
  teach: 'modula',
  commission: 'install',
  backup: 'install',
  manual: 'line',
  line: 'line',
  final: 'line',
}

export interface Suggestion {
  categoryId: number | null
  /** 근거 문구(예: "폴더 '4. 도면' · 확장자 dwg") — 비면 근거 없음 */
  reasons: string[]
}

/** path = 끌어다 놓은 폴더 기준 상대경로(파일 하나면 파일 이름) */
export function suggestCategory(path: string, categories: Code[]): Suggestion {
  const parts = path.split('/')
  const fileName = parts.pop() ?? path
  const folder = parts.join('/')
  const ext = (fileName.includes('.') ? fileName.split('.').pop() ?? '' : '').toLowerCase()
  const pick = (codes: string[]) => codes.map((c) => categories.find((x) => x.code === c)).find(Boolean)

  // NAS 칸 폴더('7. PLC DATA' 등)가 경로에 있으면 그 칸
  for (const p of parts) {
    const m = /^\s*(\d{1,2})\s*\.\s*(.+)$/.exec(p)
    const n = m ? Number(m[1]) : 0
    if (m && SLOT_CODE[n] && SLOT_NAME[n].test(m[2])) {
      const c = pick([SLOT_CODE[n]])
      if (c) return { categoryId: c.id, reasons: [`NAS 칸 폴더 '${p}'`] }
    }
  }

  let best: { rule: Rule; reasons: string[]; score: number } | null = null
  for (const rule of RULES) {
    const reasons: string[] = []
    let score = 0
    // 폴더 > 확장자 > 이름 순으로 무게를 둔다(폴더는 사람이 이미 나눈 것)
    if (rule.folder && folder && rule.folder.test(folder)) { reasons.push(`폴더 '${parts.find((p) => rule.folder!.test(p)) ?? folder}'`); score += 3 }
    if (rule.ext?.includes(ext)) { reasons.push(`확장자 ${ext}`); score += 2 }
    if (rule.name?.test(fileName.replace(/\.[^.]+$/, ''))) { reasons.push(`이름 '${rule.label}'`); score += 1 }
    if (score > (best?.score ?? 0)) best = { rule, reasons, score }
  }
  const cat = best ? pick(best.rule.codes) : undefined
  if (best && cat) return { categoryId: cat.id, reasons: best.reasons }
  return { categoryId: pick(['etc'])?.id ?? categories[0]?.id ?? null, reasons: [] }
}

/** 분류로 단계를 추정. 추정할 수 없으면 fallback(단계 화면에서 올리면 그 단계, 아니면 없음). */
export function suggestStage(categoryCode: string | undefined, stages: Code[], fallback: number | null): number | null {
  const code = categoryCode ? CATEGORY_STAGE[categoryCode] : undefined
  return stages.find((s) => s.code === code)?.id ?? fallback
}
