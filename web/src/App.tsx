import type { ReactElement } from 'react'
import { Navigate, Route, Routes } from 'react-router-dom'
import { useAuth } from './auth/AuthContext'
import { Layout } from './components/Layout'
import { ChatPage } from './pages/ChatPage'
import { HomePage } from './pages/HomePage'
import { LoginPage } from './pages/LoginPage'
import { ProjectsPage } from './pages/projects/ProjectsPage'
import { AccessPage, ItPage, MealsPage, VehiclesPage } from './pages/ServicePages'
import { useRealtimeNotifications } from './realtime/useRealtimeNotifications'
import { enabledRoutes } from './routes'

// 전체 화면 매핑. 이번 단계는 routes.ts의 enabled=true(=홈)만 라우팅하고, 나머지는 /home으로 보낸다.
// 숨긴 화면 코드는 그대로 두고 routes.ts에서 enabled를 켜면 다시 노출된다.
const pageElements: Record<string, ReactElement> = {
  '/home': <HomePage />,
  '/projects': <ProjectsPage />,
  '/chat': <ChatPage />,
  '/it': <ItPage />,
  '/meals': <MealsPage />,
  '/vehicles': <VehiclesPage />,
  '/access': <AccessPage />,
}

export function App() {
  const { user, loading } = useAuth()
  useRealtimeNotifications(user !== null)

  if (loading) return <div className="login-wrap">불러오는 중…</div>
  if (!user) return <LoginPage />

  return (
    <Layout>
      <Routes>
        {enabledRoutes.map((r) => (
          <Route key={r.path} path={r.path} element={pageElements[r.path]} />
        ))}
        <Route path="*" element={<Navigate to="/home" replace />} />
      </Routes>
    </Layout>
  )
}
