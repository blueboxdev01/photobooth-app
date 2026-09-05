import { photoUrl } from './types'
import type { SessionSnapshot } from './types'
import { useCountdown, useSession } from './useSession'
import { PosingMirror } from './PosingMirror'

/** The guest-facing screen. Fullscreen on the external monitor. */
export function Display() {
  const { snapshot } = useSession()

  if (!snapshot) {
    return <div className="stage stage--idle"><p className="muted">Connecting…</p></div>
  }

  switch (snapshot.state) {
    case 'Idle':
      return (
        <div className="stage stage--idle">
          <h1>Step in and smile</h1>
          <p className="muted">{snapshot.shotCount} photos, then your QR code</p>
          {snapshot.message && <p className="muted small">{snapshot.message}</p>}
        </div>
      )

    case 'Countdown':
      return <Posing snapshot={snapshot} counting />

    case 'Collecting':
      return <Posing snapshot={snapshot} counting={false} />

    case 'TimedOut':
      return (
        <div className="stage stage--idle">
          <h1>Just a moment…</h1>
          <p className="muted">Hang tight while we sort the camera out.</p>
          <Filmstrip snapshot={snapshot} />
        </div>
      )

    case 'ReviewShots':
      return (
        <div className="stage">
          <h1>How do these look?</h1>
          <Filmstrip snapshot={snapshot} large />
        </div>
      )

    default:
      return (
        <div className="stage stage--idle">
          <h1>All done</h1>
          <p className="muted">Your photos are on their way.</p>
          <Filmstrip snapshot={snapshot} />
        </div>
      )
  }
}

function Posing({ snapshot, counting }: { snapshot: SessionSnapshot; counting: boolean }) {
  const remaining = useCountdown(counting ? snapshot.countdownEndsUtc : null)

  return (
    <div className="stage stage--posing">
      <div className="stage__mirror">
        <PosingMirror />
        {counting && remaining !== null && (
          <div className="countdown" key={Math.ceil(remaining)}>
            {Math.max(1, Math.ceil(remaining))}
          </div>
        )}
        {!counting && <div className="hold">Hold it…</div>}
      </div>
      <p className="shotcount">
        Photo {snapshot.currentShot} of {snapshot.shotCount}
      </p>
      <Filmstrip snapshot={snapshot} />
    </div>
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
