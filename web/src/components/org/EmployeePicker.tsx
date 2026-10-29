import { useState } from 'react'
import type { OrgEmployee } from '../../api/orgTypes'
import { ErrorBox, Loading, Modal } from '../ui'
import { byRank, deptAndBelow, DeptTree, jobTitle, phoneText, useOrg, useOrgIndex } from './OrgTree'

/**
 * 조직도에서 직원 고르기(부서 트리 + 검색) + 맡은 일 입력.
 * multiple: 여러 명 체크해서 한 번에 고르기(투입 인원). 아니면 한 명(PM).
 * exclude: 이미 들어가 있어 고를 수 없는 직원 id(이유 문구와 함께 회색으로 보인다).
 */
export function EmployeePicker({ title, confirmLabel, multiple, exclude, busy, error, onPick, onClose }: {
  title: string
  confirmLabel: string
  multiple?: boolean
  exclude?: Map<number, string>
  busy?: boolean
  error?: unknown
  onPick(picked: OrgEmployee[], task: string): void
  onClose(): void
}) {
  const org = useOrg()
  const idx = useOrgIndex(org.data)
  const [dept, setDept] = useState<number | null>(null)
  const [search, setSearch] = useState('')
  const [picked, setPicked] = useState<OrgEmployee[]>([])
  const [task, setTask] = useState('')
  const isPicked = (e: OrgEmployee) => picked.some((p) => p.id === e.id)
  const toggle = (e: OrgEmployee) =>
    setPicked((cur) => (multiple ? (cur.some((p) => p.id === e.id) ? cur.filter((p) => p.id !== e.id) : [...cur, e]) : [e]))

  let body
  if (org.isLoading) body = <Loading />
  else if (org.error || !org.data || !idx) body = <ErrorBox error={org.error} />
  else {
    const q = search.trim().toLowerCase()
    const scope = dept == null ? null : deptAndBelow(idx, dept)
    const people = org.data.employees
      .filter((e) => (q ? [e.name, e.position, e.email, idx.path(e.departmentId)].some((v) => v?.toLowerCase().includes(q)) : scope ? scope.has(e.departmentId ?? -1) : false))
      .sort(byRank)
    const selectable = people.filter((e) => !exclude?.has(e.id))
    const allOn = selectable.length > 0 && selectable.every(isPicked)
    body = (
      <div className="picker">
        <div className="picker-tree">
          <DeptTree idx={idx} selected={q ? null : dept} onSelect={(id) => { setDept(id); setSearch('') }} />
        </div>
        <div className="picker-list">
          <input autoFocus value={search} onChange={(e) => setSearch(e.target.value)} placeholder="이름·부서 검색" aria-label="직원 검색" style={{ width: '100%' }} />
          {!q && dept == null && <p className="picker-hint">왼쪽에서 부서를 고르거나 이름으로 검색하세요.{multiple ? ' 여러 부서에서 이어서 고를 수 있습니다.' : ''}</p>}
          {multiple && selectable.length > 1 && (
            <label className="picker-all">
              <input type="checkbox" checked={allOn}
                onChange={() => setPicked((cur) => (allOn
                  ? cur.filter((p) => !selectable.some((s) => s.id === p.id))
                  : [...cur, ...selectable.filter((s) => !cur.some((p) => p.id === s.id))]))} />
              이 목록 {selectable.length}명 모두
            </label>
          )}
          <ul>
            {people.map((e) => {
              const why = exclude?.get(e.id)
              const on = isPicked(e)
              return (
                <li key={e.id}>
                  <button className={on ? 'on' : ''} disabled={!!why} onClick={() => toggle(e)} aria-pressed={on}>
                    {multiple && <input type="checkbox" checked={on || !!why} disabled={!!why} readOnly tabIndex={-1} aria-hidden />}
                    <span>
                      <b>{e.name}</b> <small>{jobTitle(e.position, e.duty)}</small>
                      <small className="picker-dept">{why ?? idx.path(e.departmentId)}{!why && e.mobile ? ` · ${phoneText(e.mobile)}` : ''}</small>
                    </span>
                  </button>
                </li>
              )
            })}
          </ul>
        </div>
      </div>
    )
  }

  const label = picked.length === 0 ? confirmLabel : multiple ? `${picked.length}명 ${confirmLabel}` : `${picked[0].name} ${confirmLabel}`
  return (
    <Modal
      title={title}
      onClose={onClose}
      footer={<button className="primary" disabled={picked.length === 0 || busy} onClick={() => onPick(picked, task.trim())}>{label}</button>}
    >
      {body}
      {multiple && picked.length > 0 && (
        <div className="picker-chips" aria-label="고른 인원">
          {picked.map((p) => (
            <button key={p.id} className="chip active" onClick={() => toggle(p)} title="눌러서 빼기">{p.name} ×</button>
          ))}
        </div>
      )}
      <label className="picker-task">
        맡은 일(선택{multiple ? ', 고른 사람 모두에게' : ''})
        <input value={task} onChange={(e) => setTask(e.target.value)} maxLength={100} placeholder="예: 설계, 모듈라, 시운전, 현장 설치" />
      </label>
      {error ? <ErrorBox error={error} /> : null}
    </Modal>
  )
}
