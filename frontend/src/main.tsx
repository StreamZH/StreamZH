import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter, Navigate, Route, Routes } from 'react-router'
import ActivityPage from './activity/ActivityPage'
import AuthGate from './auth/AuthGate'
import Layout from './components/Layout'
import ImportPage from './library/ImportPage'
import LibraryPage from './library/LibraryPage'
import MovieDetail from './library/MovieDetail'
import SeriesDetail from './library/SeriesDetail'
import Home from './pages/Home'
import Indexers from './pages/Indexers'
import IndexerHistoryPage from './indexerTools/IndexerHistoryPage'
import IndexerStatsPage from './indexerTools/IndexerStatsPage'
import SearchPage from './indexerTools/SearchPage'
import Placeholder from './pages/Placeholder'
import Profiles from './pages/Profiles'
import ParseTest from './pages/ParseTest'
import UsenetServers from './pages/UsenetServers'
import DownloadsPage from './settings/DownloadsPage'
import GeneralPage from './settings/GeneralPage'
import MediaManagementPage from './settings/MediaManagementPage'
import MetadataPage from './settings/MetadataPage'
import ConnectPage from './settings/ConnectPage'
import IndexerOptionsPage from './settings/IndexerOptionsPage'
import UiPage from './settings/UiPage'
import QualityPage from './settings/QualityPage'
import SettingsOverview from './settings/SettingsOverview'
import LogsPage from './system/LogsPage'
import CalendarPage from './calendar/CalendarPage'
import WantedPage from './wanted/WantedPage'
import FileRenamerPage from './tools/FileRenamerPage'
import ImportListsPage from './settings/ImportListsPage'
import BackupPage from './system/BackupPage'
import StatusPage from './system/StatusPage'
import TasksPage from './system/TasksPage'
import UiSettingsSync from './lib/UiSettingsSync'
import { RestartOverlay } from './system/Restart'
import { urlBase } from './lib/urlBase'
import './index.css'

const queryClient = new QueryClient({
  defaultOptions: { queries: { staleTime: 10_000, retry: 1 } },
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <RestartOverlay />
      <AuthGate>
        <UiSettingsSync />
        <BrowserRouter basename={urlBase || undefined}>
          <Routes>
          <Route element={<Layout />}>
            <Route index element={<Home />} />
            <Route path="library/movies" element={<LibraryPage key="movies" type="Movie" />} />
            <Route path="library/movies/:id" element={<MovieDetail />} />
            <Route path="library/series" element={<LibraryPage key="series" type="Series" />} />
            <Route path="library/series/:id" element={<SeriesDetail />} />
            <Route path="library/import" element={<ImportPage />} />
            <Route path="calendar" element={<CalendarPage />} />
            <Route path="wanted" element={<Navigate to="/wanted/missing" replace />} />
            <Route path="wanted/missing" element={<WantedPage key="missing" />} />
            <Route path="wanted/cutoff" element={<WantedPage key="cutoff" />} />
            <Route path="tools" element={<Navigate to="/tools/renamer" replace />} />
            <Route path="tools/renamer" element={<FileRenamerPage />} />
            <Route path="activity" element={<ActivityPage />} />
            <Route path="indexers" element={<Indexers />} />
            <Route path="indexers/search" element={<SearchPage />} />
            <Route path="indexers/stats" element={<IndexerStatsPage />} />
            <Route path="indexers/history" element={<IndexerHistoryPage />} />
            <Route path="settings" element={<SettingsOverview />} />
            <Route path="settings/media-management" element={<MediaManagementPage />} />
            <Route path="settings/import-lists" element={<ImportListsPage />} />
            <Route path="settings/profiles" element={<Profiles />} />
            <Route path="settings/quality" element={<QualityPage />} />
            <Route path="settings/indexers" element={<IndexerOptionsPage />} />
            <Route path="settings/downloads" element={<DownloadsPage />} />
            <Route path="settings/usenet" element={<UsenetServers />} />
            <Route path="settings/connect" element={<ConnectPage />} />
            <Route path="settings/metadata" element={<MetadataPage />} />
            <Route path="settings/general" element={<GeneralPage />} />
            <Route path="settings/ui" element={<UiPage />} />
            <Route path="settings/release-test" element={<ParseTest />} />
            <Route path="system" element={<Navigate to="/system/status" replace />} />
            <Route path="system/status" element={<StatusPage />} />
            <Route path="system/tasks" element={<TasksPage />} />
            <Route path="system/backup" element={<BackupPage />} />
            <Route path="system/logs" element={<LogsPage />} />
            <Route path="*" element={<Placeholder title="Not found" />} />
          </Route>
          </Routes>
        </BrowserRouter>
      </AuthGate>
    </QueryClientProvider>
  </StrictMode>,
)
