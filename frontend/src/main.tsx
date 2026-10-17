import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter, Route, Routes } from 'react-router'
import AuthGate from './auth/AuthGate'
import Layout from './components/Layout'
import ImportPage from './library/ImportPage'
import LibraryPage from './library/LibraryPage'
import MovieDetail from './library/MovieDetail'
import SeriesDetail from './library/SeriesDetail'
import Home from './pages/Home'
import Indexers from './pages/Indexers'
import Placeholder from './pages/Placeholder'
import Profiles from './pages/Profiles'
import ParseTest from './pages/ParseTest'
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
            <Route path="library/movies" element={<LibraryPage key="movies" type="Movie" />} />
            <Route path="library/movies/:id" element={<MovieDetail />} />
            <Route path="library/series" element={<LibraryPage key="series" type="Series" />} />
            <Route path="library/series/:id" element={<SeriesDetail />} />
            <Route path="library/import" element={<ImportPage />} />
            <Route path="activity" element={<Placeholder title="Activity" phase="0.6" />} />
            <Route path="indexers" element={<Indexers />} />
            <Route path="settings" element={<Settings />} />
            <Route path="settings/profiles" element={<Profiles />} />
            <Route path="settings/release-test" element={<ParseTest />} />
            <Route path="*" element={<Placeholder title="Not found" />} />
          </Route>
          </Routes>
        </BrowserRouter>
      </AuthGate>
    </QueryClientProvider>
  </StrictMode>,
)
