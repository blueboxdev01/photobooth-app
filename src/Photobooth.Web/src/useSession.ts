import { useEffect, useRef, useState } from 'react'
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import type { HubConnection } from '@microsoft/signalr'
import type { CameraInfo, DeliveryUpdate, SessionSnapshot } from './types'

/**
 * Subscribes both windows to one authoritative session state.
 *
 * The server pushes a full snapshot on every transition rather than deltas: a
 * session has few state changes and they are small, so replacing the whole thing
 * removes any chance of the two screens drifting apart.
 */
export function useSession() {
  const [snapshot, setSnapshot] = useState<SessionSnapshot | null>(null)
  const [delivery, setDelivery] = useState<DeliveryUpdate | null>(null)
  const [camera, setCamera] = useState<CameraInfo | null>(null)
  // Where finished sessions land, so the console can say where rather than
  // naming a folder the operator then has to go hunting for.
  const [outputFolder, setOutputFolder] = useState<string | null>(null)
  // The shape of one photo on the strip, for the guest screen's framing guide.
  // Polled with the rest of the state: it only changes when the template does.
  const [slotAspect, setSlotAspect] = useState(4 / 3)
  const [connected, setConnected] = useState(false)
  const connectionRef = useRef<HubConnection | null>(null)

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl('/hub/session')
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    connection.on('state', (s: SessionSnapshot) => setSnapshot(s))
    connection.on('delivery', (d: DeliveryUpdate) => setDelivery(d))
    connection.onreconnected(() => setConnected(true))
    connection.onreconnecting(() => setConnected(false))
    connection.onclose(() => setConnected(false))

    connection.start().then(
      () => setConnected(true),
      () => setConnected(false),
    )
    connectionRef.current = connection

    return () => {
      connectionRef.current = null
      if (connection.state !== HubConnectionState.Disconnected) {
        void connection.stop()
      }
    }
  }, [])

  // Camera health is not part of session state and changes rarely, so it is
  // polled instead of pushed.
  useEffect(() => {
    let cancelled = false
    const load = async () => {
      try {
        const r = await fetch('/api/state')
        if (!r.ok) return
        const body = await r.json()
        if (!cancelled) {
          setCamera(body.camera)
          // Delivery is polled as well as pushed: an upload can settle while no
          // browser is connected, and the pending count on the console would
          // otherwise sit stale until the next session.
          setDelivery(body.delivery)
          if (typeof body.slotAspect === 'number' && body.slotAspect > 0) {
            setSlotAspect(body.slotAspect)
          }
          if (typeof body.outputFolder === 'string') setOutputFolder(body.outputFolder)
          if (!connectionRef.current) setSnapshot(body.session)
        }
      } catch {
        /* server restarting */
      }
    }
    void load()
    const id = setInterval(load, 3000)
    return () => {
      cancelled = true
      clearInterval(id)
    }
  }, [])

  return { snapshot, delivery, camera, connected, slotAspect, outputFolder }
}

export async function command(name: string, body?: unknown) {
  await fetch(`/api/session/${name}`, {
    method: 'POST',
    headers: body ? { 'content-type': 'application/json' } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  })
}

/**
 * Rearrange the shots. `positions` is expressed in the order currently on screen
 * -- entry i is the position that should move into slot i -- which is exactly
 * what a drag produces without the console needing to track capture order.
 *
 * Returns the server's reason on refusal, or null. The new state arrives over the
 * hub like every other transition, so there is nothing to apply here.
 */
export async function reorder(positions: number[]): Promise<string | null> {
  try {
    const r = await fetch('/api/session/order', {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ order: positions }),
    })
    if (r.ok) return null
    const body = await r.json()
    return body.error ?? `HTTP ${r.status}`
  } catch (e) {
    return e instanceof Error ? e.message : 'Request failed'
  }
}

/**
 * Seconds left, counted on the booth’s clock rather than this screen’s.
 *
 * The deadline is an absolute instant decided by the booth. Subtracting the
 * local Date.now() from it means every screen counts on its own clock -- fine
 * on the operator console, which runs on the booth itself, and wrong on any
 * second screen whose clock is its own business. One three seconds adrift
 * showed the operator three seconds and the guest six.
 *
 * So each snapshot carries what the booth’s clock said when it was built, and
 * the difference against ours is applied to the deadline. Recomputed on every
 * snapshot, so it follows a clock that drifts or gets corrected mid-event.
 */
export function useCountdown(deadlineUtc: string | null, serverNowUtc?: string | null) {
  const [remaining, setRemaining] = useState<number | null>(null)

  useEffect(() => {
    if (!deadlineUtc) {
      setRemaining(null)
      return
    }

    // Measured when the snapshot arrives. The wire time between the booth
    // stamping it and this running is a few milliseconds on a local network,
    // which is far below anything a guest can perceive in a countdown.
    const skew = serverNowUtc ? new Date(serverNowUtc).getTime() - Date.now() : 0

    const end = new Date(deadlineUtc).getTime()
    const tick = () => setRemaining(Math.max(0, (end - (Date.now() + skew)) / 1000))
    tick()
    const id = setInterval(tick, 100)
    return () => clearInterval(id)
  }, [deadlineUtc, serverNowUtc])

  return remaining
}
