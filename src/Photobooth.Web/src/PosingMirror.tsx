import { useEffect, useRef, useState } from 'react'

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
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let stream: MediaStream | null = null
    let cancelled = false

    navigator.mediaDevices
      ?.getUserMedia({ video: { width: 1280, height: 720 }, audio: false })
      .then((s) => {
        if (cancelled) {
          s.getTracks().forEach((t) => t.stop())
          return
        }
        stream = s
        if (videoRef.current) videoRef.current.srcObject = s
      })
      .catch((e: unknown) =>
        setError(e instanceof Error ? e.message : 'Could not open the webcam.'),
      )

    return () => {
      cancelled = true
      stream?.getTracks().forEach((t) => t.stop())
    }
  }, [])

  if (error) {
    return (
      <div className="mirror mirror--error">
        <p>Posing mirror unavailable</p>
        <p className="muted small">{error}</p>
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
