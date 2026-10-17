import { useQuery } from '@tanstack/react-query'
import { downloadSettingsApi, downloadSettingsKeys } from '../api/downloadSettings'
import { ToolsSection } from '../pages/ToolsSettings'
import { DownloadCategoriesSection } from './DownloadCategoriesSection'
import { DownloadScheduleSection } from './DownloadScheduleSection'
import {
  DownloadClientSection,
  FoldersSection,
  HistorySection,
  PostProcessingSection,
  QueueSection,
  SpecialSection,
} from './DownloadsSection'
import { SettingsPage } from './SettingsKit'

/**
 * Settings → Downloads, laid out like SABnzbd's config pages: client, folders, categories, queue and post-processing
 * switches, scheduling, history, special, tools. With the blackhole folder only the client card is shown.
 */
export default function DownloadsPage() {
  const info = useQuery({ queryKey: downloadSettingsKeys.settings, queryFn: downloadSettingsApi.get })
  const builtIn = info.data?.client !== 'blackhole'
  return (
    <SettingsPage title="Downloads" lead="How grabbed releases are downloaded, repaired and unpacked.">
      {info.data && <DownloadClientSection info={info.data} />}
      {info.data && builtIn && (
        <>
          <FoldersSection info={info.data} />
          <DownloadCategoriesSection />
          <QueueSection />
          <PostProcessingSection />
          <DownloadScheduleSection />
          <HistorySection />
          <SpecialSection />
          <ToolsSection />
        </>
      )}
    </SettingsPage>
  )
}
