import Logo from '../components/Logo'
import styles from './Auth.module.css'

export default function Brand() {
  return (
    <div className={styles.brand}>
      <Logo size={36} />
    </div>
  )
}
