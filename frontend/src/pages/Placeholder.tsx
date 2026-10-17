import styles from './Page.module.css'

export default function Placeholder({ title, phase }: { title: string; phase?: string }) {
  return (
    <div className={styles.page}>
      <h1 className={styles.title}>{title}</h1>
      <div className={styles.panel}>
        <p className={styles.muted}>{phase ? `Coming in version ${phase}.` : 'This page does not exist.'}</p>
      </div>
    </div>
  )
}
