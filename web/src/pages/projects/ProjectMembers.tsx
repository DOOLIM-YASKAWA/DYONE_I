import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../../api/client'
import type { ProjectMember } from '../../api/orgTypes'
import { EmployeePicker } from '../../components/org/EmployeePicker'
import { jobTitle, Phone } from '../../components/org/OrgTree'
import { ErrorBox, Loading } from '../../components/ui'

const date = (v?: string) => (v ? new Date(v).toLocaleDateString('ko-KR') : '-')

export const useProjectMembers = (projectId: number) =>
  useQuery({ queryKey: ['project-members', projectId], queryFn: () => api<ProjectMember[]>(`/api/projects/${projectId}/members`) })

/**
 * 프로젝트 인원 탭: PM 1명 + 투입 인원. 사람은 조직도에서 고른다.
 * PM을 바꾸면 기존 PM은 투입 인원으로 남는다(필요 없으면 '빼기').
 */
export function ProjectMembersTab({ projectId }: { projectId: number }) {
  const qc = useQueryClient()
  const members = useProjectMembers(projectId)
  const [picker, setPicker] = useState<'pm' | 'member' | null>(null)
  const [editing, setEditing] = useState<{ id: number; task: string } | null>(null)

  const refresh = () => {
    qc.invalidateQueries({ queryKey: ['project-members', projectId] })
    qc.invalidateQueries({ queryKey: ['project', projectId] })
    qc.invalidateQueries({ queryKey: ['projects'] })
    qc.invalidateQueries({ queryKey: ['org'] })
  }
  const add = useMutation({
    mutationFn: (v: { employeeId: number; role: 'pm' | 'member'; task: string }) =>
      api(`/api/projects/${projectId}/members`, { method: 'POST', json: { employeeId: v.employeeId, role: v.role, task: v.task || null } }),
    onSuccess: () => { setPicker(null); refresh() },
  })
  // 투입 인원 여러 명 한 번에(이미 들어간 사람은 서버가 건너뜀)
  const addMany = useMutation({
    mutationFn: (v: { employeeIds: number[]; task: string }) =>
      api<{ added: number; skipped: number }>(`/api/projects/${projectId}/members/bulk`, { method: 'POST', json: { employeeIds: v.employeeIds, task: v.task || null } }),
    onSuccess: () => { setPicker(null); refresh() },
  })
  const update = useMutation({
    mutationFn: (v: { id: number; task: string }) => api(`/api/projects/${projectId}/members/${v.id}`, { method: 'PATCH', json: { task: v.task || null } }),
    onSuccess: () => { setEditing(null); refresh() },
  })
  const remove = useMutation({
    mutationFn: (id: number) => api(`/api/projects/${projectId}/members/${id}`, { method: 'DELETE' }),
    onSuccess: refresh,
  })

  if (members.isLoading) return <Loading />
  if (members.error) return <ErrorBox error={members.error} />
  const list = members.data ?? []
  const pm = list.find((m) => m.role === 'pm')
  const team = list.filter((m) => m.role === 'member')
  // 투입 인원 추가: 이미 들어간 사람은 못 고른다 / PM 지정: 지금 PM만 못 고른다(투입 인원은 PM으로 올릴 수 있다)
  const exclude = new Map<number, string>(
    picker === 'pm' ? (pm ? [[pm.employeeId, '지금 PM']] : []) : list.map((m) => [m.employeeId, m.role === 'pm' ? '이 프로젝트 PM' : '이미 투입됨']),
  )

  const taskCell = (m: ProjectMember) =>
    editing?.id === m.id ? (
      <form className="member-task-edit" onSubmit={(e) => { e.preventDefault(); update.mutate(editing) }}>
        <input autoFocus value={editing.task} maxLength={100} onChange={(e) => setEditing({ id: m.id, task: e.target.value })} aria-label="맡은 일" />
        <button className="text-button" disabled={update.isPending}>저장</button>
        <button type="button" className="text-button" onClick={() => setEditing(null)}>취소</button>
      </form>
    ) : (
      <button className="member-task" onClick={() => setEditing({ id: m.id, task: m.task ?? '' })} title="눌러서 맡은 일 고치기">
        {m.task || <span className="muted">맡은 일 입력</span>}
      </button>
    )

  return (
    <div className="members">
      <div className="member-pm">
        <div>
          <span className="member-label">PM</span>
          {pm ? (
            <div className="member-person">
              <span className="avatar">{pm.name.slice(0, 1)}</span>
              <div>
                <b>{pm.name}</b> <small>{jobTitle(pm.position, pm.duty)}</small>
                <small className="member-sub">
                  {pm.department ?? '-'}
                  {pm.mobile && <> · <Phone value={pm.mobile} label="휴대폰 " /></>}
                  {pm.directTel && <> · <Phone value={pm.directTel} label="직통 " /></>}
                  {pm.email ? ` · ${pm.email}` : ''}
                </small>
                <small className="member-sub">{taskCell(pm)} · {date(pm.assignedAt)} 지정{pm.assignedByName ? ` (${pm.assignedByName})` : ''}</small>
              </div>
            </div>
          ) : (
            <p className="muted" style={{ margin: '6px 0 0' }}>아직 PM이 지정되지 않았습니다.</p>
          )}
        </div>
        <div className="member-actions">
          <button className="primary" onClick={() => { add.reset(); setPicker('pm') }}>{pm ? 'PM 변경' : 'PM 지정'}</button>
          {pm && (
            <button className="text-button danger" disabled={remove.isPending}
              onClick={() => confirm(`${pm.name}님을 PM에서 해제할까요? (프로젝트 인원에서도 빠집니다)`) && remove.mutate(pm.id)}>
              해제
            </button>
          )}
        </div>
      </div>

      <div className="member-head">
        <h3>투입 인원 <small>{team.length}명</small></h3>
        <button className="secondary" onClick={() => { addMany.reset(); setPicker('member') }}>+ 인원 추가</button>
      </div>
      {team.length === 0 ? (
        <div className="empty" style={{ padding: 24 }}>투입 인원이 없습니다. '인원 추가'로 조직도에서 고르세요.</div>
      ) : (
        <div className="data-wrap">
          <table>
            <thead>
              <tr><th>이름</th><th>부서</th><th>직위</th><th>휴대폰</th><th>맡은 일</th><th>투입일</th><th /></tr>
            </thead>
            <tbody>
              {team.map((m) => (
                <tr key={m.id}>
                  <td data-label="이름"><b>{m.name}</b></td>
                  <td data-label="부서">{m.department ?? '-'}</td>
                  <td data-label="직위">{jobTitle(m.position, m.duty) || '-'}</td>
                  <td data-label="휴대폰"><Phone value={m.mobile} /></td>
                  <td data-label="맡은 일">{taskCell(m)}</td>
                  <td data-label="투입일">{date(m.assignedAt)}</td>
                  <td>
                    <button className="text-button" disabled={add.isPending}
                      onClick={() => confirm(`${m.name}님을 PM으로 지정할까요?${pm ? ` (${pm.name}님은 투입 인원으로 남습니다)` : ''}`) && add.mutate({ employeeId: m.employeeId, role: 'pm', task: '' })}>
                      PM으로
                    </button>
                    <button className="text-button danger" disabled={remove.isPending} onClick={() => confirm(`${m.name}님을 이 프로젝트에서 뺄까요?`) && remove.mutate(m.id)}>
                      빼기
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {addMany.data && addMany.data.skipped > 0 && (
        <p className="muted" style={{ marginTop: 10 }}>{addMany.data.added}명 추가 · {addMany.data.skipped}명은 이미 들어가 있거나 퇴직해 건너뛰었습니다.</p>
      )}
      {(update.error || remove.error || (!picker && add.error)) && <ErrorBox error={update.error ?? remove.error ?? add.error} />}

      {picker && (
        <EmployeePicker
          title={picker === 'pm' ? (pm ? 'PM 변경' : 'PM 지정') : '투입 인원 추가'}
          confirmLabel={picker === 'pm' ? 'PM으로 지정' : '추가'}
          multiple={picker === 'member'}
          exclude={exclude}
          busy={add.isPending || addMany.isPending}
          error={picker === 'pm' ? add.error : addMany.error}
          onPick={(picked, task) =>
            picker === 'pm'
              ? add.mutate({ employeeId: picked[0].id, role: 'pm', task })
              : addMany.mutate({ employeeIds: picked.map((e) => e.id), task })}
          onClose={() => setPicker(null)}
        />
      )}
    </div>
  )
}
