import { useCallback, useEffect, useState } from 'react'

export type ThemeChoice = 'light' | 'dark' | 'system'

const STORAGE_KEY = 'photobooth.theme'

function stored(): ThemeChoice {
  try {
    const value = localStorage.getItem(STORAGE_KEY)
    return value === 'light' || value === 'dark' ? value : 'system'
  } catch {
    return 'system'
  }
}

/**
 * Light and dark for the operator console.
 *
 * A booth is set up in daylight and run in a dark room, so this is not
 * decoration — glare at 2pm and glare at 10pm are different problems. Defaults
 * to following the machine, because that is usually already set correctly, and
 * remembers an explicit choice.
 *
 * The guest display deliberately does not follow this: it has its own event
 * backdrop and is never looked at by the operator.
 */
export function useTheme() {
  const [choice, setChoice] = useState<ThemeChoice>(stored)
  const [systemDark, setSystemDark] = useState(
    () => window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? true,
  )

  useEffect(() => {
    const query = window.matchMedia?.('(prefers-color-scheme: dark)')
    if (!query) return
    const onChange = (e: MediaQueryListEvent) => setSystemDark(e.matches)
    query.addEventListener('change', onChange)
    return () => query.removeEventListener('change', onChange)
  }, [])

  const resolved: 'light' | 'dark' =
    choice === 'system' ? (systemDark ? 'dark' : 'light') : choice

  useEffect(() => {
    document.documentElement.dataset.theme = resolved
    // Keeps form controls and scrollbars in step with the rest of the page.
    document.documentElement.style.colorScheme = resolved
  }, [resolved])

  const set = useCallback((next: ThemeChoice) => {
    setChoice(next)
    try {
      if (next === 'system') localStorage.removeItem(STORAGE_KEY)
      else localStorage.setItem(STORAGE_KEY, next)
    } catch {
      /* private browsing; the choice just will not persist */
    }
  }, [])

  const toggle = useCallback(
    () => set(resolved === 'dark' ? 'light' : 'dark'),
    [resolved, set],
  )

  return { choice, resolved, set, toggle }
}
