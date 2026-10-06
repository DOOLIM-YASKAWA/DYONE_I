import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// 개발 중에는 /api, /hubs 요청을 로컬 API 서버(5080)로 넘긴다.
// 운영에서는 같은 도메인의 리버스 프록시가 처리하므로 프록시 설정이 필요 없다.
export default defineConfig({
  plugins: [react()],
  // Windows 앱(WebView2)·모바일(Capacitor)이 빌드 결과를 파일로 열 수 있게 상대 경로로 빌드
  base: './',
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': 'http://localhost:5080',
      '/hubs': { target: 'http://localhost:5080', ws: true },
    },
  },
})
