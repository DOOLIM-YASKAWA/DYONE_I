import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState, type ReactNode } from 'react'
import { NavLink, useLocation, useNavigate } from 'react-router-dom'
import { api } from '../api/client'
import type { AppNotification } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { DyOneLogo } from './DyOneLogo'
import { platform } from '../platform/bridge'
import { visibleRoutes, type MenuGroup } from '../routes'
import { DemoMemos } from './DemoMemos' // [임시 · 데모] 화면 메모(포스트잇)
import { Icon, type IconName } from './icons'

const channelLabel = { web: 'PC 웹', desktop: 'Windows 앱', mobile: '모바일 앱' }[platform.kind]

type MenuItem = { path: string; icon: IconName; name: string; short?: string }
/** 펼침 메뉴 묶음(위쪽 메뉴 · 휴대폰 전체 메뉴 공통) */
const GROUP_NAME: Record<Exclude<MenuGroup, 'main'> | 'admin', string> = { eng: '엔지니어링', life: '생활', admin: '관리' }

/**
 * 화면 틀(D안, 2026-11-02): 위쪽 파란 메뉴 막대 + 화면 머리말 파란 띠(theme-d.css).
 * PC는 위쪽 메뉴(엔지니어링·생활·관리는 펼침), 휴대폰은 ☰ 전체 메뉴 + 아래 바로가기.
 */
export function Layout({ children }: { children: ReactNode }) {
  const { user, logout } = useAuth()
  const partner = user?.kind === 'partner'
  const routes = visibleRoutes(partner)
  const location = useLocation()
  // 메뉴를 연 화면 이동(location.key)을 기억해 두고, 이동하면 자연스럽게 닫힌 상태가 된다.
  // (경로로 기억하면 같은 화면에 다시 들어왔을 때 메뉴가 저절로 열려 있었다)
  const [menuOpenAt, setMenuOpenAt] = useState<string | null>(null)
  const menuOpen = menuOpenAt === location.key
  const setMenuOpen = (open: boolean) => setMenuOpenAt(open ? location.key : null)
  const wideLayout = (location.pathname.startsWith('/smartlab') || location.pathname.startsWith('/srs-analyzer') || location.pathname.startsWith('/process'))  // 임베드 화면은 본문 최대폭 해제

  useEffect(() => {
    window.scrollTo({ top: 0 })
  }, [location.pathname])

  const main: MenuItem[] = routes.filter((r) => r.group === 'main')
  const groups = ([
    ['eng', routes.filter((r) => r.group === 'eng')],
    ['life', routes.filter((r) => r.group === 'life')],
    ['admin', user?.isAdmin ? [{ path: '/admin/partners', icon: 'shield', name: '협력사 관리' }] : []],
  ] as [keyof typeof GROUP_NAME, MenuItem[]][]).filter(([, items]) => items.length > 0)
  const isIn = (items: MenuItem[]) => items.some((r) => location.pathname.startsWith(r.path))
  // 펼침 메뉴에서 고르면 포커스를 풀어 닫는다(펼침은 :hover/:focus-within으로 열린다)
  const closePop = () => (document.activeElement as HTMLElement | null)?.blur()

  return (
    <>
      <header className="dy-top">
        <div className="dy-top-in">
          <button className="dy-icon-btn dy-mobile" aria-label="전체 메뉴 열기" onClick={() => setMenuOpen(true)}>
            <Icon name="menu" size={20} />
          </button>
          <NavLink className="dy-brand" to="/projects">
            <DyOneLogo height={22} />
          </NavLink>
          <nav className="dy-nav" aria-label="주 메뉴">
            {main.map((r) => (
              <NavLink key={r.path} to={r.path} className={({ isActive }) => `dy-nav-item${isActive ? ' active' : ''}`}>{r.short ?? r.name}</NavLink>
            ))}
            {groups.map(([g, items]) => (
              <div key={g} className={`dy-group${isIn(items) ? ' active' : ''}`}>
                <button className="dy-nav-item" aria-haspopup="true">{GROUP_NAME[g]}<Icon name="down" size={14} /></button>
                <div className="dy-pop" role="menu">
                  {items.map((r) => (
                    <NavLink key={r.path} to={r.path} role="menuitem" onClick={closePop} className={({ isActive }) => (isActive ? 'active' : '')}>
                      <Icon name={r.icon} />{r.name}
                    </NavLink>
                  ))}
                </div>
              </div>
            ))}
          </nav>
          <div className="dy-top-right">
            <span className="demo-chip">개발 · dyone DB 읽기 전용</span>
            {!partner && <NotificationBell />}
            <button className="dy-avatar" title={`${user?.displayName ?? ''} · 로그아웃`} aria-label="로그아웃" onClick={logout}>
              {user?.name[0]}
            </button>
          </div>
        </div>
      </header>

      <div id="menuShade" hidden={!menuOpen} onClick={() => setMenuOpen(false)} />
      <aside className={`dy-drawer${menuOpen ? ' open' : ''}`} aria-label="전체 메뉴" aria-hidden={!menuOpen}>
        <div className="dy-drawer-head">
          <DyOneLogo height={20} />
          <button className="dy-icon-btn" aria-label="전체 메뉴 닫기" onClick={() => setMenuOpen(false)}><Icon name="close" size={20} /></button>
        </div>
        <nav aria-label="전체 메뉴 목록">
          <small>업무</small>
          {main.map((r) => (
            <NavLink key={r.path} to={r.path} className={({ isActive }) => (isActive ? 'active' : '')}><Icon name={r.icon} />{r.name}</NavLink>
          ))}
          {groups.map(([g, items]) => (
            <div key={g}>
              <small>{GROUP_NAME[g]}</small>
              {items.map((r) => (
                <NavLink key={r.path} to={r.path} className={({ isActive }) => (isActive ? 'active' : '')}><Icon name={r.icon} />{r.name}</NavLink>
              ))}
            </div>
          ))}
        </nav>
        <div className="dy-drawer-foot">
          <span className="dy-avatar">{user?.name[0]}</span>
          <div>
            <b>{user?.displayName}</b>
            <small>{user?.department} · {channelLabel}</small>
          </div>
          <button className="text-button" onClick={logout}>로그아웃</button>
        </div>
      </aside>

      <div className="dy-workspace">
        <main id="main" className={wideLayout ? 'flush' : undefined}>{children}</main>
        <footer className="demo-footer">
          개발 환경 · dyone DB(그룹웨어 프로젝트·조직도·공정·SmartLab 미러)를 읽기 전용으로 표시합니다. 그룹웨어 인증·ERP·AI·주차·출입은 아직 연결되지 않았습니다.
        </footer>
      </div>

      {/* [임시 · 데모 기간] 화면 메모(포스트잇) — 데모가 끝나면 이 줄과 DemoMemos 파일을 지운다 */}
      {!partner && <DemoMemos />}

      <nav id="bottomNav" aria-label="모바일 바로가기">
        {main.slice(0, 4).map((r) => (
          <NavLink key={r.path} to={r.path} className={({ isActive }) => (isActive ? 'active' : '')}>
            <Icon name={r.icon} size={20} />
            {r.short ?? r.name}
          </NavLink>
        ))}
        <button onClick={() => setMenuOpen(true)}>
          <Icon name="menu" size={20} />전체
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
      <button className="dy-icon-btn bell-button" aria-label={`알림 ${unread}건`} onClick={() => setOpen((v) => !v)}>
        <Icon name="bell" />{unread > 0 && <span className="bell-count">{unread > 99 ? '99+' : unread}</span>}
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
