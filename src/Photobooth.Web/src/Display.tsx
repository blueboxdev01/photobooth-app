import { photoUrl } from './types'
import type { SessionSnapshot, SessionState } from './types'
import { useCountdown, useSession } from './useSession'
import { PosingMirror } from './PosingMirror'

/**
 * States that show the guest a live mirror.
 *
 * Idle is included on purpose. An attract screen showing people themselves is
 * what pulls them into the booth, and it means framing and lighting can be
 * checked without starting a session.
 */
const MIRROR_STATES: SessionState[] = ['Idle', 'Countdown', 'Collecting', 'TimedOut']

/** The guest-facing screen. Fullscreen on the external monitor. */
export function Display() {
  const { snapshot } = useSession()

  if (!snapshot) {
    return <div className="stage"><p className="muted">Connecting…</p></div>
  }

  const { state } = snapshot

  // Reviewing photos is the one time the mirror is not wanted -- guests are
  // looking at what they took, not at themselves.
  if (state === 'ReviewShots') {
    return (
      <div className="stage">
        <h1>How do these look?</h1>
        <Filmstrip snapshot={snapshot} large />
      </div>
    )
  }

  if (!MIRROR_STATES.includes(state)) {
    return (
      <div className="stage">
        <h1>All done</h1>
        <p className="muted">Your photos are on their way.</p>
        <Filmstrip snapshot={snapshot} />
      </div>
    )
  }

  return (
    <div className="stage stage--posing">
      {/*
        One PosingMirror across every mirror state. Mounting it per state would
        tear down and re-acquire the webcam on each transition, which shows up as
        a black flash and a second of nothing exactly as the countdown starts.
      */}
      <div className="stage__mirror">
        <PosingMirror />
        <Overlay snapshot={snapshot} />
      </div>
      <Caption snapshot={snapshot} />
      <Filmstrip snapshot={snapshot} />
    </div>
  )
}

function Overlay({ snapshot }: { snapshot: SessionSnapshot }) {
  const counting = snapshot.state === 'Countdown'
  const remaining = useCountdown(counting ? snapshot.countdownEndsUtc : null)

  if (counting && remaining !== null) {
    return (
      <div className="countdown" key={Math.ceil(remaining)}>
        {Math.max(1, Math.ceil(remaining))}
      </div>
    )
  }

  if (snapshot.state === 'Collecting') {
    return <div className="hold">Hold it…</div>
  }

  if (snapshot.state === 'Idle') {
    return <div className="attract">Step in and smile</div>
  }

  return <div className="hold">Just a moment…</div>
}

function Caption({ snapshot }: { snapshot: SessionSnapshot }) {
  if (snapshot.state === 'Idle') {
    return (
      <p className="shotcount">
        {snapshot.shotCount} photos, then your QR code
      </p>
    )
  }

  return (
    <p className="shotcount">
      Photo {snapshot.currentShot} of {snapshot.shotCount}
    </p>
  )
}

/**
 * Shots so far, with empty slots for the ones still to come, so guests can see
 * how far through the session they are.
 */
function Filmstrip({
  snapshot,
  large = false,
}: {
  snapshot: SessionSnapshot
  large?: boolean
}) {
  const slots = Array.from({ length: snapshot.shotCount })

  return (
    <div className={large ? 'filmstrip filmstrip--large' : 'filmstrip'}>
      {slots.map((_, i) => {
        const photo = snapshot.photos[i]
        return (
          <figure key={i} className={photo ? 'shot' : 'shot shot--empty'}>
            {photo ? <img src={photoUrl(photo)} alt={`Photo ${i + 1}`} /> : <span>{i + 1}</span>}
          </figure>
        )
      })}
    </div>
  )
}
