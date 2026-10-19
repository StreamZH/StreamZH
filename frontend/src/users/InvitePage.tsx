import { useMutation, useQuery } from '@tanstack/react-query'
import { useEffect, useRef, useState, type FormEvent } from 'react'
import { api, ApiError } from '../api/client'
import { roleNames, usersApi } from '../api/users'
import Brand from '../auth/Brand'
import PasswordInput from '../auth/PasswordInput'
import { shake } from '../auth/shake'
import authStyles from '../auth/Auth.module.css'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { splashDone } from '../lib/splash'
import { withBase } from '../lib/urlBase'
import styles from './Users.module.css'

/**
 * The public page behind an invite link (no sign-in, no app shell): shows who the link is for, lets the person choose
 * a username and password, signs them in and opens StreamZH.
 */
export default function InvitePage({ token }: { token: string }) {
  const state = useQuery({ queryKey: ['auth-state'], queryFn: api.authState, staleTime: Infinity, retry: false })
  const check = useQuery({ queryKey: ['invite', token], queryFn: () => usersApi.checkInvite(token), retry: false })
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [repeat, setRepeat] = useState('')
  const [mismatch, setMismatch] = useState(false)
  const card = useRef<HTMLFormElement>(null)
  const instance = state.data?.instanceName ?? 'StreamZH'

  const accept = useMutation({
    mutationFn: () => usersApi.acceptInvite(token, username.trim(), password),
    // A fixed address inside StreamZH: the app opens signed in as the new user.
    onSuccess: () => window.location.replace(withBase('/')),
    onError: () => shake(card.current),
  })
  const fields = accept.error instanceof ApiError ? accept.error.fieldErrors : {}

  useEffect(() => {
    if (!check.isPending) splashDone()
  }, [check.isPending])

  useEffect(() => {
    document.title = `Invitation · ${instance}`
  }, [instance])

  const submit = (e: FormEvent) => {
    e.preventDefault()
    setMismatch(password !== repeat)
    if (password === repeat) {
      accept.mutate()
    } else {
      shake(card.current)
    }
  }

  if (check.isPending) return <div className={authStyles.loading} aria-busy="true" />

  // Unknown, used up, expired or withdrawn (or the server refused: too many attempts).
  const problem = check.isError ? check.error.message : check.data.valid ? null : (check.data.message ?? 'This invite link is not valid.')
  const gone = accept.error instanceof ApiError && (accept.error.status === 410 || accept.error.status === 404)

  return (
    <div className={authStyles.screen}>
      <div className={authStyles.wrap}>
        <Brand />
        {problem || gone ? (
          <div className={authStyles.card} role="alert">
            <div className={authStyles.downIcon}>
              <Icon name="alert" size={22} />
            </div>
            <div className={authStyles.stack}>
              <h1 className={authStyles.title}>Invitation not valid</h1>
              <p className={authStyles.lead}>{gone ? accept.error?.message : problem}</p>
            </div>
            <a className={`${ui.button} ${ui.block}`} href={withBase('/')}>
              Go to sign-in
            </a>
          </div>
        ) : (
          <form ref={card} className={authStyles.card} onSubmit={submit} noValidate>
            <div className={authStyles.stack}>
              <span className={`${styles.role} ${styles[`role_${check.data!.role ?? 'friend'}`]} ${styles.inviteRole}`}>
                <Icon name="send" size={13} />
                {roleNames[check.data!.role ?? 'friend']}
              </span>
              <h1 className={authStyles.title}>You are invited</h1>
              <p className={authStyles.lead}>
                Create your account for {instance}.{' '}
                {check.data!.role === 'requester' ? 'You can find movies and series and request them.' : 'You can browse the library and request movies and series.'}
              </p>
            </div>
            {check.data!.note && (
              <p className={ui.infoBox}>
                <Icon name="chat" size={16} />
                <span>{check.data!.note}</span>
              </p>
            )}

            <label className={ui.field}>
              Username
              <input
                className={`${ui.input} ${authStyles.input}`}
                value={username}
                onChange={(e) => setUsername(e.target.value)}
                autoComplete="username"
                spellCheck={false}
                autoFocus
              />
              {fields.username ? <small className={authStyles.fieldError}>{fields.username[0]}</small> : <small>Letters, numbers, dot, dash or underscore.</small>}
            </label>
            <label className={ui.field}>
              Password
              <PasswordInput value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" />
              {fields.password ? <small className={authStyles.fieldError}>{fields.password[0]}</small> : <small>At least 8 characters.</small>}
            </label>
            <label className={ui.field}>
              Repeat password
              <PasswordInput value={repeat} onChange={(e) => setRepeat(e.target.value)} autoComplete="new-password" />
              {mismatch && <small className={authStyles.fieldError}>The passwords do not match.</small>}
            </label>

            {accept.error && !gone && Object.keys(fields).length === 0 && (
              <p className={`${ui.errBox} ${authStyles.error}`} role="alert">
                <Icon name="alert" size={16} />
                <span>{accept.error.message}</span>
              </p>
            )}

            <button className={`${ui.primary} ${ui.block}`} type="submit" disabled={accept.isPending || accept.isSuccess}>
              {accept.isPending || accept.isSuccess ? 'Creating…' : 'Create account'}
            </button>
          </form>
        )}
      </div>
    </div>
  )
}
