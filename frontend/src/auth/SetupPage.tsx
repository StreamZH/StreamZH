import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { api, ApiError } from '../api/client'
import { authQueryKey } from './auth'
import Brand from './Brand'
import styles from './Auth.module.css'

export default function SetupPage() {
  const queryClient = useQueryClient()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [repeat, setRepeat] = useState('')
  const [mismatch, setMismatch] = useState(false)

  const setup = useMutation({
    mutationFn: () => api.setup(username, password),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: authQueryKey }),
  })

  const fieldErrors = setup.error instanceof ApiError ? setup.error.fieldErrors : {}

  const submit = (event: FormEvent) => {
    event.preventDefault()
    setMismatch(password !== repeat)
    if (password === repeat) {
      setup.mutate()
    }
  }

  return (
    <div className={styles.screen}>
      <form className={styles.card} onSubmit={submit} noValidate>
        <Brand />
        <div>
          <h1 className={styles.title}>Welcome</h1>
          <p className={styles.lead}>Create the owner account. It has full access and manages everyone else.</p>
        </div>

        <label className={styles.field}>
          <span>Username</span>
          <input value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="username" autoFocus />
          {fieldErrors.username && <small className={styles.error}>{fieldErrors.username[0]}</small>}
        </label>

        <label className={styles.field}>
          <span>Password</span>
          <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" />
          {fieldErrors.password ? (
            <small className={styles.error}>{fieldErrors.password[0]}</small>
          ) : (
            <small className={styles.hint}>At least 8 characters.</small>
          )}
        </label>

        <label className={styles.field}>
          <span>Repeat password</span>
          <input type="password" value={repeat} onChange={(e) => setRepeat(e.target.value)} autoComplete="new-password" />
          {mismatch && <small className={styles.error}>The passwords do not match.</small>}
        </label>

        {setup.error && Object.keys(fieldErrors).length === 0 && <p className={styles.error}>{setup.error.message}</p>}

        <button className={styles.primary} type="submit" disabled={setup.isPending}>
          {setup.isPending ? 'Creating…' : 'Create account'}
        </button>
      </form>
    </div>
  )
}
