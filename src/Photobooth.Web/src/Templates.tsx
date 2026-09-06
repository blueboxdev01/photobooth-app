import { useCallback, useEffect, useRef, useState } from 'react'
import { AppShell } from './AppShell'

type Fit = 'Cover' | 'Contain'

interface Slot { x: number; y: number; w: number; h: number; fit: Fit }
interface Canvas { width: number; height: number; dpi: number }
interface Template {
  name: string
  canvas: Canvas
  slots: Slot[]
  overlay: string | null
  background: string
}

interface TemplatesResponse {
  selected: string
  source: string
  usingBuiltInFallback: boolean
  folder: string
  available: string[]
  current: Template
}

/** Editor viewport height. Slots are normalised, so this is presentation only. */
const VIEW_HEIGHT = 560

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v))

/**
 * Visual slot editor.
 *
 * Slots are stored as fractions of the canvas, which is what makes this a
 * drag-rectangle over a scaled preview rather than something that has to
 * recompute pixels whenever the output size changes.
 *
 * The editor's own rendering is an approximation -- CSS object-fit stands in for
 * the compositor's cover crop. **Render preview** is the authority: it posts the
 * template to the server and gets back a strip built by the same code a real
 * session uses.
 */
export function Templates() {
  const [meta, setMeta] = useState<TemplatesResponse | null>(null)
  const [name, setName] = useState('')
  const [draft, setDraft] = useState<Template | null>(null)
  const [samples, setSamples] = useState<string[]>([])
  const [selectedSlot, setSelectedSlot] = useState(0)
  const [overlayOpacity, setOverlayOpacity] = useState(1)
  const [overlayVersion, setOverlayVersion] = useState(0)
  const [preview, setPreview] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [status, setStatus] = useState<{ ok: boolean; text: string } | null>(null)

  const stageRef = useRef<HTMLDivElement>(null)

  const load = useCallback(async (which?: string) => {
    const r = await fetch('/api/templates')
    const body: TemplatesResponse = await r.json()
    setMeta(body)

    const target = which ?? body.available.find((a) => a) ?? ''
    setName(target)

    if (target) {
      const t = await fetch(`/api/templates/${target}`)
      if (t.ok) {
        setDraft(await t.json())
        return
      }
    }
    setDraft(body.current)
  }, [])

  useEffect(() => {
    void load()
    void fetch('/api/samples').then((r) => r.json()).then(setSamples).catch(() => setSamples([]))
  }, [load])

  if (!meta || !draft) {
    return (
      <AppShell page="/templates">
        <p className="muted">Loading…</p>
      </AppShell>
    )
  }

  const aspect = draft.canvas.width / draft.canvas.height
  const viewWidth = VIEW_HEIGHT * aspect

  const update = (patch: Partial<Template>) => setDraft({ ...draft, ...patch })
  const updateSlot = (i: number, patch: Partial<Slot>) =>
    setDraft({ ...draft, slots: draft.slots.map((s, j) => (j === i ? { ...s, ...patch } : s)) })

  const addSlot = () => {
    const n = draft.slots.length
    setDraft({
      ...draft,
      slots: [...draft.slots, { x: 0.1, y: clamp(0.05 + n * 0.05, 0, 0.8), w: 0.8, h: 0.2, fit: 'Cover' }],
    })
    setSelectedSlot(n)
  }

  const removeSlot = (i: number) => {
    if (draft.slots.length <= 1) {
      setStatus({ ok: false, text: 'A template needs at least one slot.' })
      return
    }
    setDraft({ ...draft, slots: draft.slots.filter((_, j) => j !== i) })
    setSelectedSlot(0)
  }

  /** Drag a slot, or its bottom-right handle, in normalised space. */
  const startDrag = (i: number, mode: 'move' | 'resize') => (e: React.PointerEvent) => {
    e.preventDefault()
    e.stopPropagation()
    setSelectedSlot(i)

    const stage = stageRef.current
    if (!stage) return
    const rect = stage.getBoundingClientRect()
    const start = draft.slots[i]
    const originX = e.clientX
    const originY = e.clientY

    const onMove = (ev: PointerEvent) => {
      const dx = (ev.clientX - originX) / rect.width
      const dy = (ev.clientY - originY) / rect.height

      if (mode === 'move') {
        updateSlot(i, {
          x: clamp(start.x + dx, 0, 1 - start.w),
          y: clamp(start.y + dy, 0, 1 - start.h),
        })
      } else {
        updateSlot(i, {
          w: clamp(start.w + dx, 0.02, 1 - start.x),
          h: clamp(start.h + dy, 0.02, 1 - start.y),
        })
      }
    }

    const onUp = () => {
      window.removeEventListener('pointermove', onMove)
      window.removeEventListener('pointerup', onUp)
    }

    window.addEventListener('pointermove', onMove)
    window.addEventListener('pointerup', onUp)
  }

  const save = async () => {
    setBusy(true)
    try {
      const r = await fetch(`/api/templates/${name}`, {
        method: 'PUT',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(draft),
      })
      const body = await r.json()
      setStatus(r.ok
        ? { ok: true, text: `Saved. Sessions will now take ${draft.slots.length} photos.` }
        : { ok: false, text: body.error ?? `HTTP ${r.status}` })
      if (r.ok) await load(name)
    } finally {
      setBusy(false)
    }
  }

  const renderPreview = async () => {
    setBusy(true)
    try {
      const r = await fetch('/api/templates/preview', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(draft),
      })
      if (!r.ok) {
        setStatus({ ok: false, text: (await r.json()).error ?? 'Preview failed' })
        return
      }
      if (preview) URL.revokeObjectURL(preview)
      setPreview(URL.createObjectURL(await r.blob()))
      setStatus(null)
    } finally {
      setBusy(false)
    }
  }

  const uploadOverlay = async (file: File) => {
    const form = new FormData()
    form.append('overlay', file)
    const r = await fetch(`/api/templates/${name}/overlay`, { method: 'POST', body: form })
    const body = await r.json()
    if (!r.ok) {
      setStatus({ ok: false, text: body.error ?? 'Upload failed' })
      return
    }
    update({ overlay: body.overlay })
    setOverlayVersion((v) => v + 1)
    setStatus({ ok: true, text: `Frame uploaded. Save to keep it.` })
  }

  const selectTemplate = async (which: string) => {
    await fetch(`/api/templates/${which}/select`, { method: 'POST' })
    await load(which)
    setStatus({ ok: true, text: `${which} is now in use.` })
  }

  return (
    <AppShell page="/templates">
      <div className="templates">
        <p className="controls">
          <span className="pill">{draft.slots.length} slots</span>
          {meta.usingBuiltInFallback && (
            <span className="pill pill--Faulted">built-in fallback</span>
          )}
        </p>

      {status && <p className={status.ok ? 'muted' : 'banner'}>{status.text}</p>}

      <div className="templates__layout">
        <div className="templates__stage">
          <div
            ref={stageRef}
            className="stagebox"
            style={{
              width: viewWidth,
              height: VIEW_HEIGHT,
              background: draft.background,
            }}
            onPointerDown={() => setSelectedSlot(-1)}
          >
            {draft.slots.map((slot, i) => (
              <div
                key={i}
                className={`slot ${i === selectedSlot ? 'slot--active' : ''}`}
                style={{
                  left: `${slot.x * 100}%`,
                  top: `${slot.y * 100}%`,
                  width: `${slot.w * 100}%`,
                  height: `${slot.h * 100}%`,
                }}
                onPointerDown={startDrag(i, 'move')}
              >
                {samples.length > 0 && (
                  <img
                    src={samples[i % samples.length]}
                    alt=""
                    style={{ objectFit: slot.fit === 'Cover' ? 'cover' : 'contain' }}
                    draggable={false}
                  />
                )}
                <span className="slot__index">{i + 1}</span>
                <span className="slot__handle" onPointerDown={startDrag(i, 'resize')} />
              </div>
            ))}

            {draft.overlay && (
              <img
                className="stagebox__overlay"
                style={{ opacity: overlayOpacity }}
                src={`/api/templates/${name}/overlay?v=${overlayVersion}`}
                alt=""
                draggable={false}
              />
            )}
          </div>

          <p className="muted small">
            Drag a slot to move it, or its corner to resize. This view approximates
            the crop — <strong>Render preview</strong> uses the real compositor.
          </p>
        </div>

        <div className="templates__panel">
          <section>
            <h2>Template</h2>
            <div className="controls">
              <select value={name} onChange={(e) => void load(e.target.value)}>
                {meta.available.map((a) => <option key={a} value={a}>{a}</option>)}
                {!meta.available.includes(name) && <option value={name}>{name}</option>}
              </select>
              <button onClick={() => void selectTemplate(name)}>Use for sessions</button>
            </div>
            <label>Save as
              <input value={name} onChange={(e) => setName(e.target.value.trim())}
                     placeholder="my-frame" />
            </label>
            <p className="muted small">
              Letters, digits, dashes and underscores. Saving under a new name
              creates a copy.
            </p>
          </section>

          <section>
            <h2>Canvas</h2>
            <div className="fields">
              <label>Width
                <input type="number" value={draft.canvas.width}
                       onChange={(e) => update({
                         canvas: { ...draft.canvas, width: Number(e.target.value) },
                       })} />
              </label>
              <label>Height
                <input type="number" value={draft.canvas.height}
                       onChange={(e) => update({
                         canvas: { ...draft.canvas, height: Number(e.target.value) },
                       })} />
              </label>
              <label>DPI
                <input type="number" value={draft.canvas.dpi}
                       onChange={(e) => update({
                         canvas: { ...draft.canvas, dpi: Number(e.target.value) },
                       })} />
              </label>
            </div>
            <p className="muted small">
              {(draft.canvas.width / draft.canvas.dpi).toFixed(2)} ×{' '}
              {(draft.canvas.height / draft.canvas.dpi).toFixed(2)} inches printed.
            </p>
            <label>Background
              <input type="color" value={draft.background}
                     onChange={(e) => update({ background: e.target.value.toUpperCase() })} />
            </label>
          </section>

          <section>
            <h2>Frame art</h2>
            <input type="file" accept="image/png"
                   onChange={(e) => {
                     const f = e.target.files?.[0]
                     if (f) void uploadOverlay(f)
                   }} />
            <p className="muted small">
              PNG, drawn on top. Leave the photo areas transparent so the photos
              show through.
            </p>
            {draft.overlay && (
              <label>Show at
                <input type="range" min={0} max={1} step={0.05} value={overlayOpacity}
                       onChange={(e) => setOverlayOpacity(Number(e.target.value))} />
              </label>
            )}
          </section>

          <section>
            <h2>Slots — {draft.slots.length}</h2>
            <p className="muted small">
              The slot count is the number of photos a session takes.
            </p>
            <ol className="slotlist">
              {draft.slots.map((slot, i) => (
                <li key={i} className={i === selectedSlot ? 'active' : ''}
                    onClick={() => setSelectedSlot(i)}>
                  <span>#{i + 1}</span>
                  <code>
                    {(slot.w * draft.canvas.width).toFixed(0)}×
                    {(slot.h * draft.canvas.height).toFixed(0)}px
                  </code>
                  <select value={slot.fit}
                          onChange={(e) => updateSlot(i, { fit: e.target.value as Fit })}>
                    <option value="Cover">Cover</option>
                    <option value="Contain">Contain</option>
                  </select>
                  <button onClick={() => removeSlot(i)}>✕</button>
                </li>
              ))}
            </ol>
            <button onClick={addSlot}>Add slot</button>
          </section>

          <section className="controls">
            <button className="primary" disabled={busy || !name} onClick={() => void save()}>
              Save template
            </button>
            <button disabled={busy} onClick={() => void renderPreview()}>
              Render preview
            </button>
          </section>
        </div>

        {preview && (
          <div className="templates__preview">
            <h2>Real render</h2>
            <img className="strip" src={preview} alt="Rendered strip" />
            <p className="muted small">Built by the compositor a session uses.</p>
          </div>
        )}
      </div>
      </div>
    </AppShell>
  )
}
