import type { ReactElement } from 'react'
import { Navigate, Route, Routes } from 'react-router-dom'
import { useAuth } from './auth/AuthContext'
import { Layout } from './components/Layout'
import { ChatPage } from './pages/ChatPage'
import { ModularPage, WorksAppPage } from './pages/GroupwareAppPages'
import { HomePage } from './pages/HomePage'
import { LoginPage } from './pages/LoginPage'
import { OrgChartPage } from './pages/OrgChartPage'
import { ProjectDetailPage } from './pages/projects/ProjectDetailPage'
import { ProjectsPage } from './pages/projects/ProjectsPage'
import { SrsAnalyzerPage } from './pages/SrsAnalyzerPage'
import { SmartLabPage } from './pages/SmartLabPage'
import { ProcessPage } from './pages/ProcessPage'
import { MotosimPage } from './pages/motosim/MotosimPage'
import { AccessPage, ItPage, VehiclesPage } from './pages/ServicePages'
import { MealsPage } from './pages/MealsPage'
import { useRealtimeNotifications } from './realtime/useRealtimeNotifications'
import { enabledRoutes } from './routes'

// 전체 화면 매핑. 이번 단계는 routes.ts의 enabled=true(=홈)만 라우팅하고, 나머지는 /home으로 보낸다.
// 숨긴 화면 코드는 그대로 두고 routes.ts에서 enabled를 켜면 다시 노출된다.
const pageElements: Record<string, ReactElement> = {
  '/home': <HomePage />,
  '/projects': <ProjectsPage />,
  '/modular': <ModularPage />,
  '/org': <OrgChartPage />,
  '/works/281': <WorksAppPage appletId={281} title="ES 이슈" />,
  '/smartlab': <SmartLabPage />,
  '/process': <ProcessPage />,
  '/motosim': <MotosimPage />,
  '/srs-analyzer': <SrsAnalyzerPage />,
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
        <Route path="/projects/:id" element={<ProjectDetailPage />} />
        <Route path="*" element={<Navigate to="/home" replace />} />
      </Routes>
    </Layout>
  )
}
