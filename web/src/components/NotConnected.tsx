/** dyone DB에 아직 테이블이 없는 기능의 안내. DY ONE 전용 스키마가 확정되면 연결한다. */
export function NotConnected({ what, source }: { what: string; source?: string }) {
  return (
    <div className="empty not-connected">
      <strong>{what} 데이터는 아직 연결되지 않았습니다.</strong>
      <br />
      {source ? `${source} 데이터가 dyone DB에 아직 없습니다. ` : 'dyone DB에 해당 테이블이 아직 없습니다. '}
      DY ONE 전용 스키마가 확정되면 연결합니다.
    </div>
  )
}
