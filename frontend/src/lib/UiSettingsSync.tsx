import { useQuery } from '@tanstack/react-query'
import { useEffect } from 'react'
import { generalApi, generalKeys } from '../api/general'
import { setUiFormats } from './format'

/** Loads Settings → UI once after sign-in and hands the date and time formats to lib/format. */
export default function UiSettingsSync() {
  const ui = useQuery({ queryKey: generalKeys.ui, queryFn: generalApi.ui, staleTime: 5 * 60_000 })

  useEffect(() => {
    if (ui.data) setUiFormats(ui.data)
  }, [ui.data])

  return null
}
