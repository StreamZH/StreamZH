import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { api } from '../api/client'
import { authQueryKey } from './auth'
import Brand from './Brand'
import styles from './Auth.module.css'

export default function LoginPage({ instanceName }: { instanceName: string }) {
  const queryClient = useQueryClient()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [rememberMe, setRememberMe] = useState(true)

  const login = useMutation({
    mutationFn: () => api.login(username, password, rememberMe),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: authQueryKey }),
  })

  const submit = (event: FormEvent) => {
    event.preventDefault()
    login.mutate()
  }

  return (
    <div className={styles.screen}>
      <form className={styles.card} onSubmit={submit}>
        <Brand />
        <div>
          <h1 className={styles.title}>Sign in</h1>
          {instanceName !== 'StreamZH' && <p className={styles.lead}>{instanceName}</p>}
        </div>

        <label className={styles.field}>
          <span>Username</span>
          <input value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="username" autoFocus required />
        </label>

        <label className={styles.field}>
          <span>Password</span>
          <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" required />
        </label>

        <label className={styles.check}>
          <input type="checkbox" checked={rememberMe} onChange={(e) => setRememberMe(e.target.checked)} />
          <span>Stay signed in for 30 days</span>
        </label>

        {login.error && <p className={styles.error}>{login.error.message}</p>}

        <button className={styles.primary} type="submit" disabled={login.isPending}>
          {login.isPending ? 'Signing in…' : 'Sign in'}
        </button>
      </form>
    </div>
  )
}
