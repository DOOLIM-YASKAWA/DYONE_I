import { useState } from 'react'
import { useAuth } from '../auth/AuthContext'
import { NotConnected } from '../components/NotConnected'
import { PageHead } from '../components/ui'

// IT 요청 · 방문 차량은 dyone DB에 테이블이 없다. DY ONE 전용 스키마가 확정되면 화면을 연결한다. (식단은 MealsPage.tsx — 수담푸드 메뉴)

export function ItPage() {
  return (
    <>
      <PageHead eyebrow="IT SERVICE DESK" title="IT 요청" sub="장비·계정·소프트웨어·네트워크 요청과 처리 현황" />
      <section className="panel">
        <NotConnected what="IT 요청" />
      </section>
    </>
  )
}

export function VehiclesPage() {
  return (
    <>
      <PageHead eyebrow="VISITOR PARKING" title="방문 차량" sub="방문 일정과 차량을 미리 등록하세요" />
      <div className="notice">아이파킹 연동 전 · 방문 차량 등록은 아직 사용할 수 없습니다.</div>
      <section className="panel">
        <NotConnected what="방문 차량" />
      </section>
    </>
  )
}

/** 모바일 출입: 에스원 회신 후 결정(① 전용 앱 링크 / ② SDK 연동). 지금은 화면 자리만. */
export function AccessPage() {
  const { user } = useAuth()
  const [active, setActive] = useState(true)
  const [result, setResult] = useState<string | null>(null)
  return (
    <>
      <PageHead eyebrow="MOBILE ACCESS" title="모바일 출입" sub="모바일 사원증과 출입 상태" />
      <div className="notice">SECOM 연동 방식 결정 전(에스원 회신 대기) · 실제 NFC/BLE 인증이나 카드 발급은 하지 않습니다.</div>
      <div className="pass-layout">
        <div className="pass-card">
          <div className="pass-brand">
            DY ONE <span>MOBILE ID · 준비 중</span>
          </div>
          <div className="nfc">⌁</div>
          <h2>{user?.displayName}</h2>
          <p>
            DOOLIM-YASKAWA · {user?.department} · {user?.empNo}
          </p>
          <footer>
            <span>{active ? '사용 가능 · 예시' : '사용 중지 · 예시'}</span>
            <span>모바일 사원증</span>
          </footer>
        </div>
        <section className="panel pass-instructions">
          <h2>출입 인증</h2>
          <p>
            실제 서비스에서는 에스원 지원 단말기와 앱 인증 방식에 맞춰 출입하게 됩니다. 전용 앱 링크 방식인지 SDK 연동 방식인지는 에스원 회신 후
            결정합니다.
          </p>
          <button className="primary" onClick={() => setResult(active ? '✓ 인증 흐름 예시 · 본사 정문 (실제 출입 이력 아님)' : '카드가 사용 중지 상태입니다.')}>
            출입 인증 체험
          </button>
          <button className="secondary" onClick={() => setActive((v) => !v)}>
            {active ? '카드 사용 중지 체험' : '카드 활성화 체험'}
          </button>
          {result && (
            <div className="access-result" aria-live="polite">
              {result}
            </div>
          )}
        </section>
      </div>
    </>
  )
}
