import { useCallback, useEffect, useState } from 'react'

const STORAGE_KEY = 'photobooth.webcamDeviceId'

/** The webcam chosen as the posing mirror, remembered per machine. */
export function selectedWebcamId(): string | null {
  try {
    return localStorage.getItem(STORAGE_KEY)
  } catch {
    return null
  }
}

export function setSelectedWebcamId(id: string | null) {
  try {
    if (id) localStorage.setItem(STORAGE_KEY, id)
    else localStorage.removeItem(STORAGE_KEY)
  } catch {
    /* private browsing; the default device still works */
  }
}

/**
 * Lists cameras the browser can see.
 *
 * Note this covers only the *posing mirror*. The R50 is not here and never will
 * be: it reaches the app as JPEGs in a watch folder, not as a video device.
 * Two cameras, two entirely separate paths.
 *
 * Device labels are hidden until the user has granted camera access at least
 * once, so this asks for a stream first and stops it immediately -- otherwise
 * the picker shows "camera 1 / camera 2" and is useless for choosing.
 */
export function useWebcams() {
  const [devices, setDevices] = useState<MediaDeviceInfo[]>([])
  const [selected, setSelected] = useState<string | null>(selectedWebcamId())
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ video: true })
      stream.getTracks().forEach((t) => t.stop())
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Camera access was refused.')
    }

    try {
      const all = await navigator.mediaDevices.enumerateDevices()
      setDevices(all.filter((d) => d.kind === 'videoinput'))
    } catch {
      setDevices([])
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const choose = useCallback((id: string | null) => {
    setSelectedWebcamId(id)
    setSelected(id)
  }, [])

  return { devices, selected, choose, error, refresh }
}
