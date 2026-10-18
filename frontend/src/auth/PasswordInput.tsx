import { useState, type InputHTMLAttributes } from 'react'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'
import styles from './Auth.module.css'

/** Password field with a show/hide button (CloudZH .pw-wrap). */
export default function PasswordInput(props: Omit<InputHTMLAttributes<HTMLInputElement>, 'type' | 'className'>) {
  const [visible, setVisible] = useState(false)
  return (
    <span className={styles.pwWrap}>
      <input {...props} type={visible ? 'text' : 'password'} className={`${ui.input} ${styles.input}`} spellCheck={false} />
      <button
        type="button"
        onClick={() => setVisible(!visible)}
        aria-label={visible ? 'Hide password' : 'Show password'}
        aria-pressed={visible}
        title={visible ? 'Hide password' : 'Show password'}
      >
        <Icon name={visible ? 'eyeoff' : 'eye'} size={16} />
      </button>
    </span>
  )
}
