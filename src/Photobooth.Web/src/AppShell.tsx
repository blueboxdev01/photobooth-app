import { useEffect, useState, type ReactNode } from 'react'
import { useTheme, type ThemeChoice } from './useTheme'

interface BoothStatus {
  cameraStatus: string
  version: string
}

interface Folders {
  watchFolder: string
  outputFolder: string
}

/**
 * The rail, in the order the night runs: start a guest, find their photos
 * afterwards, change the layout between events. Settings sits apart at the foot
 * because it is set up once and then left alone.
 */
const NAV = [
  { href: '/operator', label: 'Start Session', hint: 'Run the booth', icon: 'session' },
  { href: '/gallery', label: 'Guest Gallery', hint: 'Find a guest’s photos', icon: 'gallery' },
  { href: '/templates', label: 'Templates', hint: 'Frame and layout', icon: 'templates' },
] as const

const SETTINGS = {
  href: '/diagnostics',
  label: 'Settings',
  hint: 'Folders, wifi and health',
  icon: 'settings',
} as const

export type Page = (typeof NAV)[number]['href'] | typeof SETTINGS.href

/**
 * The operator console frame: a fixed rail on the left, the work on the right,
 * and a strip across the top carrying the two things worth knowing at all times
 * -- whether the camera is alive, and where the files are going.
 *
 * The guest display deliberately does not use this shell. It is full-bleed and
 * carries the event's own backdrop.
 */
export function AppShell({
  page,
  aside,
  children,
}: {
  page: Page
  aside?: ReactNode
  children: ReactNode
}) {
  const { choice, set } = useTheme()
  const [status, setStatus] = useState<BoothStatus | null>(null)
  const [folders, setFolders] = useState<Folders | null>(null)

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

  // Fetched once: folders change when somebody changes them, not on their own.
  useEffect(() => {
    let cancelled = false
    void (async () => {
      try {
        const r = await fetch('/api/settings')
        if (!r.ok) return
        const body = await r.json()
        if (!cancelled) {
          setFolders({
            watchFolder: body.watchFolder ?? '',
            outputFolder: body.outputFolder ?? '',
          })
        }
      } catch { /* the topbar simply shows no paths */ }
    })()
    return () => { cancelled = true }
  }, [])

  const camera = status?.cameraStatus ?? '—'

  return (
    <div className="shell">
      <aside className="rail">
        <div className="rail__brand">
          <span className="rail__mark" aria-hidden="true" />
          <span className="rail__name">MemoStills</span>
        </div>

        <nav className="rail__nav" aria-label="Sections">
          <p className="rail__group">Main</p>

          {NAV.map((item) => (
            <NavItem key={item.href} item={item} current={page} />
          ))}
        </nav>

        {aside && <div className="rail__panel">{aside}</div>}

        <div className="rail__foot">
          <NavItem item={SETTINGS} current={page} />

          <div className="rail__meta">
            <ThemeSwitch choice={choice} onChange={set} />
            {status?.version && <code title="Build">{status.version}</code>}
          </div>
        </div>
      </aside>

      <main className="work">
        <header className="topbar">
          <span className={`camerapill camerapill--${camera}`}>
            <span className="dot" aria-hidden="true" />
            Camera {camera}
          </span>

          <div className="topbar__folders">
            <FolderChip label="Watch folder" path={folders?.watchFolder} icon="watch" />
            <FolderChip label="Destination folder" path={folders?.outputFolder} icon="folder" />
          </div>
        </header>

        <div className="work__body">{children}</div>
      </main>
    </div>
  )
}

function NavItem({
  item,
  current,
}: {
  item: { href: string; label: string; hint: string; icon: string }
  current: string
}) {
  const on = item.href === current

  return (
    <a
      href={item.href}
      className={on ? 'navitem navitem--on' : 'navitem'}
      aria-current={on ? 'page' : undefined}
    >
      <Icon name={item.icon} />
      <span className="navitem__text">
        <span className="navitem__label">{item.label}</span>
        <span className="navitem__hint">{item.hint}</span>
      </span>
    </a>
  )
}

/**
 * Where the files are going. A path is long and nobody needs to read it
 * constantly, so the chip names the folder and the full path is one hover away
 * -- and it links to Settings, which is where it can be changed.
 */
function FolderChip({ label, path, icon }: { label: string; path?: string; icon: string }) {
  return (
    <a className="folderchip" href="/diagnostics" title={path || 'Not set yet'}>
      <Icon name={icon} />
      <span>{label}</span>
    </a>
  )
}

/**
 * Drawn rather than pulled from an icon font. The booth runs at venues with no
 * internet, and a missing glyph in the rail is navigation nobody can read.
 */
function Icon({ name }: { name: string }) {
  const paths: Record<string, ReactNode> = {
    session: (
      <>
        <circle cx="12" cy="12" r="8.2" />
        <circle cx="12" cy="12" r="3.4" />
      </>
    ),
    gallery: (
      <>
        <rect x="3.2" y="5.2" width="17.6" height="13.6" rx="2.4" />
        <path d="M3.6 15.4l4.2-4a2 2 0 0 1 2.7 0l5.3 5" />
        <circle cx="15.6" cy="9.4" r="1.5" />
      </>
    ),
    templates: (
      <>
        <rect x="3.4" y="3.4" width="7.2" height="7.2" rx="1.8" />
        <rect x="13.4" y="3.4" width="7.2" height="7.2" rx="1.8" />
        <rect x="3.4" y="13.4" width="7.2" height="7.2" rx="1.8" />
        <rect x="13.4" y="13.4" width="7.2" height="7.2" rx="1.8" />
      </>
    ),
    settings: (
      <>
        <path d="M4 7h10M18 7h2M4 17h2M10 17h10" />
        <circle cx="16" cy="7" r="2.2" />
        <circle cx="8" cy="17" r="2.2" />
      </>
    ),
    watch: (
      <>
        <circle cx="8" cy="12" r="3.4" />
        <circle cx="16" cy="12" r="3.4" />
        <path d="M8 8.6V6.4M16 8.6V6.4" />
      </>
    ),
    folder: (
      <path d="M3.6 6.6a1.8 1.8 0 0 1 1.8-1.8h3.3l2 2.4h7.9a1.8 1.8 0 0 1 1.8 1.8v8.6a1.8 1.8 0 0 1-1.8 1.8H5.4a1.8 1.8 0 0 1-1.8-1.8z" />
    ),
  }

  return (
    <svg className="icon" viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      {paths[name]}
    </svg>
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
