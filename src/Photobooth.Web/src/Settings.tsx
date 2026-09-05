import { useCallback, useEffect, useState } from 'react'

interface SettingsResponse {
  watchFolder: string
  countdownSeconds: number
  noPhotoTimeoutSeconds: number
  settingsFile: string
  suggestions: string[]
}

interface FolderCheck {
  path: string
  ok: boolean
  error: string | null
  exists: boolean
  willCreate: boolean
  jpegCount: number
}

/**
 * Booth setup.
 *
 * The watch folder is the first thing anyone has to get right and the one thing
 * nobody can know in advance -- it is wherever EOS Utility happens to save on
 * that machine. Making it editable here rather than in appsettings.json is the
 * difference between a two-second fix and a restart-and-guess cycle.
 *
 * There is no folder picker: browsers will not hand a web page a filesystem
 * path. Paste it from EOS Utility's own save-location setting, or from
 * Explorer's address bar.
 */
export function Settings({ onChanged }: { onChanged?: () => void }) {
  const [data, setData] = useState<SettingsResponse | null>(null)
  const [folder, setFolder] = useState('')
  const [countdown, setCountdown] = useState(3)
  const [timeout, setTimeoutSeconds] = useState(20)
  const [check, setCheck] = useState<FolderCheck | null>(null)
  const [status, setStatus] = useState<{ ok: boolean; text: string } | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    const r = await fetch('/api/settings')
    if (!r.ok) return
    const body: SettingsResponse = await r.json()
    setData(body)
    setFolder(body.watchFolder)
    setCountdown(body.countdownSeconds)
    setTimeoutSeconds(body.noPhotoTimeoutSeconds)
  }, [])

  useEffect(() => { void load() }, [load])

  if (!data) return null

  const dirty =
    folder.trim() !== data.watchFolder ||
    countdown !== data.countdownSeconds ||
    timeout !== data.noPhotoTimeoutSeconds

  const checkFolder = async () => {
    setBusy(true)
    try {
      const r = await fetch('/api/settings/check-folder', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ watchFolder: folder }),
      })
      setCheck(r.ok ? await r.json() : null)
    } finally {
      setBusy(false)
    }
  }

  const save = async () => {
    setBusy(true)
    try {
      const r = await fetch('/api/settings', {
        method: 'PUT',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({
          watchFolder: folder.trim(),
          countdownSeconds: countdown,
          noPhotoTimeoutSeconds: timeout,
        }),
      })
      const body = await r.json()
      if (!r.ok) {
        setStatus({ ok: false, text: body.error ?? `HTTP ${r.status}` })
        return
      }

      setStatus({ ok: true, text: `Now watching ${body.watchFolder}` })
      setCheck(null)
      await load()
      onChanged?.()
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="settings">
      <h2>Setup</h2>

      {status && <p className={status.ok ? 'muted' : 'banner'}>{status.text}</p>}

      <label className="settings__folder">
        Watch folder — where EOS Utility saves
        <input
          value={folder}
          spellCheck={false}
          placeholder="C:\Users\you\Pictures\Photobooth"
          onChange={(e) => { setFolder(e.target.value); setCheck(null) }}
        />
      </label>

      <div className="controls">
        <button disabled={busy || !folder.trim()} onClick={() => void checkFolder()}>
          Check folder
        </button>
        <button className="primary" disabled={busy || !dirty} onClick={() => void save()}>
          Save and apply
        </button>
      </div>

      {check && (
        <p className={check.ok ? 'muted small' : 'banner'}>
          <code>{check.path}</code>{' '}
          {check.ok
            ? check.willCreate
              ? '— usable; it will be created when you save'
              : `— usable, ${check.jpegCount} JPEG(s) already there`
            : `— ${check.error}`}
        </p>
      )}

      {data.suggestions.length > 0 && (
        <p className="muted small">
          Try:{' '}
          {data.suggestions.map((s) => (
            <button key={s} className="linkish" onClick={() => { setFolder(s); setCheck(null) }}>
              {s}
            </button>
          ))}
        </p>
      )}

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
      <p className="muted small">
        Set the timeout from the press-to-file latency measured below — it is a
        guess until someone with a camera measures it.
      </p>

      <p className="muted small">
        Saved to <code>{data.settingsFile}</code>, so it survives a restart.
        The watch folder is applied immediately; no restart needed.
      </p>
    </section>
  )
}
