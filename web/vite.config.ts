import react from '@vitejs/plugin-react'
import { defineConfig, loadEnv } from 'vite'

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  return {
    plugins: [react()],
    base: './',
    server: {
      port: 5173,
      strictPort: true,
      proxy: {
        // Optional development API for the new process SSO while the existing API stays running.
        '/api/integrations/process': env.VITE_PROCESS_API_TARGET || 'http://localhost:5080',
        '/api': 'http://localhost:5080',
        '/hubs': { target: 'http://localhost:5080', ws: true },
      },
    },
  }
})
