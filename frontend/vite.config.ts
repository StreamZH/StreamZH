import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// npm run dev proxies to a local StreamZH; STREAMZH_BACKEND points it at another one (e.g. a test server in the LAN).
const backend = process.env.STREAMZH_BACKEND || 'http://localhost:9797'

// The production build goes straight into the ASP.NET Core host (served from wwwroot).
// Relative asset paths in the build: the server adds <base href="{URL base}/"> to index.html, so the same build works
// at the root and below a URL base (Settings → General → Host).
export default defineConfig(({ command }) => ({
  base: command === 'build' ? './' : '/',
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
}))
