import { useState } from 'react'
import { AppShell, Panel, RailSection } from './AppShell'
import { photoUrl } from './types'
import type { SessionSnapshot, SessionState } from './types'
import { command, useCountdown, useSession } from './useSession'

const MOCK_MODES = [
  ['Normal', 'Simulate press'],
  ['DuplicateName', 'Duplicate name'],
  ['Stale', 'Stale file'],
  ['NeverFinishes', 'Stalled transfer'],
] as const

/** What the operator should understand at a glance, per state. */
const HEADLINE: Record<SessionState, string> = {
  Idle: 'Ready for the next guest',
  Countdown: 'Get ready',
  Collecting: 'Waiting for the photo',
  TimedOut: 'No photo arrived',
  ReviewShots: 'All shots are in',
  Composing: 'Building the strip',
  Uploading: 'Uploading',
  ShowQr: 'Showing the QR code',
  Done: 'Session complete',
}

export function Operator() {
  const { snapshot, camera, connected } = useSession()
  const [mockResult, setMockResult] = useState<{ ok: boolean; text: string } | null>(null)

  if (!snapshot) {
    return (
      <AppShell page="/operator">
        <Panel><p className="muted">Connecting…</p></Panel>
      </AppShell>
    )
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
          ? `wrote ${body.file}`
          : `wrote ${body.file} — no session running, so it will be ignored`,
      })
    } catch (e) {
      setMockResult({ ok: false, text: e instanceof Error ? e.message : 'Request failed' })
    }
  }

  return (
    <AppShell
      page="/operator"
      aside={
        <>
          <RailSection title="Session">
            <button className="btn btn--primary btn--block" onClick={() => command('arm')}>
              {running ? 'Restart session' : 'Start session'}
            </button>
            <div className="btnrow">
              <button className="btn" disabled={!running} onClick={() => command('retake')}>
                Retake
              </button>
              <button className="btn" disabled={state !== 'TimedOut'}
                      onClick={() => command('resume')}>
                Keep waiting
              </button>
            </div>
            <div className="btnrow">
              <button className="btn btn--go" disabled={state !== 'ReviewShots'}
                      onClick={() => command('accept')}>
                Accept
              </button>
              <button className="btn btn--stop" disabled={!running}
                      onClick={() => command('abort')}>
                Abort
              </button>
            </div>
          </RailSection>

          <RailSection title="Mock camera">
            <p className="hint">
              The app cannot fire the shutter. These stand in for the remote.
            </p>
            <div className="btngrid">
              {MOCK_MODES.map(([mode, label]) => (
                <button key={mode} className="btn btn--quiet" onClick={() => press(mode)}>
                  {label}
                </button>
              ))}
            </div>
            {mockResult && (
              <p className={mockResult.ok ? 'hint' : 'hint hint--bad'}>{mockResult.text}</p>
            )}
          </RailSection>
        </>
      }
    >
      {!connected && <p className="notice notice--warn">Reconnecting to the booth…</p>}
      {snapshot.message && <p className="notice">{snapshot.message}</p>}

      <Stage snapshot={snapshot} />

      <Panel title="Shots">
        <Filmstrip snapshot={snapshot} />
      </Panel>

      {snapshot.stripUrl && (
        <Panel
          title="Strip"
          actions={
            <a className="btn btn--quiet" href={snapshot.stripUrl}
               target="_blank" rel="noreferrer">Open full size</a>
          }
        >
          <div className="result">
            <img className="result__strip" src={snapshot.stripUrl} alt="Composed strip" />
            <dl className="facts">
              <dt>Saved to</dt>
              <dd><code>{snapshot.sessionFolder}</code></dd>
              <dt>Contents</dt>
              <dd>{snapshot.shotCount} raw photos, the strip, and session.json</dd>
            </dl>
          </div>
        </Panel>
      )}

      {camera && (
        <Panel title="Watch folder">
          <code className="path">{camera.watchFolder}</code>
        </Panel>
      )}
    </AppShell>
  )
}

/**
 * The readout. Deliberately the largest thing on the screen: it is read from
 * across a booth, mid-conversation, not studied.
 */
function Stage({ snapshot }: { snapshot: SessionSnapshot }) {
  const counting = snapshot.state === 'Countdown'
  const remaining = useCountdown(counting ? snapshot.countdownEndsUtc : null)

  return (
    <section className={`stagecard stagecard--${snapshot.state}`}>
      <div className="stagecard__main">
        <p className="stagecard__state">{snapshot.state}</p>
        <h1 className="stagecard__headline">{HEADLINE[snapshot.state]}</h1>
      </div>

      <div className="stagecard__metric">
        {counting && remaining !== null ? (
          <>
            <span className="metric">{Math.max(0, remaining).toFixed(1)}</span>
            <span className="metric__unit">seconds — press on “1”</span>
          </>
        ) : (
          <>
            <span className="metric">
              {snapshot.capturedCount}<span className="metric__of">/{snapshot.shotCount}</span>
            </span>
            <span className="metric__unit">photos captured</span>
          </>
        )}
      </div>
    </section>
  )
}

function Filmstrip({ snapshot }: { snapshot: SessionSnapshot }) {
  const slots = Array.from({ length: snapshot.shotCount })

  return (
    <div className="filmrow">
      {slots.map((_, i) => {
        const photo = snapshot.photos[i]
        return (
          <figure key={i} className={photo ? 'frame' : 'frame frame--empty'}>
            {photo
              ? <img src={photoUrl(photo)} alt={`Photo ${i + 1}`} />
              : <span>{i + 1}</span>}
            {photo && (
              <figcaption>
                {photo.fileName}
                <span>{Math.round(photo.sizeBytes / 1024)} KB</span>
              </figcaption>
            )}
          </figure>
        )
      })}
    </div>
  )
}
