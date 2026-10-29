import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../../api/client'
import type { Code, LabTest, ProjectDetail } from '../../api/types'
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
  const qc = useQueryClient()
  const { data } = useQuery({ queryKey: ['project', projectId], queryFn: () => api<ProjectDetail>(`/api/projects/${projectId}`) })
  const stages = useQuery({ queryKey: ['codes', 'project_stage'], queryFn: () => api<Code[]>('/api/codes/project_stage') })
  const [stageId, setStageId] = useState<number | null>(null)
  const save = useMutation({
    mutationFn: (sid: number) => api(`/api/projects/${projectId}/stage`, { method: 'PUT', json: { stageId: sid } }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['project', projectId] })
      qc.invalidateQueries({ queryKey: ['home'] })
      qc.invalidateQueries({ queryKey: ['projects'] })
      setStageId(null)
    },
  })
  if (!data) return <Modal title="프로젝트" onClose={onClose}><Loading /></Modal>
  const p = data.project
  // 현재 단계 기본값: dyone 단계 있으면 그것, 없으면 그룹웨어 상태 매핑(영업중→영업, 수주→수주)
  const gwCode = p.status === '영업중' ? 'opportunity' : p.status === '수주' ? 'order' : null
  const defaultStageId = data.dyoneStage?.stageId ?? stages.data?.find((s) => s.code === gwCode)?.id ?? null
  const selected = stageId ?? defaultStageId
  const curIdx = stages.data?.findIndex((s) => s.id === selected) ?? -1
  return (
    <Modal title={p.name} onClose={onClose}>
      <span className="badge blue">{p.code}</span> {p.status && <span className="badge">{p.status}</span>}

      <p style={{ margin: '16px 0 7px', fontSize: 13, color: 'var(--muted)' }}>예상 현재 진행 상황</p>
      <div className="step-track">
        {stages.data?.map((s, i) => (
          <div key={s.id} className={i === curIdx ? 'active' : curIdx >= 0 && i < curIdx ? 'done' : ''}>
            {s.name}
            {i === curIdx && <small>현재</small>}
          </div>
        ))}
      </div>

      <div className="notice" style={{ marginTop: 14, display: 'grid', gap: 12 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <span style={{ color: 'var(--muted)', minWidth: 96 }}>그룹웨어 상태</span>
          <span className="badge">{p.status ?? '미지정'}</span>
          <span style={{ color: 'var(--muted)', fontSize: 12 }}>읽기 전용</span>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
          <span style={{ color: 'var(--muted)', minWidth: 96 }}>진행 단계</span>
          <select value={selected ?? ''} onChange={(e) => setStageId(Number(e.target.value))} style={{ minWidth: 150 }}>
            {stages.data?.map((s) => (
              <option key={s.id} value={s.id}>{s.name}</option>
            ))}
          </select>
          <button className="primary" disabled={save.isPending || selected == null} onClick={() => selected != null && save.mutate(selected)}>
            {save.isPending ? '저장 중…' : '저장'}
          </button>
          {data.dyoneStage && (
            <small style={{ color: 'var(--muted)' }}>변경: {data.dyoneStage.updatedByName ?? '-'} · {date(data.dyoneStage.updatedAt)}</small>
          )}
        </div>
      </div>
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
          ['최근 변경', p.lastChangedAt ? `${date(p.lastChangedAt)} (${p.lastChangedSource ?? '변경'})` : '-'],
          ['설명', p.description, true],
        ]}
      />
      <AssignmentList assignments={data.assignments} />
      <p className="notice" style={{ marginTop: 16 }}>
        그룹웨어 상태(영업중/수주)는 원천(applet285) 미러라 읽기 전용입니다. 수주 이후 진행 단계는 DY ONE에서 직접 관리합니다.
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
