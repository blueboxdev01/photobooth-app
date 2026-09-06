import { useState } from 'react'
import { AppShell, Panel, RailSection } from './AppShell'
import { photoUrl } from './types'
import type { SessionSnapshot, SessionState } from './types'
import { command, reorder, useCountdown, useSession } from './useSession'

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

      <Panel
        title="Shots"
        actions={state === 'ReviewShots' && snapshot.isReordered ? (
          <button className="btn btn--quiet" onClick={() => command('order/reset')}>
            Back to capture order
          </button>
        ) : undefined}
      >
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

/**
 * The shots, in the order they will be composited.
 *
 * That order is the operator's to change during review: the guest posed six
 * times, and which of those opens the strip is a judgement no software makes
 * well. Dragging is the mouse path; the arrows exist because a booth gets run
 * from a touchscreen, where HTML5 drag does nothing at all.
 *
 * Each thumbnail keeps its capture number, so "shot 4" is still shot 4 after it
 * has moved to the front -- without it, a rearranged strip is impossible to talk
 * about with the person standing next to you.
 */
function Filmstrip({ snapshot }: { snapshot: SessionSnapshot }) {
  const [dragging, setDragging] = useState<number | null>(null)
  const [over, setOver] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)

  const taken = snapshot.photos.length
  const canReorder = snapshot.state === 'ReviewShots' && taken > 1

  /** Lift the shot at `from` out and drop it in at `to`, as a whole permutation. */
  const move = async (from: number, to: number) => {
    if (from === to || from < 0 || to < 0 || from >= taken || to >= taken) return
    const positions = Array.from({ length: taken }, (_, i) => i)
    positions.splice(to, 0, positions.splice(from, 1)[0])
    setError(await reorder(positions))
  }

  const endDrag = () => {
    setDragging(null)
    setOver(null)
  }

  return (
    <>
      {canReorder && (
        <p className="hint">
          Drag a shot, or use the arrows, to change where it lands on the strip.
        </p>
      )}
      {error && <p className="hint hint--bad">{error}</p>}

      <div className="filmrow">
        {Array.from({ length: snapshot.shotCount }).map((_, i) => {
          const photo = snapshot.photos[i]
          if (!photo) {
            return (
              <figure key={`empty-${i}`} className="frame frame--empty">
                <span>{i + 1}</span>
              </figure>
            )
          }

          const shot = (snapshot.order[i] ?? i) + 1
          const classes = [
            'frame',
            canReorder ? 'frame--movable' : '',
            dragging === i ? 'frame--dragging' : '',
            over === i && dragging !== i ? 'frame--over' : '',
          ].filter(Boolean).join(' ')

          return (
            <figure
              key={photo.fileName}
              className={classes}
              draggable={canReorder}
              onDragStart={() => setDragging(i)}
              onDragEnd={endDrag}
              onDragOver={(e) => {
                if (!canReorder || dragging === null) return
                e.preventDefault()
                setOver(i)
              }}
              onDrop={(e) => {
                e.preventDefault()
                if (dragging !== null) void move(dragging, i)
                endDrag()
              }}
            >
              <img src={photoUrl(photo)} alt={`Shot ${shot}`} />

              {canReorder && (
                <div className="frame__move">
                  <button className="btn btn--icon" disabled={i === 0}
                          title={`Move shot ${shot} earlier`}
                          onClick={() => void move(i, i - 1)}>‹</button>
                  <span className="frame__slot">slot {i + 1}</span>
                  <button className="btn btn--icon" disabled={i === taken - 1}
                          title={`Move shot ${shot} later`}
                          onClick={() => void move(i, i + 1)}>›</button>
                </div>
              )}

              <figcaption>
                <span className="frame__shot">shot {shot}</span>
                {photo.fileName}
              </figcaption>
            </figure>
          )
        })}
      </div>
    </>
  )
}
