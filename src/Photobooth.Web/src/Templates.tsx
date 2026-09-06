import { useCallback, useEffect, useRef, useState } from 'react'
import { AppShell } from './AppShell'

type Fit = 'Cover' | 'Contain'
type ArtLayer = 'InFront' | 'Behind'

interface Slot { x: number; y: number; w: number; h: number; fit: Fit }
interface Canvas { width: number; height: number; dpi: number }
interface Template {
  name: string
  canvas: Canvas
  slots: Slot[]
  overlay: string | null
  background: string
  art: ArtLayer
}

interface TemplatesResponse {
  selected: string
  source: string
  usingBuiltInFallback: boolean
  folder: string
  available: string[]
  current: Template
}

/** Tallest the editor canvas gets. Slots are normalised, so this is presentation only. */
const MAX_STAGE_HEIGHT = '62vh'

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

  const selected = draft.slots[selectedSlot]

  /** Normalised fraction -> whole pixels, for display. */
  const px = (value: number, axis: 'w' | 'h') =>
    Math.round(value * (axis === 'w' ? draft.canvas.width : draft.canvas.height))

  /** Pixels typed by hand -> the normalised fraction actually stored. */
  const setPx = (i: number, key: 'x' | 'y' | 'w' | 'h', raw: string) => {
    const pixels = Number(raw)
    if (!Number.isFinite(pixels)) return
    const axis = key === 'x' || key === 'w' ? draft.canvas.width : draft.canvas.height
    updateSlot(i, { [key]: clamp(pixels / axis, 0, 1) } as Partial<Slot>)
  }

  /** Uniform photo sizes, without disturbing where each one sits. */
  const matchAllToSelected = () => {
    if (!selected) return
    setDraft({
      ...draft,
      slots: draft.slots.map((s) => ({ ...s, w: selected.w, h: selected.h })),
    })
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

    // The server decides the layer by looking at the image; the editor follows it
    // so the preview and the rendered strip cannot disagree.
    update({ overlay: body.overlay, art: body.layer as ArtLayer })
    setOverlayVersion((v) => v + 1)
    setStatus({ ok: true, text: `${body.note} Save to keep it.` })
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
              // Whichever is smaller: the column, or what the height cap allows.
              // Keeps a tall 2x6 and a wide 6x4 both fitting without the panel
              // being pushed below the canvas.
              width: `min(100%, calc(${MAX_STAGE_HEIGHT} * ${aspect}))`,
              aspectRatio: `${draft.canvas.width} / ${draft.canvas.height}`,
              background: draft.background,
            }}
            onPointerDown={() => setSelectedSlot(-1)}
          >
            {/* A backdrop is painted first so the photos sit on top of it, exactly
                as the compositor does. */}
            {draft.overlay && draft.art === 'Behind' && (
              <img
                className="stagebox__art stagebox__art--behind"
                style={{ opacity: overlayOpacity }}
                src={`/api/templates/${name}/overlay?v=${overlayVersion}`}
                alt=""
                draggable={false}
              />
            )}
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

            {draft.overlay && draft.art === 'InFront' && (
              <img
                className="stagebox__art stagebox__art--front"
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
              <select className="control" value={name} onChange={(e) => void load(e.target.value)}>
                {meta.available.map((a) => <option key={a} value={a}>{a}</option>)}
                {!meta.available.includes(name) && <option value={name}>{name}</option>}
              </select>
              <button className="btn" onClick={() => void selectTemplate(name)}>Use for sessions</button>
            </div>
            <label>Save as
              <input className="control" value={name}
                     onChange={(e) => setName(e.target.value.trim())}
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
                <input className="control" type="number" value={draft.canvas.width}
                       onChange={(e) => update({
                         canvas: { ...draft.canvas, width: Number(e.target.value) },
                       })} />
              </label>
              <label>Height
                <input className="control" type="number" value={draft.canvas.height}
                       onChange={(e) => update({
                         canvas: { ...draft.canvas, height: Number(e.target.value) },
                       })} />
              </label>
              <label>DPI
                <input className="control" type="number" value={draft.canvas.dpi}
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
              <input className="control control--color" type="color" value={draft.background}
                     onChange={(e) => update({ background: e.target.value.toUpperCase() })} />
            </label>
          </section>

          <section>
            <h2>Template art</h2>
            <label className="btn btn--block filebtn">
              Choose image…
              <input type="file" accept="image/png,image/jpeg"
                     onChange={(e) => {
                       const f = e.target.files?.[0]
                       if (f) void uploadOverlay(f)
                     }} />
            </label>
            <p className="muted small">
              Upload at <strong>{draft.canvas.width}&times;{draft.canvas.height}px</strong> for
              this size. Anything else is scaled to fill and centre-cropped, so the
              edges may be trimmed.
            </p>
            <p className="muted small">
              A <strong>backdrop</strong> can be PNG or JPEG; the photos are drawn
              on top of it. A <strong>frame</strong> must be a PNG that is
              transparent where the photos go, and is drawn over them.
            </p>

            {draft.overlay && (
              <p className="muted small">
                Using it as a{' '}
                <strong>{draft.art === 'InFront' ? 'frame' : 'backdrop'}</strong>.{' '}
                <button className="linkish"
                        onClick={() => update({
                          art: draft.art === 'InFront' ? 'Behind' : 'InFront',
                        })}>
                  use as a {draft.art === 'InFront' ? 'backdrop' : 'frame'} instead
                </button>
              </p>
            )}
            {draft.overlay && (
              <label>Art opacity, for positioning slots underneath it
                <input className="control control--range" type="range"
                       min={0} max={1} step={0.05} value={overlayOpacity}
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
                  <span className="slotlist__no">{i + 1}</span>
                  <code>
                    {Math.round(slot.w * draft.canvas.width)}×
                    {Math.round(slot.h * draft.canvas.height)}
                  </code>
                  <select className="control control--sm" value={slot.fit}
                          onChange={(e) => updateSlot(i, { fit: e.target.value as Fit })}>
                    <option value="Cover">Cover</option>
                    <option value="Contain">Contain</option>
                  </select>
                  <button className="btn btn--icon" title="Remove this slot"
                          onClick={() => removeSlot(i)}>✕</button>
                </li>
              ))}
            </ol>
            <button className="btn btn--block" onClick={addSlot}>Add slot</button>
          </section>

          {selected && (
            <section>
              <h2>Slot {selectedSlot + 1} position</h2>
              <p className="muted small">
                Typed in pixels, for sizes a drag cannot hit exactly.
              </p>
              <div className="grid2">
                <label>X
                  <input className="control" type="number" value={px(selected.x, 'w')}
                         onChange={(e) => setPx(selectedSlot, 'x', e.target.value)} />
                </label>
                <label>Y
                  <input className="control" type="number" value={px(selected.y, 'h')}
                         onChange={(e) => setPx(selectedSlot, 'y', e.target.value)} />
                </label>
                <label>Width
                  <input className="control" type="number" value={px(selected.w, 'w')}
                         onChange={(e) => setPx(selectedSlot, 'w', e.target.value)} />
                </label>
                <label>Height
                  <input className="control" type="number" value={px(selected.h, 'h')}
                         onChange={(e) => setPx(selectedSlot, 'h', e.target.value)} />
                </label>
              </div>
              <button className="btn btn--block" disabled={draft.slots.length < 2}
                      onClick={matchAllToSelected}>
                Make every slot this size
              </button>
              <p className="muted small">
                Photos at different sizes on one strip read as a mistake, so this
                copies the width and height to every other slot without moving them.
              </p>
            </section>
          )}

          <section className="controls">
            <button className="btn btn--primary" disabled={busy || !name}
                    onClick={() => void save()}>
              Save template
            </button>
            <button className="btn" disabled={busy} onClick={() => void renderPreview()}>
              Render preview
            </button>
          </section>
        </div>

        <div className="templates__preview">
          <h2>Real render</h2>
          {preview
            ? <img className="strip" src={preview} alt="Rendered strip" />
            : (
              <p className="placeholder">
                Press <strong>Render preview</strong> to build this template through
                the same compositor a session uses.
              </p>
            )}
          {preview && <p className="muted small">Built by the compositor a session uses.</p>}
        </div>
      </div>
      </div>
    </AppShell>
  )
}
