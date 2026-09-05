import { useCallback, useEffect, useState } from 'react'
import { useWebcams } from './useWebcams'

interface IngestRow {
  atUtc: string
  fileName: string
  outcome: 'Accepted' | 'Rejected' | 'Abandoned'
  reason: string
  sizeBytes: number
}

interface Diag {
  build: { version: string; startedUtc: string; framework: string; machine: string; os: string }
  camera: { status: string; canTrigger: boolean; abandoned: string[] }
  watchFolder: {
    path: string
    exists: boolean
    writable: boolean
    freeDiskBytes: number | null
    recentFiles: { name: string; sizeBytes: number; lastWriteUtc: string; watched: boolean }[]
  }
  assumptions: Record<string, unknown>
  latency: {
    samples: { pressedAtUtc: string; fileName: string; seconds: number }[]
    count: number
    averageSeconds: number | null
    maxSeconds: number | null
  }
  ingest: IngestRow[]
}

const gb = (bytes: number | null) =>
  bytes === null ? 'unknown' : `${(bytes / 1024 ** 3).toFixed(1)} GB free`

/**
 * The page that makes a test in another building debuggable.
 *
 * Everything here exists because the person running the camera cannot show you
 * their screen: what the app saw, what it threw away and why, how long the
 * camera actually took, and which build produced the answer.
 */
export function Diagnostics() {
  const [d, setD] = useState<Diag | null>(null)
  const [marked, setMarked] = useState<string | null>(null)
  const { devices, selected, choose, error: camError, refresh } = useWebcams()

  const load = useCallback(async () => {
    try {
      const r = await fetch('/api/diagnostics')
      if (r.ok) setD(await r.json())
    } catch {
      /* server restarting */
    }
  }, [])

  useEffect(() => {
    void load()
    const id = setInterval(load, 1500)
    return () => clearInterval(id)
  }, [load])

  const markPress = async () => {
    await fetch('/api/diagnostics/mark-press', { method: 'POST' })
    setMarked(new Date().toLocaleTimeString())
  }

  if (!d) return <main className="operator"><p className="muted">Loading…</p></main>

  return (
    <main className="operator diagnostics">
      <header>
        <h1>Diagnostics</h1>
        <span className="pill">{d.build.version}</span>
        <span className={`pill pill--${d.camera.status}`}>Camera {d.camera.status}</span>
        <a href="/operator">← operator</a>
      </header>

      <section>
        <h2>Cameras</h2>
        <table>
          <tbody>
            <tr>
              <th>Capture — Canon R50</th>
              <td>
                Reaches the app as JPEGs in the watch folder via EOS Utility.
                The app <strong>cannot</strong> trigger it
                {d.camera.canTrigger ? ' (unexpectedly reports it can!)' : ''}.
              </td>
            </tr>
            <tr>
              <th>Preview — webcam</th>
              <td>
                {camError && <span className="bad">{camError} </span>}
                <select
                  value={selected ?? ''}
                  onChange={(e) => choose(e.target.value || null)}
                >
                  <option value="">Default camera</option>
                  {devices.map((dev, i) => (
                    <option key={dev.deviceId} value={dev.deviceId}>
                      {dev.label || `Camera ${i + 1}`}
                    </option>
                  ))}
                </select>{' '}
                <button onClick={() => void refresh()}>Rescan</button>
                <div className="muted small">
                  {devices.length} video device{devices.length === 1 ? '' : 's'} found.
                  Used for the posing mirror only — never for capture.
                </div>
              </td>
            </tr>
          </tbody>
        </table>
      </section>

      <section>
        <h2>Press-to-file latency</h2>
        <p className="muted small">
          The app cannot know when the shutter fired — that is what an external
          trigger means. Tap this at the moment you press the remote, and the next
          accepted photo is timed against it.
        </p>
        <div className="controls">
          <button className="primary" onClick={markPress}>Mark shutter press</button>
          {marked && <span className="muted">marked at {marked}</span>}
        </div>
        {d.latency.count > 0 ? (
          <p>
            <strong>{d.latency.averageSeconds}s</strong> average over {d.latency.count}{' '}
            press{d.latency.count === 1 ? '' : 'es'} (worst {d.latency.maxSeconds}s)
          </p>
        ) : (
          <p className="muted">No measurements yet.</p>
        )}
      </section>

      <section>
        <h2>Watch folder</h2>
        <p><code>{d.watchFolder.path}</code></p>
        <p className="muted small">
          {d.watchFolder.exists ? 'exists' : <span className="bad">MISSING</span>} ·{' '}
          {d.watchFolder.writable ? 'writable' : <span className="bad">NOT WRITABLE</span>} ·{' '}
          {gb(d.watchFolder.freeDiskBytes)}
        </p>
        {d.camera.abandoned.length > 0 && (
          <p className="banner">
            Gave up on {d.camera.abandoned.length} file(s) that never finished
            transferring.
          </p>
        )}
        <table>
          <thead><tr><th>File</th><th>Size</th><th>Written</th><th>Watched</th></tr></thead>
          <tbody>
            {d.watchFolder.recentFiles.map((f) => (
              <tr key={f.name}>
                <td><code>{f.name}</code></td>
                <td>{Math.round(f.sizeBytes / 1024)} KB</td>
                <td>{new Date(f.lastWriteUtc).toLocaleTimeString()}</td>
                <td>{f.watched ? 'yes' : <span className="muted">ignored</span>}</td>
              </tr>
            ))}
            {d.watchFolder.recentFiles.length === 0 && (
              <tr><td colSpan={4} className="muted">Folder is empty.</td></tr>
            )}
          </tbody>
        </table>
      </section>

      <section>
        <h2>Assumptions about EOS Utility</h2>
        <p className="muted small">
          None of this has been verified against a real camera. It is all
          configuration precisely so that when the field test contradicts it, the
          fix is a settings change rather than a rewrite.
        </p>
        <table>
          <tbody>
            {Object.entries(d.assumptions).map(([k, v]) => (
              <tr key={k}>
                <th>{k}</th>
                <td><code>{Array.isArray(v) ? v.join(', ') : String(v)}</code></td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>

      <section>
        <h2>Ingest decisions</h2>
        <p className="muted small">
          Including the rejections — “nothing happened” is impossible to debug
          remotely, a named reason is not.
        </p>
        <table>
          <thead><tr><th>Time</th><th>File</th><th>Outcome</th><th>Reason</th></tr></thead>
          <tbody>
            {d.ingest.map((e, i) => (
              <tr key={`${e.atUtc}-${i}`}>
                <td>{new Date(e.atUtc).toLocaleTimeString()}</td>
                <td><code>{e.fileName}</code></td>
                <td className={e.outcome === 'Accepted' ? 'good' : 'bad'}>{e.outcome}</td>
                <td className="muted">{e.reason}</td>
              </tr>
            ))}
            {d.ingest.length === 0 && (
              <tr><td colSpan={4} className="muted">Nothing seen yet.</td></tr>
            )}
          </tbody>
        </table>
      </section>

      <section>
        <h2>Send this back</h2>
        <p className="muted small">
          Logs, ingest decisions and settings — <strong>no photographs</strong>.
        </p>
        <a className="button" href="/api/diagnostics/bundle">Download diagnostics bundle</a>
      </section>
    </main>
  )
}
