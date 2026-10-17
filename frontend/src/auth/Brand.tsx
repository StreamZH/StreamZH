import styles from './Auth.module.css'

export default function Brand() {
  return (
    <div className={styles.brand}>
      <div className={styles.tile}>ZH</div>
      <div className={styles.wordmark}>
        Stream<span>ZH</span>
      </div>
    </div>
  )
}
