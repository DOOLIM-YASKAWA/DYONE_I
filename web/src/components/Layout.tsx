import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState, type ReactNode } from 'react'
import { NavLink, useLocation, useNavigate } from 'react-router-dom'
import { api } from '../api/client'
import type { AppNotification } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { platform } from '../platform/bridge'
import { visibleRoutes } from '../routes'
import { DemoMemos } from './DemoMemos' // [임시 · 데모] 화면 메모(포스트잇)

const channelLabel = { web: 'PC 웹', desktop: 'Windows 앱', mobile: '모바일 앱' }[platform.kind]

export function Layout({ children }: { children: ReactNode }) {
  const { user, logout } = useAuth()
  const partner = user?.kind === 'partner'
  const routes = visibleRoutes(partner)
  const location = useLocation()
  // 메뉴를 연 화면 경로를 기억해 두고, 다른 화면으로 이동하면 자연스럽게 닫힌 상태가 된다.
  const [menuOpenAt, setMenuOpenAt] = useState<string | null>(null)
  const menuOpen = menuOpenAt === location.pathname
  const setMenuOpen = (open: boolean) => setMenuOpenAt(open ? location.pathname : null)
  const current = routes.find((r) => location.pathname.startsWith(r.path)) ?? routes[0]
  const wideLayout = (location.pathname.startsWith('/smartlab') || location.pathname.startsWith('/srs-analyzer') || location.pathname.startsWith('/process'))  // 임베드 화면은 본문 최대폭 해제

  useEffect(() => {
    window.scrollTo({ top: 0 })
  }, [location.pathname])

  return (
    <>
      <aside id="sidebar" className={menuOpen ? 'open' : ''}>
        <NavLink className="brand" to={partner ? '/projects' : '/home'}>
          <span className="brand-mark">D</span>
          <span>
            DY <b>ONE</b>
            <small>INTERNAL WORKSPACE</small>
          </span>
        </NavLink>
        <p className="nav-label">WORK &amp; LIFE</p>
        <nav id="navigation" aria-label="주 메뉴">
          {routes.map((r) => (
            <NavLink key={r.path} to={r.path} className={({ isActive }) => `nav-item${isActive ? ' active' : ''}`}>
              <span className="nav-icon">{r.icon}</span>
              {r.name}
            </NavLink>
          ))}
          {user?.isAdmin && (
            <NavLink to="/admin/partners" className={({ isActive }) => `nav-item${isActive ? ' active' : ''}`}>
              <span className="nav-icon">⚿</span>
              협력사 관리
            </NavLink>
          )}
        </nav>
        <div className="sidebar-foot">
          <span className="avatar">{user?.name[0]}</span>
          <div>
            <b>{user?.displayName}</b>
            <small>
              {user?.department} · {channelLabel}
            </small>
          </div>
        </div>
      </aside>
      <div id="menuShade" hidden={!menuOpen} onClick={() => setMenuOpen(false)} />

      <div className="workspace">
        <header className="topbar">
          <div>
            <button className="icon-btn mobile-only" aria-label="전체 메뉴 열기" onClick={() => setMenuOpen(true)}>
              ☰
            </button>
            <span id="breadcrumb">워크스페이스 / {location.pathname.startsWith('/admin/partners') ? '협력사 관리' : current.name}</span>
          </div>
          <div>
            <span className="demo-chip">개발 · dyone DB 읽기 전용</span>
            {!partner && <NotificationBell />}
            <button className="avatar mini avatar-button" title="로그아웃" onClick={logout}>
              {user?.name[0]}
            </button>
          </div>
        </header>
        <main id="main" className={wideLayout ? 'flush' : undefined}>{children}</main>
        <footer className="demo-footer">
          개발 환경 · dyone DB(그룹웨어 프로젝트·조직도·공정·SmartLab 미러)를 읽기 전용으로 표시합니다. 그룹웨어 인증·ERP·AI·주차·출입은 아직 연결되지 않았습니다.
        </footer>
      </div>

      {/* [임시 · 데모 기간] 화면 메모(포스트잇) — 데모가 끝나면 이 줄과 DemoMemos 파일을 지운다 */}
      {!partner && <DemoMemos />}

      <nav id="bottomNav" aria-label="모바일 바로가기">
        {routes.slice(0, 4).map((r) => (
          <NavLink key={r.path} to={r.path} className={({ isActive }) => (isActive ? 'active' : '')}>
            <span>{r.icon}</span>
            {r.name}
          </NavLink>
        ))}
        <button onClick={() => setMenuOpen(true)}>
          <span>☰</span>전체
        </button>
      </nav>
    </>
  )
}

function NotificationBell() {
  const [open, setOpen] = useState(false)
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  // 알림함: 접속 전에 온 알림(내가 쓰는 앱 것, 최근 30일)을 서버에서 받고, 이후는 실시간으로 받은 알림을 앞에 쌓는다.
  const { data = [] } = useQuery({
    queryKey: ['notifications'],
    queryFn: () => api<AppNotification[]>('/api/notifications'),
    staleTime: Infinity,
  })
  const unread = data.filter((n) => !n.readAt).length
  const markRead = (id?: number) =>
    queryClient.setQueryData<AppNotification[]>(['notifications'], (old = []) =>
      old.map((n) => (id === undefined || n.id === id ? { ...n, readAt: n.readAt ?? new Date().toISOString() } : n)))
  // 읽음은 화면에 먼저 표시하고 서버에 남긴다(다른 PC·Windows 앱에서도 읽음으로 보인다).
  const read = { mutate: (id: number) => { markRead(id); void api(`/api/notifications/${id}/read`, { method: 'POST' }).catch(() => {}) } }
  const readAll = { mutate: () => { markRead(); void api('/api/notifications/read-all', { method: 'POST' }).catch(() => {}) } }
  const test = useMutation({ mutationFn: () => api('/api/notifications/test', { method: 'POST' }) })

  return (
    <div className="bell">
      <button className="secondary bell-button" aria-label={`알림 ${unread}건`} onClick={() => setOpen((v) => !v)}>
        🔔{unread > 0 && <span className="bell-count">{unread}</span>}
      </button>
      {open && (
        <div className="bell-panel panel" role="dialog" aria-label="알림함">
          <div className="bell-head">
            <b>알림함</b>
            <span>
              <button className="text-button" onClick={() => test.mutate()}>
                테스트 알림
              </button>
              <button className="text-button" onClick={() => readAll.mutate()} disabled={unread === 0}>
                모두 읽음
              </button>
            </span>
          </div>
          {data.length === 0 && <p className="bell-empty">알림이 없습니다.</p>}
          {data.map((n) => (
            <button
              key={n.id}
              className={`bell-item${n.readAt ? '' : ' unread'}`}
              onClick={() => {
                if (!n.readAt) read.mutate(n.id)
                // 그룹웨어 알림(그대로 받기)은 그룹웨어 문서 주소 → 새 창(Windows 앱은 기본 브라우저)으로 연다
                if (n.link && /^https?:\/\//.test(n.link)) void platform.openExternal(n.link)
                else if (n.link) navigate(n.link)
                setOpen(false)
              }}
            >
              <strong>{n.title}</strong>
              <small>{n.body}</small>
              <time>{new Date(n.createdAt).toLocaleString('ko-KR')}</time>
            </button>
          ))}
        </div>
      )}
    </div>
  )
}
