import { useEffect, useState } from 'react'

export interface DisplayTheme {
  backgroundColor: string
  backgroundImage: string | null
}

/**
 * The guest screen's backdrop, so a booth can match an event.
 *
 * Polled rather than pushed: it changes only while someone is setting the booth
 * up, and polling means the guest screen picks up a new backdrop without anyone
 * walking over to reload it.
 */
export function useDisplayTheme(): DisplayTheme {
  const [theme, setTheme] = useState<DisplayTheme>({
    backgroundColor: '#14161A',
    backgroundImage: null,
  })

  useEffect(() => {
    let cancelled = false

    const load = async () => {
      try {
        const r = await fetch('/api/settings')
        if (!r.ok) return
        const body = await r.json()
        if (!cancelled && body.display) setTheme(body.display)
      } catch {
        /* server restarting */
      }
    }

    void load()
    const id = setInterval(load, 5000)
    return () => {
      cancelled = true
      clearInterval(id)
    }
  }, [])

  return theme
}

/** Inline style for the guest stage: colour, with any backdrop image over it. */
export function backdropStyle(theme: DisplayTheme): React.CSSProperties {
  return {
    backgroundColor: theme.backgroundColor,
    ...(theme.backgroundImage
      ? {
          backgroundImage: `url(${theme.backgroundImage})`,
          backgroundSize: 'cover',
          backgroundPosition: 'center',
        }
      : {}),
  }
}
