import { useCallback, useEffect, useState } from 'react'

interface Preset {
  id: string
  label: string
  inches: string
  orientation: 'Portrait' | 'Landscape'
  width: number
  height: number
}

interface SettingsResponse {
  watchFolder: string
  outputFolder: string
  countdownSeconds: number
  noPhotoTimeoutSeconds: number
  settingsFile: string
  suggestions: string[]
  layout: {
    minPhotos: number
    maxPhotos: number
    photoCount: number
    supportedMin: number
    supportedMax: number
    canvasPresetId: string | null
    orientation: 'Portrait' | 'Landscape'
    canvas: { width: number; height: number; dpi: number }
    template: string
    presets: Preset[]
  }
  display: {
    backgroundColor: string
    backgroundImage: string | null
  }
}

interface FolderCheck {
  path: string
  ok: boolean
  error: string | null
  exists: boolean
  willCreate: boolean
  jpegCount: number
}

type Status = { ok: boolean; text: string } | null

/**
 * Booth setup.
 *
 * Two folders, and they must be different ones. The **watch folder** is wherever
 * EOS Utility happens to save — every guest's raw frames land there together,
 * which is what makes it useless for handing photos to a person. The **output
 * folder** is where a finished session is filed under its own subfolder, raws
 * and strip together.
 *
 * There is no folder picker: browsers will not hand a web page a filesystem
 * path. Paste it from EOS Utility's save-location setting, or Explorer's address
 * bar.
 */
export function Settings({ onChanged }: { onChanged?: () => void }) {
  const [data, setData] = useState<SettingsResponse | null>(null)
  const [watch, setWatch] = useState('')
  const [output, setOutput] = useState('')
  const [countdown, setCountdown] = useState(3)
  const [timeout, setTimeoutSeconds] = useState(20)
  const [minPhotos, setMinPhotos] = useState(2)
  const [maxPhotos, setMaxPhotos] = useState(6)
  const [photoCount, setPhotoCount] = useState(3)
  const [presetId, setPresetId] = useState('')
  const [colour, setColour] = useState('#14161A')
  const [check, setCheck] = useState<{ which: 'watch' | 'output'; result: FolderCheck } | null>(null)
  const [status, setStatus] = useState<Status>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    const r = await fetch('/api/settings')
    if (!r.ok) return
    const body: SettingsResponse = await r.json()
    setData(body)
    setWatch(body.watchFolder)
    setOutput(body.outputFolder)
    setCountdown(body.countdownSeconds)
    setTimeoutSeconds(body.noPhotoTimeoutSeconds)
    setMinPhotos(body.layout.minPhotos)
    setMaxPhotos(body.layout.maxPhotos)
    setPhotoCount(body.layout.photoCount)
    setPresetId(body.layout.canvasPresetId ?? '')
    setColour(body.display.backgroundColor)
  }, [])

  useEffect(() => { void load() }, [load])

  if (!data) return null

  const save = async (patch: Record<string, unknown>, success: string) => {
    setBusy(true)
    try {
      const r = await fetch('/api/settings', {
        method: 'PUT',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(patch),
      })
      const body = await r.json()
      if (!r.ok) {
        setStatus({ ok: false, text: body.error ?? `HTTP ${r.status}` })
        return false
      }

      setStatus({ ok: true, text: body.note ? `${success} ${body.note}` : success })
      setCheck(null)
      await load()
      onChanged?.()
      return true
    } finally {
      setBusy(false)
    }
  }

  const checkFolder = async (which: 'watch' | 'output') => {
    const folder = which === 'watch' ? watch : output
    setBusy(true)
    try {
      const r = await fetch('/api/settings/check-folder', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(which === 'watch'
          ? { watchFolder: folder }
          : { outputFolder: folder }),
      })
      setCheck(r.ok ? { which, result: await r.json() } : null)
    } finally {
      setBusy(false)
    }
  }

  const uploadBackground = async (file: File) => {
    const form = new FormData()
    form.append('background', file)
    const r = await fetch('/api/settings/display-background', { method: 'POST', body: form })
    const body = await r.json()
    if (!r.ok) {
      setStatus({ ok: false, text: body.error ?? 'Upload failed' })
      return
    }
    setStatus({ ok: true, text: 'Backdrop uploaded.' })
    await load()
    onChanged?.()
  }

  const checkNote = (which: 'watch' | 'output') => {
    if (check?.which !== which) return null
    const c = check.result
    return (
      <p className={c.ok ? 'muted small' : 'banner'}>
        <code>{c.path}</code>{' '}
        {c.ok
          ? c.willCreate
            ? '— usable; it will be created when you save'
            : `— usable, ${c.jpegCount} JPEG(s) already there`
          : `— ${c.error}`}
      </p>
    )
  }

  const { layout, display } = data
  const preset = layout.presets.find((p) => p.id === presetId)

  return (
    <section className="settings">
      <h2>Setup</h2>

      {status && <p className={status.ok ? 'muted' : 'banner'}>{status.text}</p>}

      <div className="settings__group">
        <h3>Folders</h3>

        <label className="settings__folder">
          Watch folder — where EOS Utility saves
          <input value={watch} spellCheck={false}
                 placeholder="C:\Users\you\Pictures\Tethered"
                 onChange={(e) => { setWatch(e.target.value); setCheck(null) }} />
        </label>
        <div className="controls">
          <button disabled={busy || !watch.trim()} onClick={() => void checkFolder('watch')}>
            Check
          </button>
          <button className="primary"
                  disabled={busy || watch.trim() === data.watchFolder}
                  onClick={() => void save({ watchFolder: watch.trim() }, 'Watch folder applied.')}>
            Save watch folder
          </button>
        </div>
        {checkNote('watch')}

        <label className="settings__folder">
          Output folder — finished sessions, one subfolder per guest
          <input value={output} spellCheck={false}
                 placeholder="C:\Users\you\Pictures\Photobooth"
                 onChange={(e) => { setOutput(e.target.value); setCheck(null) }} />
        </label>
        <div className="controls">
          <button disabled={busy || !output.trim()} onClick={() => void checkFolder('output')}>
            Check
          </button>
          <button className="primary"
                  disabled={busy || output.trim() === data.outputFolder}
                  onClick={() => void save({ outputFolder: output.trim() }, 'Output folder applied.')}>
            Save output folder
          </button>
        </div>
        {checkNote('output')}
        <p className="muted small">
          Each accepted session becomes its own folder here, holding the raw
          photos, the finished strip and a <code>session.json</code>. It must be a
          different folder from the watch folder.
        </p>

        {data.suggestions.length > 0 && (
          <p className="muted small">
            Try:{' '}
            {data.suggestions.map((s) => (
              <button key={s} className="linkish"
                      onClick={() => { setOutput(s); setCheck(null) }}>{s}</button>
            ))}
          </p>
        )}
      </div>

      <div className="settings__group">
        <h3>Strip layout</h3>

        <label>Output size
          <select value={presetId} onChange={(e) => setPresetId(e.target.value)}>
            {!layout.canvasPresetId && <option value="">Custom ({layout.canvas.width}×{layout.canvas.height})</option>}
            {layout.presets.map((p) => (
              <option key={p.id} value={p.id}>
                {p.label} — {p.inches} {p.orientation === 'Portrait' ? '↕' : '↔'}
              </option>
            ))}
          </select>
        </label>
        <p className="muted small">
          The shape decides the arrangement: a portrait size stacks photos into a
          strip, a landscape one runs them along a row and then into a grid.
          {preset && ` Currently ${preset.orientation.toLowerCase()}.`}
        </p>

        <div className="fields">
          <label>Photos per strip
            <input type="number" min={layout.minPhotos} max={layout.maxPhotos}
                   value={photoCount}
                   onChange={(e) => setPhotoCount(Number(e.target.value))} />
          </label>
          <label>Minimum
            <input type="number" min={layout.supportedMin} max={layout.supportedMax}
                   value={minPhotos}
                   onChange={(e) => setMinPhotos(Number(e.target.value))} />
          </label>
          <label>Maximum
            <input type="number" min={layout.supportedMin} max={layout.supportedMax}
                   value={maxPhotos}
                   onChange={(e) => setMaxPhotos(Number(e.target.value))} />
          </label>
        </div>
        <p className="muted small">
          The bounds are what this event allows ({layout.supportedMin}–
          {layout.supportedMax} is what the layout engine supports). The photo
          count is also the number of shots a session takes.
        </p>

        <div className="controls">
          <button className="primary" disabled={busy}
                  onClick={() => void save(
                    { minPhotos, maxPhotos, photoCount, canvasPresetId: presetId || undefined },
                    'Layout regenerated.')}>
            Apply layout
          </button>
          <a className="linkish" href="/templates">see it in the editor →</a>
        </div>
        <p className="muted small">
          Slots are placed evenly and can still be nudged in the{' '}
          <a href="/templates">template editor</a>. Changing the size or photo
          count re-lays them out, which detaches frame art drawn for the old shape.
        </p>
      </div>

      <div className="settings__group">
        <h3>Guest display</h3>

        <div className="fields">
          <label>Backdrop colour
            <input type="color" value={colour} onChange={(e) => setColour(e.target.value)} />
          </label>
          <label>Backdrop image
            <input type="file" accept="image/png,image/jpeg"
                   onChange={(e) => {
                     const f = e.target.files?.[0]
                     if (f) void uploadBackground(f)
                   }} />
          </label>
        </div>

        {display.backgroundImage && (
          <div className="settings__backdrop">
            <img src={`${display.backgroundImage}?v=${Date.now()}`} alt="Current backdrop" />
            <button onClick={() => void save(
              { clearDisplayBackgroundImage: true }, 'Backdrop image removed.')}>
              Remove image
            </button>
          </div>
        )}

        <div className="controls">
          <button className="primary"
                  disabled={busy || colour.toUpperCase() === display.backgroundColor.toUpperCase()}
                  onClick={() => void save(
                    { displayBackgroundColor: colour }, 'Backdrop colour applied.')}>
            Save colour
          </button>
        </div>
        <p className="muted small">
          Shown behind the guest screen, so the booth can match an event. The
          image is drawn over the colour and covers the screen.
        </p>
      </div>

      <div className="settings__group">
        <h3>Timings</h3>
        <div className="fields">
          <label>Countdown (s)
            <input type="number" min={0} max={30} value={countdown}
                   onChange={(e) => setCountdown(Number(e.target.value))} />
          </label>
          <label>No-photo timeout (s)
            <input type="number" min={5} max={300} value={timeout}
                   onChange={(e) => setTimeoutSeconds(Number(e.target.value))} />
          </label>
        </div>
        <div className="controls">
          <button className="primary"
                  disabled={busy || (countdown === data.countdownSeconds
                                     && timeout === data.noPhotoTimeoutSeconds)}
                  onClick={() => void save(
                    { countdownSeconds: countdown, noPhotoTimeoutSeconds: timeout },
                    'Timings applied.')}>
            Save timings
          </button>
        </div>
        <p className="muted small">
          Set the timeout from the press-to-file latency measured below — it is a
          guess until someone with a camera measures it.
        </p>
      </div>

      <p className="muted small">
        Saved to <code>{data.settingsFile}</code>, so all of this survives a
        restart. Folder changes apply immediately; no restart needed.
      </p>
    </section>
  )
}
