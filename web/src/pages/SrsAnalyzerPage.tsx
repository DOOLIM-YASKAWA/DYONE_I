import { EmbeddedAppPage } from '../components/EmbeddedAppPage'
export function SrsAnalyzerPage() {
  return (
    <EmbeddedAppPage
      title="SRS Analyzer"
      appKey="srs-analyzer"
      endpoint="/api/integrations/srs-analyzer/token"
    />
  )
}
