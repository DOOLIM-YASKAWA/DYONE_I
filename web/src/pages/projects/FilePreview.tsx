import { useQuery } from '@tanstack/react-query'
import { useEffect, useMemo } from 'react'
import { Link } from 'react-router-dom'
import { fetchBlob } from '../../api/client'
import type { ProjectFile } from '../../api/types'
import { ErrorBox, Loading } from '../../components/ui'

/**
 * 자료 미리 보기 — 형식마다 보여 주는 방식이 다르다.
 * - 브라우저가 바로 여는 것: PDF · 사진 · 영상 · 글자(TXT·JBI·INI·CSV 등)
 * - 서버 변환이 필요한 것(엑셀·워드·PPT·DWG·ZIP 목록): 변환 기능이 생기기 전까지 안내만
 */
const KIND: Record<string, 'pdf' | 'image' | 'video' | 'text' | 'csv' | 'office' | 'cad' | 'zip'> = {
  pdf: 'pdf',
  jpg: 'image', jpeg: 'image', png: 'image', gif: 'image', webp: 'image', bmp: 'image',
  mp4: 'video', webm: 'video', mov: 'video',
  txt: 'text', log: 'text', jbi: 'text', jbr: 'text', ini: 'text', json: 'text', xml: 'text', prm: 'text', dat: 'text',
  csv: 'csv',
  xlsx: 'office', xls: 'office', xlsm: 'office', docx: 'office', doc: 'office', pptx: 'office', ppt: 'office',
  dwg: 'cad', dxf: 'cad', step: 'cad', stp: 'cad',
  zip: 'zip',
}
const LABEL = { pdf: 'PDF', image: '사진', video: '영상', text: '글자', csv: '표(CSV)', office: '오피스 문서', cad: '도면', zip: '압축 파일' }
const MAX_TEXT = 200_000 // 글자 미리 보기는 앞부분만

const previewKind = (name: string) => KIND[(name.split('.').pop() ?? '').toLowerCase()]

export function FilePreview({ projectId, file }: { projectId: number; file: ProjectFile }) {
  const kind = previewKind(file.fileName)
  const direct = kind === 'pdf' || kind === 'image' || kind === 'video' || kind === 'text' || kind === 'csv'
  const blob = useQuery({
    queryKey: ['file-blob', projectId, file.id],
    queryFn: () => fetchBlob(`/api/projects/${projectId}/files/${file.id}/download`),
    enabled: direct,
    staleTime: 5 * 60_000,
  })
  const text = useQuery({
    queryKey: ['file-text', projectId, file.id],
    queryFn: async () => (await blob.data!.slice(0, MAX_TEXT).text()),
    enabled: !!blob.data && (kind === 'text' || kind === 'csv'),
  })
  // PDF는 서버가 보낸 형식이 octet-stream일 수 있어 형식을 붙여 다시 만든다
  const url = useMemo(() => {
    if (!blob.data || !(kind === 'pdf' || kind === 'image' || kind === 'video')) return null
    const typed = kind === 'pdf' ? new Blob([blob.data], { type: 'application/pdf' }) : blob.data
    return URL.createObjectURL(typed)
  }, [blob.data, kind])
  useEffect(() => () => { if (url) URL.revokeObjectURL(url) }, [url])

  const head = <div className="fpv-head">미리 보기 · {kind ? LABEL[kind] : '알 수 없는 형식'}</div>
  const srs = /SRS[_\s-]*BACKUP/i.test(file.fileName)

  if (!direct) {
    return (
      <div className="fpv">
        {head}
        <div className="fpv-note">
          {kind === 'office' && '엑셀·워드·파워포인트는 서버에서 PDF·표로 바꿔 보여 줄 예정입니다(변환 기능 준비 중). 지금은 다운로드해서 여세요.'}
          {kind === 'cad' && '도면(DWG·DXF)은 서버에서 PDF로 바꿔 보여 줄 예정입니다(변환 기능 준비 중). 지금은 다운로드해서 여세요.'}
          {kind === 'zip' && (srs
            ? <>현장 백업(SRS)입니다. 3D·차트는 <Link to="/srs-analyzer">SRS Analyzer</Link>에서 볼 수 있습니다. 압축 안 파일 목록 보기는 준비 중입니다.</>
            : '압축 안 파일 목록 보기는 준비 중입니다. 지금은 다운로드해서 여세요.')}
          {!kind && '이 형식은 미리 보기가 없습니다. 다운로드해서 여세요.'}
        </div>
      </div>
    )
  }
  if (blob.isLoading || text.isLoading) return <div className="fpv">{head}<Loading /></div>
  if (blob.error) return <div className="fpv">{head}<ErrorBox error={blob.error} /></div>

  return (
    <div className="fpv">
      {head}
      {kind === 'pdf' && url && <iframe className="fpv-pdf" src={url} title={file.fileName} />}
      {kind === 'image' && url && <img className="fpv-img" src={url} alt={file.fileName} />}
      {kind === 'video' && url && <video className="fpv-img" src={url} controls />}
      {kind === 'text' && <pre className="fpv-text">{text.data}</pre>}
      {kind === 'csv' && text.data != null && <CsvTable text={text.data} />}
      {(kind === 'text' || kind === 'csv') && (blob.data?.size ?? 0) > MAX_TEXT && <div className="fpv-note">앞부분만 보여 줍니다.</div>}
    </div>
  )
}

function CsvTable({ text }: { text: string }) {
  const rows = text.split(/\r?\n/).filter(Boolean).slice(0, 50).map((l) => l.split(','))
  return (
    <div className="data-wrap fpv-table">
      <table>
        <tbody>{rows.map((r, i) => <tr key={i}>{r.map((c, j) => (i === 0 ? <th key={j}>{c}</th> : <td key={j}>{c}</td>))}</tr>)}</tbody>
      </table>
    </div>
  )
}
