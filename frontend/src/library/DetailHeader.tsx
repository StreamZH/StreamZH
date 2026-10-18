import type { ReactNode } from 'react'
import { Link } from 'react-router'
import Icon from '../components/Icon'
import Poster from '../components/Poster'
import { tmdbImage } from '../lib/format'
import styles from './Detail.module.css'

/** The head of a movie or series page (CloudZH .detail): backdrop, poster, serif title, meta pills, a compact action row. */
export default function DetailHeader(props: {
  title: string
  posterPath: string | null
  backdropPath: string | null
  facts: (string | null | false)[]
  badges: ReactNode
  overview: string | null
  actions: ReactNode
  back: { to: string; label: string }
}) {
  const backdrop = tmdbImage(props.backdropPath, 'w1280')

  return (
    <section className={styles.hero}>
      {backdrop && <div className={styles.backdrop} style={{ backgroundImage: `url(${backdrop})` }} />}
      <div className={styles.heroInner}>
        <div className={styles.poster}>
          <Poster path={props.posterPath} title={props.title} size="w500" />
        </div>
        <div className={styles.info}>
          <Link className={styles.back} to={props.back.to}>
            <Icon name="left" size={14} />
            {props.back.label}
          </Link>
          <h1 className={styles.title}>{props.title}</h1>
          <p className={styles.facts}>{props.facts.filter(Boolean).join(' · ')}</p>
          <div className={styles.badges}>{props.badges}</div>
          {props.overview && <p className={styles.overview}>{props.overview}</p>}
          <div className={styles.actions}>{props.actions}</div>
        </div>
      </div>
    </section>
  )
}
