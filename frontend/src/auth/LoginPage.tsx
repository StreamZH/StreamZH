import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useRef, useState, type FormEvent } from 'react'
import { api } from '../api/client'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import { authQueryKey } from './auth'
import Brand from './Brand'
import PasswordInput from './PasswordInput'
import { shake } from './shake'
import styles from './Auth.module.css'

export default function LoginPage({ instanceName }: { instanceName: string }) {
  const queryClient = useQueryClient()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [rememberMe, setRememberMe] = useState(false)
  const card = useRef<HTMLFormElement>(null)

  const login = useMutation({
    mutationFn: () => api.login(username, password, rememberMe),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: authQueryKey }),
    onError: () => shake(card.current),
  })

  const submit = (event: FormEvent) => {
    event.preventDefault()
    login.mutate()
  }

  return (
    <div className={styles.screen}>
      <div className={styles.wrap}>
        <Brand />
        <form ref={card} className={styles.card} onSubmit={submit}>
          <div className={styles.stack}>
            <h1 className={styles.title}>Sign in</h1>
            <p className={styles.lead}>{instanceName !== 'StreamZH' ? instanceName : 'Welcome back. Sign in to manage your library.'}</p>
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
              required
            />
          </label>

          <label className={ui.field}>
            Password
            <PasswordInput value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" required />
          </label>

          <label className={ui.check}>
            <input type="checkbox" checked={rememberMe} onChange={(e) => setRememberMe(e.target.checked)} />
            <span>Remember me for 30 days</span>
          </label>

          {login.error && (
            <p className={`${ui.errBox} ${styles.error}`} role="alert">
              <Icon name="alert" size={16} />
              <span>{login.error.message}</span>
            </p>
          )}

          <button className={`${ui.primary} ${ui.block}`} type="submit" disabled={login.isPending}>
            {login.isPending ? 'Signing in…' : 'Sign in'}
          </button>
        </form>
      </div>
    </div>
  )
}
