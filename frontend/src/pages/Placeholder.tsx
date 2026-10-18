import { Link } from 'react-router'
import Icon from '../components/Icon'
import ui from '../components/ui.module.css'

/** Not found (and pages still to come), as a CloudZH .empty card. */
export default function Placeholder({ title, phase }: { title: string; phase?: string }) {
  return (
    <div className={`${ui.panel} ${ui.empty}`} style={{ marginTop: 8, paddingBlock: 64 }}>
      <Icon name="compass" size={30} className={ui.muted} />
      <h3>{title}</h3>
      <p>{phase ? `Coming in version ${phase}.` : 'This page does not exist. The link may be old or mistyped.'}</p>
      <Link className={`${ui.primary} ${ui.sm}`} to="/" style={{ marginTop: 8 }}>
        Back to the overview
      </Link>
    </div>
  )
}
