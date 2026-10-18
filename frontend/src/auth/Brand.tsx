import Logo from '../components/Logo'
import styles from './Auth.module.css'

/** The logo above the sign-in card, centred like CloudZH's .login-wrap .brand. */
export default function Brand() {
  return (
    <div className={styles.brand}>
      <Logo size={40} />
    </div>
  )
}
