import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../../api/client'

interface Lookup {
  id: number
  code: string
  name: string
}

/** 프로젝트가 3천 건이 넘어 드롭다운 대신 검색해서 고른다. */
export function ProjectPicker({ onPick }: { onPick(id: number): void }) {
  const [text, setText] = useState('')
  const term = text.trim()
  const { data = [] } = useQuery({
    queryKey: ['project-lookup', term],
    queryFn: () => api<Lookup[]>(`/api/projects/lookup?search=${encodeURIComponent(term)}`),
    enabled: term.length >= 2,
  })

  return (
    <label className="project-picker">
      프로젝트
      <input
        list="project-lookup"
        value={text}
        placeholder="프로젝트 코드·이름 검색 (2자 이상)"
        onChange={(e) => {
          const v = e.target.value
          setText(v)
          const hit = data.find((p) => `${p.code} · ${p.name}` === v)
          if (hit) {
            onPick(hit.id)
            setText('')
          }
        }}
      />
      <datalist id="project-lookup">
        {data.map((p) => (
          <option key={p.id} value={`${p.code} · ${p.name}`} />
        ))}
      </datalist>
    </label>
  )
}
