import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'

// Self-hosted rather than fetched from a font CDN: a booth at a venue with no
// internet would otherwise fall back to system fonts mid-event.
import '@fontsource-variable/manrope'
import '@fontsource-variable/jetbrains-mono'

import { Diagnostics } from './Diagnostics'
import { Display } from './Display'
import { Operator } from './Operator'
import { Templates } from './Templates'
import './styles.css'

// Two windows, one bundle. No router: there are exactly two screens and they are
// opened directly as separate browser windows, so the path is enough.
const path = window.location.pathname.replace(/\/+$/, '')
const view =
  path === '/display' ? <Display />
  : path === '/diagnostics' ? <Diagnostics />
  : path === '/templates' ? <Templates />
  : <Operator />

createRoot(document.getElementById('root')!).render(<StrictMode>{view}</StrictMode>)
