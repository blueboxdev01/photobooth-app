import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { Display } from './Display'
import { Operator } from './Operator'
import './styles.css'

// Two windows, one bundle. No router: there are exactly two screens and they are
// opened directly as separate browser windows, so the path is enough.
const path = window.location.pathname.replace(/\/+$/, '')
const view = path === '/display' ? <Display /> : <Operator />

createRoot(document.getElementById('root')!).render(<StrictMode>{view}</StrictMode>)
