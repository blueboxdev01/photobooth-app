import { useEffect, useRef, useState } from 'react'
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import type { HubConnection } from '@microsoft/signalr'
import type { CameraInfo, SessionSnapshot } from './types'

/**
 * Subscribes both windows to one authoritative session state.
 *
 * The server pushes a full snapshot on every transition rather than deltas: a
 * session has few state changes and they are small, so replacing the whole thing
 * removes any chance of the two screens drifting apart.
 */
export function useSession() {
  const [snapshot, setSnapshot] = useState<SessionSnapshot | null>(null)
  const [camera, setCamera] = useState<CameraInfo | null>(null)
  const [connected, setConnected] = useState(false)
  const connectionRef = useRef<HubConnection | null>(null)

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl('/hub/session')
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    connection.on('state', (s: SessionSnapshot) => setSnapshot(s))
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

  return { snapshot, camera, connected }
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

/** Seconds left until an absolute deadline, ticking locally. */
export function useCountdown(deadlineUtc: string | null) {
  const [remaining, setRemaining] = useState<number | null>(null)

  useEffect(() => {
    if (!deadlineUtc) {
      setRemaining(null)
      return
    }

    const end = new Date(deadlineUtc).getTime()
    const tick = () => setRemaining(Math.max(0, (end - Date.now()) / 1000))
    tick()
    const id = setInterval(tick, 100)
    return () => clearInterval(id)
  }, [deadlineUtc])

  return remaining
}
