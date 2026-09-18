import { useEffect, useState } from 'react'
import { AppShell, Panel } from './AppShell'

interface SessionRecord {
  token: string
  folderName: string
  createdUtc: string
  template: string
  shotCount: number
  strip: string
  photos: string[]
  animation: string | null
  uploadState: string
}

interface SessionsResponse {
  root: string
  freeDiskBytes: number
  diskIsLow: boolean
  sessions: SessionRecord[]
}

/**
 * Every session this booth has taken, newest first.
 *
 * The one thing the console could not do before: a guest comes back an hour
 * later having lost their link, or asks for their photos by email next week, and
 * the only answer was to go digging through dated folders in Explorer. Now they
 * are here, with the strip to recognise them by.
 *
 * Read-only on purpose. Deleting a guest's photos is a real decision and the
 * output folder is the right place to make it, deliberately, rather than from a
 * button that can be brushed past mid-event.
 */
export function Gallery() {
  const [data, setData] = useState<SessionsResponse | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = async () => {
    try {
      const r = await fetch('/api/sessions')
      if (!r.ok) {
        setError(`The booth answered ${r.status}.`)
        return
      }
      setData(await r.json())
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not reach the booth.')
    }
  }

  useEffect(() => {
    void load()

    // Slow on purpose. A session appears here when it finishes, and nobody is
    // watching this page waiting for one.
    const id = setInterval(load, 10_000)
    return () => clearInterval(id)
  }, [])

  const sessions = data?.sessions ?? []

  return (
    <AppShell page="/gallery">
      <Panel
        title="Guest gallery"
        actions={
          <button className="btn" onClick={() => void load()}>Refresh</button>
        }
      >
        <div className="gallerybar">
          <p className="muted small">
            {sessions.length === 0
              ? 'No sessions yet.'
              : `${sessions.length} session${sessions.length === 1 ? '' : 's'}, newest first.`}
            {data && <> Saved in <code className="path">{data.root}</code>.</>}
          </p>

          {data?.diskIsLow && (
            <span className="pill pill--Faulted">Disk space is low</span>
          )}
        </div>

        {error && <p className="banner">{error}</p>}
      </Panel>

      {sessions.length > 0 && (
        <div className="sessions">
          {sessions.map((s) => (
            <SessionCard key={s.folderName} session={s} />
          ))}
        </div>
      )}

      {sessions.length === 0 && !error && (
        <Panel>
          <p className="muted small">
            Finished sessions appear here as soon as a guest is done. Each one
            keeps its strip, its animation and the original photos, so you can
            find a guest again after the event without going through the output
            folder by hand.
          </p>
        </Panel>
      )}
    </AppShell>
  )
}

function SessionCard({ session }: { session: SessionRecord }) {
  const at = new Date(session.createdUtc)
  const file = (name: string) => `/api/sessions/${session.folderName}/${name}`

  return (
    <article className="sessioncard">
      <a className="sessioncard__art" href={file(session.strip)} target="_blank" rel="noreferrer">
        <img src={file(session.strip)} alt={`Strip from ${at.toLocaleString()}`} loading="lazy" />
      </a>

      <div className="sessioncard__body">
        <span className="sessioncard__when">
          {at.toLocaleDateString(undefined, { day: 'numeric', month: 'short' })}
          {', '}
          {at.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })}
        </span>

        <span className="sessioncard__meta">
          {session.photos.length} photo{session.photos.length === 1 ? '' : 's'}
          {' · '}{session.template}
        </span>

        <div className="sessioncard__links">
          <a href={file(session.strip)} target="_blank" rel="noreferrer">Strip</a>

          {session.animation && (
            <a href={file(session.animation)} target="_blank" rel="noreferrer">GIF</a>
          )}

          {session.photos.map((p, i) => (
            <a key={p} href={file(p)} target="_blank" rel="noreferrer">{i + 1}</a>
          ))}
        </div>
      </div>
    </article>
  )
}
