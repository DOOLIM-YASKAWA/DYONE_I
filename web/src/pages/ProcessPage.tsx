import { EmbeddedAppPage } from '../components/EmbeddedAppPage'
export function ProcessPage() {
  return (
    <EmbeddedAppPage
      title="공정 관리"
      appKey="process"
      endpoint="/api/integrations/process/token"
      waitForReady={false}
    />
  )
}
