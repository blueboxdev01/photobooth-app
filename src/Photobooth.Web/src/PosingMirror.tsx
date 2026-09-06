import { useCallback, useEffect, useRef, useState } from 'react'
import { selectedWebcamId, setSelectedWebcamId } from './useWebcams'

/**
 * The guest-facing live preview.
 *
 * This is a **separate webcam**, not the R50. EOS Utility owns the camera's USB
 * live view while tethered, so the app cannot have it; a cheap webcam beside the
 * lens gives guests something to pose against for about $25.
 *
 * Two details that matter more than they look:
 *
 * - The feed is mirrored. Guests expect a mirror, and an unmirrored preview feels
 *   subtly wrong to everyone who stands in front of it.
 * - The guide box shows the **strip slot's** crop, not the camera's frame. The
 *   R50 shoots 3:2 but the strip slots are 4:3, so the sides get cropped away.
 *   Anyone who fills the camera frame loses their shoulders on the strip.
 *
 * The guide is uncalibrated until M6, when the webcam and the R50 are finally in
 * the same room and their fields of view can be measured against each other.
 */
export function PosingMirror({ slotAspect = 4 / 3 }: { slotAspect?: number }) {
  const videoRef = useRef<HTMLVideoElement>(null)
  const streamRef = useRef<MediaStream | null>(null)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [attempt, setAttempt] = useState(0)

  const retry = useCallback(() => setAttempt((n) => n + 1), [])

  useEffect(() => {
    let cancelled = false

    const stop = () => {
      streamRef.current?.getTracks().forEach((t) => t.stop())
      streamRef.current = null
    }

    const start = async () => {
      stop()
      try {
        const stream = await open(selectedWebcamId())
        if (cancelled) {
          stream.getTracks().forEach((t) => t.stop())
          return
        }

        streamRef.current = stream
        if (videoRef.current) videoRef.current.srcObject = stream
        setProblem(null)
      } catch (e) {
        if (!cancelled) setProblem(describe(e))
      }
    }

    void start()
    return () => {
      cancelled = true
      stop()
    }
  }, [attempt])

  if (problem) {
    return (
      <div className="mirror mirror--error">
        <p className="mirror__headline">{problem.headline}</p>
        <ul className="mirror__hints">
          {problem.hints.map((h) => <li key={h}>{h}</li>)}
        </ul>
        <p className="muted small">{problem.raw}</p>
        <div className="controls">
          <button className="btn" onClick={retry}>Try again</button>
          {problem.offerReset && (
            <button
              className="btn"
              onClick={() => {
                setSelectedWebcamId(null)
                retry()
              }}
            >
              Use the default camera
            </button>
          )}
        </div>
      </div>
    )
  }

  return (
    <div className="mirror">
      <video ref={videoRef} autoPlay playsInline muted />
      <div className="mirror__guide" style={{ aspectRatio: String(slotAspect) }}>
        <span>strip crop &middot; uncalibrated</span>
      </div>
    </div>
  )
}

/**
 * Opens the chosen camera, falling back to any camera if that specific device is
 * gone. A stored device id can outlive the device it names -- a webcam unplugged,
 * or the same browser profile on another machine -- and that should not leave the
 * booth with no preview at all.
 */
async function open(deviceId: string | null): Promise<MediaStream> {
  const size = { width: 1280, height: 720 }

  if (!deviceId) {
    return navigator.mediaDevices.getUserMedia({ video: size, audio: false })
  }

  try {
    return await navigator.mediaDevices.getUserMedia({
      video: { deviceId: { exact: deviceId }, ...size },
      audio: false,
    })
  } catch (e) {
    if (e instanceof DOMException && e.name === 'OverconstrainedError') {
      return navigator.mediaDevices.getUserMedia({ video: size, audio: false })
    }

    throw e
  }
}

interface Problem {
  headline: string
  hints: string[]
  raw: string
  offerReset: boolean
}

/**
 * Turns a browser camera error into something actionable.
 *
 * "Could not start video source" wastes the most time of any of these: permission
 * was granted and the device exists, but something else already holds it -- very
 * often another tab of this same app, since the diagnostics page opens the camera
 * too in order to read device labels.
 */
function describe(e: unknown): Problem {
  const raw = e instanceof Error ? `${e.name}: ${e.message}` : String(e)
  const name = e instanceof DOMException ? e.name : ''

  if (name === 'NotReadableError' || /could not start video source/i.test(raw)) {
    return {
      headline: 'The camera is busy',
      hints: [
        'Close any other tab showing /display or /diagnostics — both open the camera.',
        'Quit Zoom, Teams, OBS or the Windows Camera app if any are running.',
        'Unplug and replug a USB webcam, then press Try again.',
      ],
      raw,
      offerReset: true,
    }
  }

  if (name === 'NotAllowedError' || name === 'SecurityError') {
    return {
      headline: 'Camera access was refused',
      hints: [
        'Click the camera icon in the address bar and allow this site.',
        'Windows: Settings → Privacy & security → Camera, and let apps use it.',
      ],
      raw,
      offerReset: false,
    }
  }

  if (name === 'NotFoundError' || name === 'OverconstrainedError') {
    return {
      headline: 'No camera found',
      hints: [
        'Plug the webcam in, then press Try again.',
        'A camera picked earlier may no longer be connected.',
      ],
      raw,
      offerReset: true,
    }
  }

  return {
    headline: 'Posing mirror unavailable',
    hints: ['Press Try again, or pick a different camera on /diagnostics.'],
    raw,
    offerReset: true,
  }
}
