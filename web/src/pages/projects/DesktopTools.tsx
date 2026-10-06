import { useState } from 'react'
import type { Project } from '../../api/types'
import { platform } from '../../platform/bridge'

/**
 * Cell · MotoSim · Smart Cell Plugin 기능. 로컬 PC 작업이라 Windows 앱(Local Broker)에서만 동작한다.
 * 지금은 Broker 골격: 폴더 선택·Plugin 상태 확인은 실제 동작, 업로드·MotoSim 실행은 자리만 있다.
 */
export function DesktopTools({ project }: { project: Project | null }) {
  const [result, setResult] = useState<string | null>(null)

  if (platform.kind !== 'desktop') {
    return (
      <section className="panel" style={{ marginTop: 22 }}>
        <div className="panel-head">
          <div>
            <h2>Cell · MotoSim</h2>
            <p>Cell 업로드·다운로드, MotoSim 실행, Smart Cell Plugin 관리는 설치형 앱(DY ONE Desktop)에서 사용할 수 있습니다.</p>
          </div>
        </div>
      </section>
    )
  }

  const run = async (cmd: string, args?: Record<string, unknown>) => {
    try {
      setResult(JSON.stringify(await platform.broker(cmd, args), null, 2))
    } catch (e) {
      setResult(`❌ ${e instanceof Error ? e.message : String(e)}`)
    }
  }

  return (
    <section className="panel" style={{ marginTop: 22 }}>
      <div className="panel-head">
        <div>
          <h2>Cell · MotoSim · 설치형 앱 기능</h2>
          <p>{project ? `${project.name} · ` : ''}Local Broker를 통해 이 PC에서 실행됩니다.</p>
        </div>
      </div>
      <div className="desktop-tools">
        <button className="secondary" onClick={() => run('cell.pickFolder')}>
          Cell 폴더 선택 (.vcl 확인)
        </button>
        <button className="secondary" onClick={() => run('smartcell.status')}>
          Smart Cell Plugin 상태
        </button>
        <button className="secondary" onClick={() => run('motosim.status')}>
          MotoSim 설치 확인
        </button>
        <button className="secondary" onClick={() => run('cell.upload', { projectId: project?.id })}>
          Cell 업로드 (준비 중)
        </button>
      </div>
      {result && <pre className="broker-result">{result}</pre>}
    </section>
  )
}
