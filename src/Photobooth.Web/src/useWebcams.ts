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
    const cameras = async () =>
      (await navigator.mediaDevices.enumerateDevices())
        .filter((d) => d.kind === 'videoinput')

    try {
      let found = await cameras()

      // Only grab a stream if labels are still hidden, which means permission has
      // never been granted. Opening the camera unconditionally here would fight
      // /display for the device on machines whose webcam allows one reader --
      // and produce exactly the "could not start video source" it is meant to
      // help diagnose.
      if (found.length > 0 && found.every((d) => d.label === '')) {
        const stream = await navigator.mediaDevices.getUserMedia({ video: true })
        stream.getTracks().forEach((t) => t.stop())
        found = await cameras()
      }

      setDevices(found)
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Camera access was refused.')
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
