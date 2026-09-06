import { useEffect, useState, type ReactNode } from 'react'
import { useTheme, type ThemeChoice } from './useTheme'

interface BoothStatus {
  cameraStatus: string
  version: string
}

const NAV = [
  { href: '/operator', label: 'Session', hint: 'Run the booth' },
  { href: '/templates', label: 'Templates', hint: 'Frame and layout' },
  { href: '/diagnostics', label: 'Setup', hint: 'Folders and health' },
] as const

/**
 * The operator console frame: a fixed rail of controls on the left, the work on
 * the right.
 *
 * Everything actionable lives in the rail so the main area can stay a readout —
 * this is glanced at across a room while talking to a guest, not studied. The
 * guest display deliberately does not use this shell; it is full-bleed and
 * carries the event's own backdrop.
 */
export function AppShell({
  page,
  aside,
  children,
}: {
  page: (typeof NAV)[number]['href']
  aside?: ReactNode
  children: ReactNode
}) {
  const { choice, set } = useTheme()
  const [status, setStatus] = useState<BoothStatus | null>(null)

  useEffect(() => {
    let cancelled = false
    const load = async () => {
      try {
        const r = await fetch('/api/state')
        if (!r.ok) return
        const body = await r.json()
        if (!cancelled) {
          setStatus({
            cameraStatus: body.camera?.status ?? 'Unknown',
            version: body.build?.version ?? '',
          })
        }
      } catch {
        if (!cancelled) setStatus((s) => (s ? { ...s, cameraStatus: 'Offline' } : null))
      }
    }
    void load()
    const id = setInterval(load, 3000)
    return () => {
      cancelled = true
      clearInterval(id)
    }
  }, [])

  const camera = status?.cameraStatus ?? '—'

  return (
    <div className="shell">
      <aside className="rail">
        <div className="rail__brand">
          <span className="rail__mark" aria-hidden="true" />
          <span className="rail__name">Photobooth</span>
        </div>

        <div className={`rail__status rail__status--${camera}`}>
          <span className="dot" aria-hidden="true" />
          <span>Camera {camera}</span>
        </div>

        <nav className="rail__nav" aria-label="Sections">
          {NAV.map((item) => (
            <a
              key={item.href}
              href={item.href}
              className={item.href === page ? 'navitem navitem--on' : 'navitem'}
              aria-current={item.href === page ? 'page' : undefined}
            >
              <span className="navitem__label">{item.label}</span>
              <span className="navitem__hint">{item.hint}</span>
            </a>
          ))}
        </nav>

        {aside && <div className="rail__panel">{aside}</div>}

        <div className="rail__foot">
          <ThemeSwitch choice={choice} onChange={set} />
          <div className="rail__meta">
            <a href="/display" target="_blank" rel="noreferrer">Guest display ↗</a>
            {status?.version && <code>{status.version}</code>}
          </div>
        </div>
      </aside>

      <main className="work">{children}</main>
    </div>
  )
}

/**
 * Three states rather than two: "Auto" follows the machine, which is usually
 * already right, and is the honest default. A plain on/off toggle would have to
 * silently pick one the first time.
 */
function ThemeSwitch({
  choice,
  onChange,
}: {
  choice: ThemeChoice
  onChange: (next: ThemeChoice) => void
}) {
  const options: { id: ThemeChoice; label: string; glyph: string }[] = [
    { id: 'light', label: 'Light', glyph: '☀' },
    { id: 'system', label: 'Auto', glyph: '◐' },
    { id: 'dark', label: 'Dark', glyph: '☾' },
  ]

  return (
    <div className="themeswitch" role="group" aria-label="Colour scheme">
      {options.map((o) => (
        <button
          key={o.id}
          type="button"
          className={o.id === choice ? 'themeswitch__on' : undefined}
          aria-pressed={o.id === choice}
          title={o.label}
          onClick={() => onChange(o.id)}
        >
          <span aria-hidden="true">{o.glyph}</span>
          <span className="sr-only">{o.label}</span>
        </button>
      ))}
    </div>
  )
}

/** A titled block for the rail. */
export function RailSection({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="railsection">
      <h2>{title}</h2>
      {children}
    </section>
  )
}

/** A titled block for the work area. */
export function Panel({
  title,
  actions,
  children,
}: {
  title?: string
  actions?: ReactNode
  children: ReactNode
}) {
  return (
    <section className="panel">
      {(title || actions) && (
        <header className="panel__head">
          {title && <h2>{title}</h2>}
          {actions}
        </header>
      )}
      {children}
    </section>
  )
}
