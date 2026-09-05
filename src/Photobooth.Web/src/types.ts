export type SessionState =
  | 'Idle'
  | 'Countdown'
  | 'Collecting'
  | 'TimedOut'
  | 'ReviewShots'
  | 'Composing'
  | 'Uploading'
  | 'ShowQr'
  | 'Done'

export interface CapturedPhoto {
  filePath: string
  fileName: string
  sizeBytes: number
  detectedAtUtc: string
}

export interface SessionSnapshot {
  state: SessionState
  shotCount: number
  photos: CapturedPhoto[]
  /** Absolute instants, so the browser ticks the countdown locally. */
  countdownEndsUtc: string | null
  timeoutAtUtc: string | null
  startedUtc: string | null
  message: string | null
  capturedCount: number
  currentShot: number
}

export interface CameraInfo {
  status: 'Disconnected' | 'Ready' | 'Faulted'
  canTrigger: boolean
  watchFolder: string
}

export const photoUrl = (p: CapturedPhoto) =>
  `/api/photos/${encodeURIComponent(p.fileName)}`
