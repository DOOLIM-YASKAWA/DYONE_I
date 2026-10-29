import { useQuery } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { api } from '../../api/client'
import type { OrgData, OrgDepartment, OrgEmployee } from '../../api/orgTypes'
import '../../styles/org.css'

/** 조직도 데이터(부서·재직자 전체). 조직도 화면과 인원 선택 상자가 같이 쓴다. */
export const useOrg = () => useQuery({ queryKey: ['org'], queryFn: () => api<OrgData>('/api/org'), staleTime: 5 * 60_000 })

export interface OrgIndex {
  depts: Map<number, OrgDepartment>
  children: Map<number | null, OrgDepartment[]>
  /** 부서별 소속 직원(그 부서에 바로 속한 사람만) */
  members: Map<number | null, OrgEmployee[]>
  /** 하위 부서까지 합친 인원 수 */
  total: Map<number, number>
  /** '기술부 > 공정기술실 > PM팀' */
  path(deptId?: number | null): string
}

/** 부서 트리·인원 색인. 최상위 회사 아래에 사람이 하나도 없는 부서(빈 법인 등)는 숨긴다. */
export function useOrgIndex(data?: OrgData): OrgIndex | null {
  return useMemo(() => {
    if (!data) return null
    const depts = new Map(data.departments.map((d) => [d.id, d]))
    const children = new Map<number | null, OrgDepartment[]>()
    for (const d of data.departments) {
      const parent = d.parentId != null && depts.has(d.parentId) ? d.parentId : null
      children.set(parent, [...(children.get(parent) ?? []), d])
    }
    const members = new Map<number | null, OrgEmployee[]>()
    for (const e of data.employees) {
      const key = e.departmentId != null && depts.has(e.departmentId) ? e.departmentId : null
      members.set(key, [...(members.get(key) ?? []), e])
    }
    const total = new Map<number, number>()
    const count = (id: number): number => {
      const n = (members.get(id)?.length ?? 0) + (children.get(id) ?? []).reduce((s, c) => s + count(c.id), 0)
      total.set(id, n)
      return n
    }
    for (const root of children.get(null) ?? []) count(root.id)
    const path = (deptId?: number | null) => {
      const names: string[] = []
      let cur = deptId != null ? depts.get(deptId) : undefined
      while (cur) {
        if (cur.parentId != null) names.unshift(cur.name) // 회사 이름(최상위)은 빼고 보여 준다
        cur = cur.parentId != null ? depts.get(cur.parentId) : undefined
      }
      return names.join(' > ')
    }
    return { depts, children, members, total, path }
  }, [data])
}

/** 부서 id와 그 하위 부서 id 전부 */
export function deptAndBelow(idx: OrgIndex, id: number): Set<number> {
  const out = new Set<number>([id])
  const walk = (x: number) => (idx.children.get(x) ?? []).forEach((c) => { out.add(c.id); walk(c.id) })
  walk(id)
  return out
}

/** '책임매니저 · 팀장' — 직위와 직책이 같으면(실장 · 실장) 한 번만 */
export const jobTitle = (position?: string, duty?: string) => [...new Set([position, duty].filter(Boolean))].join(' · ')

/** 휴대폰 번호 표시: 하이픈 없이 적힌 01012345678은 010-1234-5678로. 나머지(해외·내선)는 그대로 */
export const phoneText = (v?: string) => (v && /^01\d{8,9}$/.test(v) ? v.replace(/^(\d{3})(\d{3,4})(\d{4})$/, '$1-$2-$3') : v ?? '')

/** 누르면 전화 걸기(모바일·Windows 전화 연결 앱). 번호가 없으면 '-' */
export function Phone({ value, label }: { value?: string; label?: string }) {
  if (!value) return <>{label ? null : '-'}</>
  return (
    <a className="phone" href={`tel:${value.replace(/[^\d+]/g, '')}`} onClick={(e) => e.stopPropagation()}>
      {label && <span className="phone-label">{label}</span>}{phoneText(value)}
    </a>
  )
}

/** 직원 정렬: 직책(대표·본부장·실장·팀장 순) → 직위 → 이름 */
export const byRank = (a: OrgEmployee, b: OrgEmployee) =>
  (a.dutyOrder ?? 999) - (b.dutyOrder ?? 999) || a.positionRank - b.positionRank || a.name.localeCompare(b.name, 'ko')

/** 부서 트리(접기·펼치기). 인원이 없는 부서는 숨긴다. */
export function DeptTree({ idx, selected, onSelect }: { idx: OrgIndex; selected: number | null; onSelect(id: number): void }) {
  const roots = (idx.children.get(null) ?? []).filter((d) => (idx.total.get(d.id) ?? 0) > 0)
  const [open, setOpen] = useState<Set<number>>(() => new Set(roots.map((r) => r.id)))
  const toggle = (id: number) => setOpen((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n })
  const node = (d: OrgDepartment, depth: number) => {
    const kids = (idx.children.get(d.id) ?? []).filter((c) => (idx.total.get(c.id) ?? 0) > 0)
    const isOpen = open.has(d.id)
    return (
      <li key={d.id}>
        <div className={`org-node${selected === d.id ? ' on' : ''}`} style={{ paddingLeft: 6 + depth * 16 }}>
          {kids.length > 0 ? (
            <button className="org-toggle" onClick={() => toggle(d.id)} aria-label={isOpen ? `${d.name} 접기` : `${d.name} 펼치기`} aria-expanded={isOpen}>
              {isOpen ? '▾' : '▸'}
            </button>
          ) : <span className="org-toggle" />}
          <button className="org-name" onClick={() => onSelect(d.id)} aria-pressed={selected === d.id}>
            {d.name} <small>{idx.total.get(d.id) ?? 0}</small>
          </button>
        </div>
        {isOpen && kids.length > 0 && <ul>{kids.map((k) => node(k, depth + 1))}</ul>}
      </li>
    )
  }
  return <ul className="org-tree">{roots.map((r) => node(r, 0))}</ul>
}
