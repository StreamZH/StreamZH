import styles from './Auth.module.css'

/** Restarts the short shake of the card (CloudZH .shake) after a failed attempt. */
export function shake(element: HTMLElement | null) {
  if (!element) return
  element.classList.remove(styles.shake)
  void element.offsetWidth
  element.classList.add(styles.shake)
}
