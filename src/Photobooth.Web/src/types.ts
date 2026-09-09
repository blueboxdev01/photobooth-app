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
  /**
   * For each entry in `photos`, the 0-based position it was captured in. After
   * dragging the fourth shot to the front of six this is [3, 0, 1, 2, 4, 5], so a
   * thumbnail can still be labelled with the pose it actually is.
   */
  order: number[]
  /** True once the shots have been rearranged, so the console can offer to undo it. */
  isReordered: boolean
  /** Absolute instants, so the browser ticks the countdown locally. */
  countdownEndsUtc: string | null
  timeoutAtUtc: string | null
  startedUtc: string | null
  message: string | null
  capturedCount: number
  currentShot: number
  /**
   * The strip position currently being reshot, 0-based, when one is. Lets both
   * screens name the pose instead of showing a bare countdown.
   */
  retakingSlot: number | null
  /** Set once the strip is composed and archived. */
  stripUrl: string | null
  sessionFolder: string | null
}

/**
 * Delivery, which is deliberately not part of the session snapshot: an upload
 * outlives the session that produced it, so `sessionFolder` says which session
 * this update is about and the screens match it against the one they are showing.
 */
export interface DeliveryUpdate {
  /** Uploading is switched on and a Google client is configured. */
  enabled: boolean
  /** A token is held. False means somebody has to press Re-authorise. */
  authorised: boolean
  pending: number
  failed: number
  lastError: string | null
  sessionFolder: string | null
  state: 'NotAttempted' | 'Pending' | 'Uploaded' | 'Failed' | null
  url: string | null
  qrUrl: string | null
  error: string | null
}

export interface CameraInfo {
  status: 'Disconnected' | 'Ready' | 'Faulted'
  canTrigger: boolean
  watchFolder: string
}

export const photoUrl = (p: CapturedPhoto) =>
  `/api/photos/${encodeURIComponent(p.fileName)}`
