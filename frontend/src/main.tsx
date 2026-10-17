import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter, Route, Routes } from 'react-router'
import AuthGate from './auth/AuthGate'
import Layout from './components/Layout'
import Home from './pages/Home'
import Placeholder from './pages/Placeholder'
import Settings from './pages/Settings'
import './index.css'

const queryClient = new QueryClient({
  defaultOptions: { queries: { staleTime: 10_000, retry: 1 } },
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <AuthGate>
        <BrowserRouter>
          <Routes>
          <Route element={<Layout />}>
            <Route index element={<Home />} />
            <Route path="library/movies" element={<Placeholder title="Movies" phase="0.2" />} />
            <Route path="library/series" element={<Placeholder title="Series" phase="0.2" />} />
            <Route path="activity" element={<Placeholder title="Activity" phase="0.6" />} />
            <Route path="indexers" element={<Placeholder title="Indexers" phase="0.4" />} />
            <Route path="settings" element={<Settings />} />
            <Route path="*" element={<Placeholder title="Not found" />} />
          </Route>
          </Routes>
        </BrowserRouter>
      </AuthGate>
    </QueryClientProvider>
  </StrictMode>,
)
