import { PageHead } from '../components/ui'

/** 아직 내용이 없는 메뉴(11-05: 다운로드 · 자료 검색 · AI 두리미). 메뉴 자리만 먼저 둔다. */
export function ComingSoonPage({ title }: { title: string }) {
  return (
    <>
      <PageHead eyebrow="DY ONE" title={title} sub="준비 중인 화면입니다." />
      <section className="panel" style={{ padding: '48px 24px', textAlign: 'center', color: 'var(--muted, #56637C)' }}>
        <b style={{ display: 'block', fontSize: 16, color: 'var(--ink, #0C1A33)', marginBottom: 6 }}>{title} 화면을 준비하고 있습니다.</b>
        곧 이곳에서 쓸 수 있습니다.
      </section>
    </>
  )
}
