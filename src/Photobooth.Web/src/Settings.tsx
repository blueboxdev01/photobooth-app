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
  delivery: {
    /** A Google OAuth client exists in this build at all. */
    configured: boolean
    account: string | null
    status: {
      enabled: boolean
      authorised: boolean
      pending: number
      failed: number
      lastError: string | null
      lastSuccessUtc: string | null
    }
  }
}

interface FailedSession {
  folderName: string
  createdUtc: string
  uploadState: string
  uploadError: string | null
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
  const [stuck, setStuck] = useState<FailedSession[]>([])
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

    const sessions = await fetch('/api/sessions')
    if (sessions.ok) {
      const all = await sessions.json()
      setStuck(
        (all.sessions as FailedSession[]).filter((x) => x.uploadState === 'Failed'))
    }
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
          <button className="btn" disabled={busy || !watch.trim()} onClick={() => void checkFolder('watch')}>
            Check
          </button>
          <button className="btn btn--primary"
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
          <button className="btn" disabled={busy || !output.trim()} onClick={() => void checkFolder('output')}>
            Check
          </button>
          <button className="btn btn--primary"
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
          <button className="btn btn--primary" disabled={busy}
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
            <span className="btn filebtn">
              Choose image…
              <input type="file" accept="image/png,image/jpeg"
                     onChange={(e) => {
                       const f = e.target.files?.[0]
                       if (f) void uploadBackground(f)
                     }} />
            </span>
          </label>
        </div>

        {display.backgroundImage && (
          <div className="settings__backdrop">
            <img src={`${display.backgroundImage}?v=${Date.now()}`} alt="Current backdrop" />
            <button className="btn" onClick={() => void save(
              { clearDisplayBackgroundImage: true }, 'Backdrop image removed.')}>
              Remove image
            </button>
          </div>
        )}

        <div className="controls">
          <button className="btn btn--primary"
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

      <Delivery data={data} busy={busy} setBusy={setBusy} setStatus={setStatus}
                stuck={stuck} reload={load} save={save} />

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
          <button className="btn btn--primary"
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

/**
 * Google Drive delivery.
 *
 * Three things an operator has to be able to see at a glance, because each one
 * silently stops guests getting their photos: whether uploading is on, whether
 * the booth is still signed in, and whether anything has given up.
 */
function Delivery({
  data,
  busy,
  setBusy,
  setStatus,
  stuck,
  reload,
  save,
}: {
  data: SettingsResponse
  busy: boolean
  setBusy: (b: boolean) => void
  setStatus: (s: Status) => void
  stuck: FailedSession[]
  reload: () => Promise<void>
  save: (patch: Record<string, unknown>, success: string) => Promise<boolean>
}) {
  const { configured, account, status } = data.delivery

  const post = async (url: string, success: string) => {
    setBusy(true)
    try {
      const r = await fetch(url, { method: 'POST' })
      const body = await r.json()
      if (!r.ok) {
        setStatus({ ok: false, text: body.error ?? `HTTP ${r.status}` })
        return
      }
      setStatus({ ok: true, text: success })
      await reload()
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="settings__group">
      <h3>Guest delivery</h3>

      {!configured ? (
        <p className="muted small">
          This build has no Google account set up, so finished sessions stay in the
          output folder and nothing is uploaded. That is the right setting for
          field testing. To turn it on, follow <code>docs/DRIVE-SETUP.md</code> and
          put the OAuth client in <code>appsettings.Local.json</code>.
        </p>
      ) : (
        <>
          {status.enabled && !status.authorised && (
            <p className="banner">
              Not signed in — nothing is being uploaded. Press Re-authorise.
            </p>
          )}

          <div className="controls">
            <button className="btn btn--primary" disabled={busy}
                    onClick={() => void save(
                      { driveEnabled: !status.enabled },
                      status.enabled ? 'Uploading switched off.' : 'Uploading switched on.')}>
              {status.enabled ? 'Turn uploading off' : 'Turn uploading on'}
            </button>
            <button className="btn" disabled={busy}
                    onClick={() => void post('/api/delivery/authorize', 'Signed in to Google Drive.')}>
              {status.authorised ? 'Re-authorise' : 'Sign in'}
            </button>
            {status.authorised && (
              <button className="btn" disabled={busy}
                      onClick={() => void post('/api/delivery/sign-out', 'Signed out.')}>
                Sign out
              </button>
            )}
          </div>

          <dl className="facts">
            <dt>Uploading</dt>
            <dd>{status.enabled ? 'On' : 'Off'}</dd>
            <dt>Account</dt>
            <dd>{account ?? (status.authorised ? 'signed in' : 'not signed in')}</dd>
            <dt>Waiting</dt>
            <dd>{status.pending} session{status.pending === 1 ? '' : 's'}</dd>
            {status.lastError && (
              <>
                <dt>Last error</dt>
                <dd className="bad">{status.lastError}</dd>
              </>
            )}
          </dl>

          <p className="muted small">
            Each session becomes its own Drive folder, shared by link, and the QR
            points at it — so a guest sees their own photos and nobody else’s.
            Uploads happen in the background: the guest never waits on the network,
            and a session captured with no signal keeps its photos and gets its
            link when the connection comes back.
          </p>
        </>
      )}

      {stuck.length > 0 && (
        <>
          <p className="banner">
            {stuck.length} session{stuck.length === 1 ? '' : 's'} gave up. The photos
            are safe in the output folder.
          </p>
          <ul className="slotlist">
            {stuck.map((s) => (
              <li key={s.folderName}>
                <code>{s.folderName}</code>
                <span className="muted small">{s.uploadError}</span>
                <button className="btn" disabled={busy}
                        onClick={() => void post(
                          `/api/delivery/republish/${s.folderName}`,
                          `${s.folderName} queued again.`)}>
                  Try again
                </button>
              </li>
            ))}
          </ul>
        </>
      )}
    </div>
  )
}
