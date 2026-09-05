import { photoUrl } from './types'
import { useState } from 'react'
import { command, useCountdown, useSession } from './useSession'

const MOCK_MODES = [
  ['Normal', 'Simulate press'],
  ['DuplicateName', 'Duplicate name'],
  ['Stale', 'Stale file'],
  ['NeverFinishes', 'Stalled transfer'],
] as const

/** The laptop screen. Everything the operator can actually do. */
export function Operator() {
  const { snapshot, camera, connected } = useSession()
  const [mockResult, setMockResult] = useState<{ ok: boolean; text: string } | null>(null)
  const remaining = useCountdown(snapshot?.countdownEndsUtc ?? null)

  if (!snapshot) {
    return <main className="operator"><p className="muted">Connecting…</p></main>
  }

  const state = snapshot.state
  const running = state !== 'Idle' && state !== 'Done'

  const press = async (mode: string) => {
    try {
      const r = await fetch(`/api/mock/press?mode=${mode}`, { method: 'POST' })
      const body = await r.json()
      if (!r.ok) {
        setMockResult({ ok: false, text: body.error ?? `HTTP ${r.status}` })
        return
      }

      setMockResult({
        ok: true,
        text: running
          ? `wrote ${body.file} (${body.mode})`
          : `wrote ${body.file} — but no session is running, so it will be ` +
            `ingested and then ignored. Click Start session first.`,
      })
    } catch (e) {
      setMockResult({ ok: false, text: e instanceof Error ? e.message : 'Request failed' })
    }
  }

  return (
    <main className="operator">
      <header>
        <h1>Operator</h1>
        <span className={`pill pill--${state}`}>{state}</span>
        {camera && <span className={`pill pill--${camera.status}`}>Camera {camera.status}</span>}
        {!connected && <span className="pill pill--Faulted">Reconnecting…</span>}
        <a href="/diagnostics">diagnostics →</a>
      </header>

      {snapshot.message && <p className="banner">{snapshot.message}</p>}

      <section className="controls">
        <button className="primary" onClick={() => command('arm')}>
          {running ? 'Restart session' : 'Start session'}
        </button>
        <button disabled={!running} onClick={() => command('retake')}>
          Retake last
        </button>
        <button disabled={state !== 'TimedOut'} onClick={() => command('resume')}>
          Keep waiting
        </button>
        <button disabled={state !== 'ReviewShots'} onClick={() => command('accept')}>
          Accept
        </button>
        <button disabled={!running} onClick={() => command('abort')}>
          Abort
        </button>
      </section>

      <p className="status">
        {state === 'Countdown' && remaining !== null && (
          <>Countdown {Math.max(0, remaining).toFixed(1)}s — press the remote on “1”.</>
        )}
        {state === 'Collecting' && <>Waiting for photo {snapshot.currentShot}…</>}
        {state === 'ReviewShots' && <>All {snapshot.shotCount} shots in.</>}
        {state === 'Idle' && <>Ready for the next guest.</>}
      </p>

      <section className="shots">
        {snapshot.photos.map((p) => (
          <figure key={p.fileName}>
            <img src={photoUrl(p)} alt={p.fileName} />
            <figcaption>
              {p.fileName} · {Math.round(p.sizeBytes / 1024)} KB
            </figcaption>
          </figure>
        ))}
        {snapshot.photos.length === 0 && <p className="muted">No photos yet.</p>}
      </section>

      <section className="mock">
        {/*
          The app cannot fire the shutter -- CanTrigger is false -- so these are
          not camera controls. They stand in for the BR-E1 remote so the whole
          flow can be exercised with no camera, including the ways EOS Utility is
          expected to misbehave.
        */}
        <h2>Mock camera {camera?.canTrigger === false && <span className="muted small">(app cannot trigger the real shutter)</span>}</h2>
        <div className="controls">
          {MOCK_MODES.map(([mode, label]) => (
            <button key={mode} onClick={() => press(mode)}>{label}</button>
          ))}
        </div>
        {mockResult && (
          <p className={mockResult.ok ? 'muted small' : 'banner'}>{mockResult.text}</p>
        )}
        {camera && <p className="muted small">Watch folder: <code>{camera.watchFolder}</code></p>}
      </section>
    </main>
  )
}
