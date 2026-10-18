import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useRef, useState, type FormEvent } from 'react'
import { api, ApiError } from '../api/client'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { authQueryKey } from './auth'
import Brand from './Brand'
import PasswordInput from './PasswordInput'
import { shake } from './shake'
import styles from './Auth.module.css'

export default function SetupPage() {
  const queryClient = useQueryClient()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [repeat, setRepeat] = useState('')
  const [mismatch, setMismatch] = useState(false)
  const card = useRef<HTMLFormElement>(null)

  const setup = useMutation({
    mutationFn: () => api.setup(username, password),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: authQueryKey }),
    onError: () => shake(card.current),
  })

  const fieldErrors = setup.error instanceof ApiError ? setup.error.fieldErrors : {}

  const submit = (event: FormEvent) => {
    event.preventDefault()
    setMismatch(password !== repeat)
    if (password === repeat) {
      setup.mutate()
    } else {
      shake(card.current)
    }
  }

  return (
    <div className={styles.screen}>
      <div className={styles.wrap}>
        <Brand />
        <form ref={card} className={styles.card} onSubmit={submit} noValidate>
          <div className={styles.stack}>
            <h1 className={styles.title}>Welcome</h1>
            <p className={styles.lead}>Create the owner account. It has full access and manages everyone else.</p>
          </div>

          <label className={ui.field}>
            Username
            <input
              className={`${ui.input} ${styles.input}`}
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              autoComplete="username"
              spellCheck={false}
              autoFocus
            />
            {fieldErrors.username && <small className={styles.fieldError}>{fieldErrors.username[0]}</small>}
          </label>

          <label className={ui.field}>
            Password
            <PasswordInput value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" />
            {fieldErrors.password ? <small className={styles.fieldError}>{fieldErrors.password[0]}</small> : <small>At least 8 characters.</small>}
          </label>

          <label className={ui.field}>
            Repeat password
            <PasswordInput value={repeat} onChange={(e) => setRepeat(e.target.value)} autoComplete="new-password" />
            {mismatch && <small className={styles.fieldError}>The passwords do not match.</small>}
          </label>

          {setup.error && Object.keys(fieldErrors).length === 0 && (
            <p className={`${ui.errBox} ${styles.error}`} role="alert">
              <Icon name="alert" size={16} />
              <span>{setup.error.message}</span>
            </p>
          )}

          <button className={`${ui.primary} ${ui.block}`} type="submit" disabled={setup.isPending}>
            {setup.isPending ? 'Creating…' : 'Create account'}
          </button>
        </form>
      </div>
    </div>
  )
}
