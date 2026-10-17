import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

const backend = 'http://localhost:9797'

// The production build goes straight into the ASP.NET Core host (served from wwwroot).
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../src/StreamZH.Host/wwwroot',
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': backend,
      '/health': backend,
      '/hub': { target: backend, ws: true },
    },
  },
})
