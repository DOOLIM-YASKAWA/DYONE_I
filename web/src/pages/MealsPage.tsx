import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../api/client'
import { Empty, ErrorBox, Loading, PageHead } from '../components/ui'
import '../styles/meals.css'

// 수담푸드(구내식당) 주간 점심 식단. Worker(SudamMenuJob)가 네이버 QR 메뉴판 이미지를 사내 AI로 읽어 캐시 파일에 쓰고,
// API(/api/meals/sudam)는 그 파일을 그대로 준다. 글자를 못 읽은 주는 원본 이미지(요일별 한 장)를 대신 보여 준다.

interface MenuItem { name: string; main: boolean }
interface MenuDay { date: string; items: MenuItem[]; imageUrl?: string | null }
interface SudamMenu {
  sourceUrl: string | null
  checkedAt: string | null
  uploadedOn?: string | null
  images: string[]
  extracted: boolean
  extractError?: string | null
  days: MenuDay[]
}

const WEEKDAY = ['일', '월', '화', '수', '목', '금', '토']
const iso = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
const parse = (s: string) => new Date(`${s}T00:00:00`)
const md = (s: string) => { const d = parse(s); return `${d.getMonth() + 1}/${d.getDate()}` }
const fmtWhen = (s: string | null | undefined) =>
  s ? new Date(s).toLocaleString('ko-KR', { month: 'numeric', day: 'numeric', weekday: 'short', hour: '2-digit', minute: '2-digit' }) : '-'

/** 글자를 못 읽었을 때: 업로드 다음 월요일부터 월~금에 2번째 이후 이미지(요일별 한 장)를 붙인다. 1번째는 주간표. */
function imageDays(m: SudamMenu): MenuDay[] {
  if (!m.uploadedOn || m.images.length < 6) return []
  const monday = parse(m.uploadedOn)
  monday.setDate(monday.getDate() + ((8 - monday.getDay()) % 7))
  return m.images.slice(1, 6).map((url, i) => {
    const d = new Date(monday)
    d.setDate(monday.getDate() + i)
    return { date: iso(d), items: [], imageUrl: url }
  })
}

export function MealsPage() {
  const { data, isLoading, error } = useQuery({ queryKey: ['meals', 'sudam'], queryFn: () => api<SudamMenu>('/api/meals/sudam') })
  const [picked, setPicked] = useState<string | null>(null)
  const [showImage, setShowImage] = useState(false)
  const [today] = useState(() => iso(new Date()))

  const days = data ? (data.days.length > 0 ? data.days : imageDays(data)) : []
  // 기본 선택: 오늘 → 다음 식단일 → 마지막 날
  const selected = days.find((d) => d.date === picked) ?? days.find((d) => d.date >= today) ?? days[days.length - 1]
  const textMode = days.some((d) => d.items.length > 0)
  const range = days.length > 0 ? `${md(days[0].date)} ~ ${md(days[days.length - 1].date)}` : ''

  return (
    <>
      <PageHead
        eyebrow="SUDAM FOOD"
        title="수담푸드 메뉴"
        sub={`구내식당 주간 점심 식단${range ? ` · ${range}` : ''}`}
      />
      {isLoading && <Loading />}
      {error && <ErrorBox error={error} />}
      {data && days.length === 0 && (
        <section className="panel">
          <Empty>{data.checkedAt ? '이번 주 식단을 읽지 못했습니다. 잠시 뒤 다시 확인해 주세요.' : '아직 식단을 가져오지 않았습니다. Worker가 매일 06:30에 확인합니다.'}</Empty>
        </section>
      )}
      {data && selected && (
        <>
          {!textMode && <div className="notice">메뉴 글자를 아직 읽지 못해 업체가 올린 이미지를 그대로 보여 드립니다.</div>}
          <div className="meal-days">
            {days.map((d) => (
              <button key={d.date} className={d.date === selected.date ? 'active' : ''} onClick={() => { setPicked(d.date); setShowImage(false) }}>
                {WEEKDAY[parse(d.date).getDay()]} {md(d.date)}
                {d.date === today && <em className="meal-today">오늘</em>}
              </button>
            ))}
          </div>

          <div className="meal-layout">
            <section className="panel meal-card">
              <div className="meal-head">
                <div>
                  <small>{selected.date === today ? '오늘의 점심' : '점심'}</small>
                  <h2>
                    {parse(selected.date).getMonth() + 1}월 {parse(selected.date).getDate()}일 {WEEKDAY[parse(selected.date).getDay()]}요일
                  </h2>
                </div>
                {selected.items.filter((i) => i.main).map((i) => (
                  <span key={i.name} className="meal-main-chip">{i.name}</span>
                ))}
              </div>
              {selected.items.length > 0 && !showImage ? (
                <ul className="meal-list">
                  {selected.items.map((i, n) => (
                    <li key={`${i.name}-${n}`} className={i.main ? 'main' : ''}>
                      {i.name}
                      {i.main && <small>메인</small>}
                    </li>
                  ))}
                </ul>
              ) : selected.imageUrl ? (
                <img className="meal-image" src={selected.imageUrl} alt={`${md(selected.date)} 식단 이미지`} />
              ) : (
                <Empty>이 날 메뉴가 없습니다.</Empty>
              )}
              <div className="meal-meta">
                <span>수담푸드 · {data.uploadedOn ? `${md(data.uploadedOn)} 업로드` : ''} · {fmtWhen(data.checkedAt)} 확인</span>
                {selected.items.length > 0 && selected.imageUrl && (
                  <button className="text-button" onClick={() => setShowImage((v) => !v)}>
                    {showImage ? '글자로 보기' : '원본 이미지'}
                  </button>
                )}
              </div>
            </section>

            {textMode && (
              <section className="panel">
                <div className="panel-head">
                  <div>
                    <h2>이번 주 한눈에</h2>
                    <p>굵은 글씨가 메인 메뉴입니다. 식자재 수급에 따라 메뉴가 바뀔 수 있습니다.</p>
                  </div>
                </div>
                <div className="meal-week">
                  {days.map((d) => (
                    <button key={d.date} className={`meal-week-day${d.date === selected.date ? ' active' : ''}${d.date === today ? ' today' : ''}`} onClick={() => { setPicked(d.date); setShowImage(false) }}>
                      <strong>{WEEKDAY[parse(d.date).getDay()]} <small>{md(d.date)}</small></strong>
                      {d.items.map((i, n) => (
                        <span key={`${i.name}-${n}`} className={i.main ? 'main' : ''}>{i.name}</span>
                      ))}
                    </button>
                  ))}
                </div>
              </section>
            )}
          </div>
          {textMode && <p className="meal-footnote">메뉴 글자는 사내 AI가 업체 식단 이미지를 읽은 것이라 틀릴 수 있습니다. 이상하면 '원본 이미지'로 확인해 주세요.</p>}
        </>
      )}
    </>
  )
}
