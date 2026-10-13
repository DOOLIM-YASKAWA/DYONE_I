import { useSearchParams } from 'react-router-dom'
import { PageHead } from '../components/ui'
import { ModularSection } from './projects/ModularSection'
import { WorksAppSection } from './projects/WorksAppSection'

/**
 * 그룹웨어에서 옮긴 앱을 왼쪽 메뉴에서 바로 여는 화면(프로젝트 화면의 탭과 같은 내용, 전체 프로젝트 대상).
 * 주소의 ?doc=번호로 문서 상세를 바로 연다 — 그룹웨어 변경 알림을 누르면 이 주소로 온다.
 */
function useDocParam() {
  const [params, setParams] = useSearchParams()
  const doc = Number(params.get('doc')) || null
  const clear = () => {
    const p = new URLSearchParams(params)
    p.delete('doc')
    setParams(p, { replace: true })
  }
  return [doc, clear] as const
}

export function ModularPage() {
  const [doc, clear] = useDocParam()
  return (
    <>
      <PageHead eyebrow="GROUPWARE · 376" title="모듈라" sub="모듈라빌드 프로젝트 정보 — 그룹웨어 변경은 1분 안에 반영되고 알림으로 알려 줍니다" />
      <section className="panel">
        <ModularSection projectCode={null} openDocId={doc} onDocClosed={clear} />
      </section>
    </>
  )
}

export function WorksAppPage({ appletId, title }: { appletId: number; title: string }) {
  const [doc, clear] = useDocParam()
  return (
    <>
      <PageHead eyebrow={`GROUPWARE · ${appletId}`} title={title} sub="그룹웨어 변경은 1분 안에 반영되고 알림으로 알려 줍니다" />
      <section className="panel">
        <WorksAppSection appletId={appletId} projectCode={null} openDocId={doc} onDocClosed={clear} />
      </section>
    </>
  )
}
