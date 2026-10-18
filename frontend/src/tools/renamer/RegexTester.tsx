import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { renamerApi, renamerKeys } from '../../api/renamer'
import ui from '../../components/ui.module.css'
import styles from './Renamer.module.css'
import { useDebounced } from './useDebounced'

/** Runs the pattern on the first file (on the server, so it is .NET regex like the rename): matches, groups and the result. */
export default function RegexTester({ pattern, caseSensitive, replacement, sample }: { pattern: string; caseSensitive: boolean; replacement: string; sample: string | null }) {
  const input = sample ?? ''
  // Debounce the text form: a new object every render would never settle.
  const args = JSON.parse(useDebounced(JSON.stringify({ pattern, caseSensitive, replacement, input }), 300)) as {
    pattern: string
    caseSensitive: boolean
    replacement: string
    input: string
  }
  const test = useQuery({
    queryKey: renamerKeys.regex(args.pattern, args.caseSensitive, args.input, args.replacement),
    queryFn: () => renamerApi.testRegex(args.pattern, args.caseSensitive, args.input, args.replacement),
    enabled: args.pattern !== '' && args.input !== '',
    placeholderData: keepPreviousData,
  })

  if (!sample) return <p className={styles.testerEmpty}>Choose a folder to test the expression on its first file.</p>

  const data = test.data
  const matches = data?.matches ?? []
  const parts: { text: string; hit: boolean }[] = []
  let at = 0
  for (const m of matches) {
    if (m.index > at) parts.push({ text: input.slice(at, m.index), hit: false })
    parts.push({ text: input.slice(m.index, m.index + m.length) || '∅', hit: true })
    at = m.index + m.length
  }
  if (at < input.length) parts.push({ text: input.slice(at), hit: false })
  const groups = matches[0]?.groups ?? []

  return (
    <div className={styles.tester} aria-live="polite">
      <div className={styles.testerHead}>
        <span>Test on the first file</span>
        {data && !data.error && <span className={styles.testerCount}>{matches.length === 0 ? 'No match' : `${matches.length} match${matches.length === 1 ? '' : 'es'}`}</span>}
      </div>
      {data?.error ? (
        <p className={ui.error}>{data.error}</p>
      ) : (
        <>
          <code className={styles.testerSample}>
            {parts.length === 0
              ? input
              : parts.map((p, i) =>
                  p.hit ? (
                    <mark key={i} className={styles.testerHit}>
                      {p.text}
                    </mark>
                  ) : (
                    <span key={i}>{p.text}</span>
                  ),
                )}
          </code>
          {groups.length > 0 && (
            <table className={styles.groups}>
              <tbody>
                {groups.map((g) => (
                  <tr key={g.number}>
                    <th>
                      ${g.number}
                      {g.name !== String(g.number) && <span className={styles.groupName}> {g.name}</span>}
                    </th>
                    <td>{g.value == null ? <span className={ui.muted}>not matched</span> : <code>{g.value || '""'}</code>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {data?.result != null && matches.length > 0 && (
            <p className={styles.testerResult}>
              <span>Result</span>
              <code>{data.result}</code>
            </p>
          )}
        </>
      )}
    </div>
  )
}
