import { createContext, useContext } from 'react'
import type { AuthState } from '../api/client'

export const AuthContext = createContext<AuthState | null>(null)

export const authQueryKey = ['auth-state']

export function useAuth(): AuthState {
  const state = useContext(AuthContext)
  if (!state) {
    throw new Error('useAuth must be used inside AuthGate')
  }
  return state
}
