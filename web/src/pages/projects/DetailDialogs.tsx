import { useQuery } from '@tanstack/react-query'
import { api } from '../../api/client'
import type { LabTest, ProjectDetail } from '../../api/types'
import { Loading, Modal } from '../../components/ui'

const date = (v?: string) => (v ? new Date(v).toLocaleDateString('ko-KR') : '-')

function Meta({ items }: { items: [string, string | number | undefined | null, boolean?][] }) {
  return (
    <dl className="detail-meta">
      {items.map(([label, value, full]) => (
        <div key={label} className={full ? 'full' : ''}>
          <dt>{label}</dt>
          <dd>{value === undefined || value === null || value === '' ? '-' : String(value)}</dd>
        </div>
      ))}
    </dl>
  )
}

/** 프로젝트 상세(읽기 전용). 원천: project.project + 공정 배당 + 앱 소속 */
export function ProjectDetailDialog({ projectId, onClose }: { projectId: number; onClose(): void }) {
  const { data } = useQuery({ queryKey: ['project', projectId], queryFn: () => api<ProjectDetail>(`/api/projects/${projectId}`) })
  if (!data) return <Modal title="프로젝트" onClose={onClose}><Loading /></Modal>
  const p = data.project
  return (
    <Modal title={p.name} onClose={onClose}>
      <span className="badge blue">{p.code}</span> {p.status && <span className="badge">{p.status}</span>}
      <Meta
        items={[
          ['프로젝트 코드', p.code],
          ['프로젝트 종류', p.type],
          ['발주처', p.partnerName],
          ['프로젝트 상태', p.status ?? '미지정'],
          ['ERP 등록날짜', p.erpRegDate],
          ['시작 · 종료', `${p.startDate ?? '-'} ~ ${p.endDate ?? '-'}`],
          ['앱 소속', data.apps.join(', ') || '없음'],
          ['랩 테스트', `${data.labTestCount}건`],
          ['최근 변경', date(p.updatedAt)],
          ['설명', p.description, true],
        ]}
      />
      <AssignmentList assignments={data.assignments} />
      <p className="notice" style={{ marginTop: 16 }}>
        그룹웨어 applet285 미러 데이터입니다. DY ONE에서는 읽기만 하며, 수정은 원천(그룹웨어)에서 합니다.
      </p>
    </Modal>
  )
}

export function AssignmentList({ assignments }: { assignments: ProjectDetail['assignments'] }) {
  return (
    <div className="update-history">
      <h3>공정 배당 {assignments.length}건</h3>
      {assignments.length === 0 && <p>배당된 공정이 없습니다.</p>}
      {assignments.map((a, i) => (
        <p key={i}>{[a.customer, a.factory, a.booth, a.zone].filter(Boolean).join(' › ') || `(${a.level})`}</p>
      ))}
    </div>
  )
}

/** 랩 테스트 상세(읽기 전용). 원천: smartlab.lab_test */
export function LabDetailDialog({ lab, onClose }: { lab: LabTest; onClose(): void }) {
  return (
    <Modal title={lab.name} onClose={onClose}>
      {lab.projectCode && <span className="badge blue">{lab.projectCode}</span>} {lab.type && <span className="badge">{lab.type}</span>}
      <Meta
        items={[
          ['테스트 명', lab.name, true],
          ['테스트 종류', lab.type],
          ['담당', lab.operator],
          ['연결 프로젝트', lab.projectName ? `${lab.projectCode} · ${lab.projectName}` : '미연결', true],
          ['테스트 시작', date(lab.startedAt)],
          ['테스트 종료', date(lab.endedAt)],
          ['설명', lab.description, true],
        ]}
      />
      <p className="notice" style={{ marginTop: 16 }}>
        SmartLab 데이터입니다. 조건·측정값 상세는 SmartLab에서 확인합니다.
      </p>
    </Modal>
  )
}
