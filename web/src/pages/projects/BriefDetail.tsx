import { useQuery } from '@tanstack/react-query'
import { useEffect } from 'react'
import { createPortal } from 'react-dom'
import { api } from '../../api/client'
import type { Brief, BriefDetail as Detail, BriefFlow, BriefSchedule } from '../../api/projectMainTypes'
import { useToast } from '../../components/Toast'
import '../../styles/brief-detail.css'

/**
 * 오늘의 브리핑 [상세히] — 프로젝트 한 장 요약(B안 가운데 큰 창, 휴대폰은 아래 시트). 시안 docs/시안/main-variants/j-evidence.html(11-05).
 * 지금 상태 문장 · 한눈에(단계 하나 · 모듈라 상태 · ERP 공사 기간) · 문서 일정 · 최근 흐름(NAS 작업 · ERP 발주) · 이 문장은 어떻게 만들었나.
 * 문장·흐름 글만 사내 AI(검사 통과 시)이고 나머지는 DB 값을 그대로 보인다.
 */
const md = (v?: string) => (v ? `${new Date(v).getMonth() + 1}/${new Date(v).getDate()}` : '')
const hm = (v?: string) => (v ? new Date(v).toLocaleTimeString('ko-KR', { hour: '2-digit', minute: '2-digit', hour12: false }) : '')
const ymd = (v?: string) => (v ? v.slice(0, 10) : '')
const day = (v: string) => new Date(`${v.slice(0, 10)}T00:00:00`).getTime()
const KIND: Record<string, string> = { summary: '사양정리', plan: '수행계획서', spec: '사양서' }
const ALERT: Record<string, string> = { late: '지연', soon: '임박', after_sop: '양산 후' }
const TAG_TONE: Record<string, string> = { '꾸준히 작업': 'ok', '늘어남': 'ok', '이어짐': 'ok', '잦아듦': 'warn', '한 번에 올림': 'warn', '가끔 작업': 'mute', '멈춤': 'mute' }

export function BriefDetailModal({ brief, onClose, onOpen }: { brief: Brief; onClose(): void; onOpen(b: Brief): void }) {
  const toast = useToast()
  const d = brief.evidence?.find((e): e is Detail => e.kind === 'detail')
  const sched = useQuery({
    queryKey: ['brief-detail', brief.code],
    queryFn: () => api<BriefSchedule>(`/api/projects/main/brief-detail?code=${encodeURIComponent(brief.code)}`),
  })
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    document.body.style.overflow = 'hidden'
    return () => { window.removeEventListener('keydown', onKey); document.body.style.overflow = '' }
  }, [onClose])

  const copy = (text?: string) => {
    if (!text) return
    void navigator.clipboard?.writeText(text).then(() => toast('NAS 폴더 경로를 복사했습니다.'), () => {})
  }
  const chips = [
    d?.stage.value && `${d.stage.label === 'ERP 상태' ? 'ERP 상태' : '단계'} ${d.stage.value}`,
    d?.modular && `모듈라 상태 ${d.modular.status}`,
    d && d.nas14 > 0 && `NAS 2주 ${d.nas14.toLocaleString()}개`,
    d?.changes[0],
  ].filter(Boolean) as string[]

  return createPortal(
    <div className="pbd">
      <div className="pbd-backdrop" onClick={onClose} />
      <div className="pbd-box" role="dialog" aria-modal="true" aria-label={`${brief.name} 상세히`}>
        <header className="pbd-h">
          <div>
            <h3>{brief.name}</h3>
            <small>{[brief.codeLabel, sched.data?.partner, d?.process].filter(Boolean).join(' · ')}</small>
          </div>
          <button className="pbd-x" aria-label="닫기" onClick={onClose}>×</button>
        </header>
        <div className="pbd-b">
          <div className="pbd-col">
            <section className="pbd-sec">
              <h4>지금 상태</h4>
              <div className="pbd-say">
                <p>{brief.sentence}</p>
                <div className="pbd-meta">
                  <span className={`pbd-tag ${brief.source === 'ai' ? 'ai' : 'mute'}`}>{brief.source === 'ai' ? '사내 AI 문장' : '규칙 문장'}</span>
                  {md(brief.madeAt)} {hm(brief.madeAt)} 만듦{brief.changedYesterday > 0 && ` · 어제 바뀐 것 +${brief.changedYesterday.toLocaleString()}`}
                </div>
                {chips.length > 0 && <div className="pbd-chk">{chips.map((c) => <span key={c}>{c}</span>)}</div>}
              </div>
            </section>

            {d && (
              <section className="pbd-sec">
                <h4>한눈에</h4>
                <div className={`pbd-facts${d.modular ? '' : ' one'}`}>
                  <div className="pbd-fact">
                    <label>{d.stage.label}</label>
                    <b>{d.stage.value ?? '–'}</b>
                    <small>{d.stage.label === 'ERP 상태' ? 'DY ONE에서 정한 단계 없음 · ERP 영업 단계' : [d.stage.others?.length ? `${d.stage.others.join('·')}도 진행 중` : '', d.stage.changedAt ? `${md(d.stage.changedAt)} 바꿈` : ''].filter(Boolean).join(' · ') || 'DY ONE에서 정한 단계'}</small>
                  </div>
                  {d.modular && (
                    <div className="pbd-fact">
                      <label>모듈라 상태</label>
                      <b>{d.modular.status}</b>
                      <small>그룹웨어 모듈라{d.modular.lastAt ? ` · ${md(d.modular.lastAt)} 마지막 글` : ''}</small>
                    </div>
                  )}
                  {d.erp.from && d.erp.to && <ErpTerm from={d.erp.from} to={d.erp.to} />}
                </div>
              </section>
            )}

            <section className="pbd-sec">
              <h4>문서 일정 <i>사용자가 올린 사양정리·수행계획서·사양서에서 읽음 · 검은 선 = 오늘</i></h4>
              {sched.isLoading && <div className="pbd-empty">불러오는 중…</div>}
              {sched.data && sched.data.schedule.length === 0 && <div className="pbd-empty">문서에서 읽은 일정이 없습니다. NAS '2. 사양서'·'3. 수행 계획서'에 문서가 없거나 일정표를 찾지 못했습니다.</div>}
              {sched.data && sched.data.schedule.length > 0 && <Timeline data={sched.data} />}
            </section>
          </div>

          <div className="pbd-col">
            <section className="pbd-sec">
              <h4>최근 흐름 {d?.nasDir && <button className="pbd-link" onClick={() => copy(d.nasDir)}>NAS 폴더 경로 복사</button>}</h4>
              <div className="pbd-flow">
                <FlowBox title="NAS 작업 폴더" sub={d && d.nas14 > 0 ? `최근 2주 · 파일을 고친 날 ${d.nasDays14}일` : '최근 2주'} flow={d?.nas}
                  empty="최근 2주 동안 작업 폴더(2~9번)에서 고친 파일이 없습니다." />
                <FlowBox title="ERP 발주" sub={`최근 3달${d?.lastPo ? ` · 마지막 ${md(d.lastPo)}` : ''}`} flow={d?.po} empty="최근 3달 발주가 없습니다." />
              </div>
            </section>
            <section className="pbd-sec">
              <h4>이 문장은 어떻게 만들었나</h4>
              <div className="pbd-how">
                {brief.source === 'ai'
                  ? <>사내 AI가 <b>단계 · 모듈라 상태 · 최근 흐름 · 최근 바뀐 이력(단계 변경·모듈라 글)</b>만 보고 정해진 틀에 맞춰 썼습니다. 문장 속 숫자가 모두 넘겨준 사실 안에 있어 통과했습니다.</>
                  : <>사내 AI 문장이 없거나 검사에 걸려 <b>규칙 문장</b>으로 대신했습니다.</>}
                {' '}흐름 글은 {[d?.nas, d?.po].some((f) => f?.source === 'ai') ? '사내 AI가 묶음마다 제목을 붙였습니다' : '규칙으로 만들었습니다'}. 값이 바뀌지 않으면 다음 날 다시 쓰지 않습니다. 매일 06:30.
              </div>
            </section>
          </div>
        </div>
        <footer className="pbd-f">
          {brief.projectId && <button className="pbd-btn pri" onClick={() => onOpen(brief)}>프로젝트 상세 열기 →</button>}
        </footer>
      </div>
    </div>,
    document.body,
  )
}

function ErpTerm({ from, to }: { from: string; to: string }) {
  const a = day(from), b = day(to), now = Date.now()
  const pct = Math.max(0, Math.min(100, ((now - a) / Math.max(1, b - a)) * 100))
  const months = (x: number, y: number) => Math.max(0, Math.round((y - x) / (30.44 * 86400000)))
  const over = now > b
  return (
    <div className="pbd-fact wide">
      <label>ERP 공사 기간 <i>영업이 수주 때 적은 예상 기간</i></label>
      <div className="pbd-term"><i style={{ width: `${pct}%` }} />{!over && <em style={{ left: `${pct}%` }} />}</div>
      <div className="pbd-term-l">
        <span>{ymd(from)}</span>
        <span>{over ? <span className="pbd-tag warn">기간 지남</span> : now < a ? '시작 전' : `오늘 · ${months(a, b)}개월 중 ${months(a, now) + 1}개월째`}</span>
        <span>{ymd(to)}</span>
      </div>
    </div>
  )
}

function Timeline({ data }: { data: BriefSchedule }) {
  const pts = data.schedule
  // 단계는 같은 간격으로 놓고(글자 겹침 방지), 오늘 선은 앞뒤 단계 사이에 날짜 비율로 넣는다
  const now = Date.now()
  const pos = (i: number) => (pts.length <= 1 ? 50 : 4 + (i / (pts.length - 1)) * 92)
  const ends = pts.map((p) => day(p.to))
  const after = ends.findIndex((t) => t >= now)
  const todayPos = after === -1 ? 99 : after === 0 ? 1 : pos(after - 1) + ((now - ends[after - 1]) / Math.max(1, ends[after] - ends[after - 1])) * (pos(after) - pos(after - 1))
  const lateStages = new Set(data.alerts.filter((a) => a.kind === 'late').map((a) => a.stage))
  // 달 전체(1일~말일)면 '26/10', 하루면 '26/10/15', 기간이면 끝 날짜
  const label = (p: { from: string; to: string }) => {
    const a = new Date(`${p.from}T00:00:00`), b = new Date(`${p.to}T00:00:00`)
    const whole = a.getDate() === 1 && new Date(b.getFullYear(), b.getMonth() + 1, 0).getDate() === b.getDate() && a.getMonth() === b.getMonth() && a.getFullYear() === b.getFullYear()
    return `${String(b.getFullYear()).slice(2)}/${b.getMonth() + 1}${whole ? '' : `/${b.getDate()}`}`
  }
  const SHORT: Record<string, string> = { '현장 설치': '설치' }
  // 출처: 가장 많이 쓴 문서
  const top = [...pts.reduce((m, p) => m.set(p.doc, (m.get(p.doc) ?? 0) + 1), new Map<string, number>())].sort((x, y) => y[1] - x[1])[0]?.[0]
  const src = pts.find((p) => p.doc === top)
  const lines = [...new Set(pts.map((p) => p.raw))]
  return (
    <>
      <div className="pbd-tl">
        {pts.map((p, i) => (
          <span key={p.stage} className={`pbd-p${lateStages.has(p.stage) ? ' bad' : ends[i] < now ? ' past' : ''}`} style={{ left: `${pos(i)}%` }} title={`${p.name} ${p.from}~${p.to}`}>
            <b>{SHORT[p.name] ?? p.name}</b><small>{label(p)}</small>
          </span>
        ))}
        <span className="pbd-today" style={{ left: `${todayPos}%` }} />
      </div>
      {src && (
        <div className="pbd-src">출처 <code>{src.doc}</code>{src.page && ` · ${src.page}`} · {KIND[src.kind] ?? src.kind}{src.mtime && ` · ${md(src.mtime)} 저장`}{pts.some((p) => p.doc !== top) && ' 외'}</div>
      )}
      <details className="pbd-raw"><summary>원문 줄 보기 ({lines.length})</summary>{lines.map((l) => <p key={l}>{l}</p>)}</details>
      {data.alerts.map((a) => (
        <div key={a.kind + a.stage} className={`pbd-alert ${a.kind}`}><span className="pbd-tag">{ALERT[a.kind] ?? a.kind}</span>{a.reason.replace(/'\d+\.\s*/g, "'")}</div>
      ))}
    </>
  )
}

function FlowBox({ title, sub, flow, empty }: { title: string; sub: string; flow?: BriefFlow | null; empty: string }) {
  return (
    <div className="pbd-box2">
      <div className="pbd-box-h">{title} <small>{sub}</small></div>
      {!flow ? <div className="pbd-empty">{empty}</div> : (
        <>
          <p className="pbd-trend"><span className={`pbd-tag ${TAG_TONE[flow.tag] ?? 'mute'}`}>{flow.tag}</span>{flow.trend}</p>
          <ol className="pbd-steps">
            {flow.steps.map((s, i) => (
              <li key={i} className={i === flow.steps.length - 1 ? 'now' : ''}>
                <time>{s.when}</time>
                <div><b>{s.title}</b>{s.detail && <small>{s.detail}</small>}</div>
              </li>
            ))}
          </ol>
        </>
      )}
    </div>
  )
}
