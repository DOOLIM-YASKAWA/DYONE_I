import { EmbeddedAppPage } from '../components/EmbeddedAppPage'
export function SmartLabPage() {
  return (
    <EmbeddedAppPage
      title="SmartLab"
      appKey="smartlab"
      endpoint="/api/integrations/smartlab/token"
    />
  )
}
