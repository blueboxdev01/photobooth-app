# Photobooth App — Implementation Plan

## Context

You want a photobooth you run yourself at events: a **Canon EOS R50** on a tripod, a guest-facing monitor, and software on your laptop that runs the session — capture, review, strip composition, and delivery. Today `C:\Users\edwar\Projects\photobooth-app` is empty; this is greenfield.

Two decisions shape everything:

**v1 avoids the Canon developer SDK.** Rather than controlling the camera through EDSDK, the booth uses Canon's own free EOS Utility to write photos into a folder, and our app reacts to files appearing there. That trades away programmatic shutter control for shipping without a registration gate, at full 24 MP.

**Delivery is a per-session Google Drive folder.** Each session gets its own folder shared by link, and the QR points straight at it — so guests see only their own photos and there is no gallery to host.

## The flow

1. Guest steps into the booth and strikes **3 poses**.
2. You fire the shutter with the **BR-E1 remote** (or the camera's own button). The app never triggers the camera.
3. **EOS Utility** saves each JPEG straight into a watch folder.
4. The app ingests photos as they land and shows them as a **gallery preview** on both screens — thumbnails filling in "2 of 3". Any shot can be retaken.
5. The app composites the 3 photos into a **2×6 strip, three landscape frames plus a branding footer**, using the selected frame design.
6. The app writes the finished session to a **local session folder** — the strip plus the 3 raw photos — then creates a **matching Google Drive folder** and uploads the strip first, then the raws. Local and Drive hold the same four files under the same folder name.
7. The display shows a **QR code**. The guest scans it and gets **4 files: their 3 raw photos and the finished strip.**

**The template drives the shot count.** A template declares how many photo slots it has, and the session captures exactly that many. Change to a 4-frame strip and sessions become 4 shots — no separate setting to keep in sync.

Confirmed decisions:

| Decision | Answer |
|---|---|
| Camera | Canon EOS R50, USB-C tethered to EOS Utility |
| Camera control | **None from the app** — shutter fired by BR-E1 remote or camera button |
| Photo ingest | EOS Utility saves to a watch folder; app picks up new files |
| Guest live preview | **Separate webcam** as a posing mirror, independent of the R50 |
| Session output | 3 raw photos + 1 composited strip |
| Local archive | **Per-session folder mirroring Drive** — same name, same 4 files |
| Strip format | Classic **2×6 at 300 DPI (600×1800 px)**, 3 landscape frames + footer |
| Frame designs | Upload a frame PNG and **position slots visually** in-app |
| Delivery | Per-session Google Drive folder + QR |
| Storage account | Dedicated booth Google account (not personal) |
| Distribution | GitHub Actions → **Release ZIP**, self-contained (no .NET install) |
| Field testing | Colleague runs hardware bring-up remotely; their build is **camera + local only, Drive off** |
| Repo | Treated as public from day one — no secrets, no guest photos in git |
| Printing | Built last, after delivery works |
| Operation | Attended (you run it), self-serve kiosk mode later |
| Stack | .NET 10 + local web UI |

## System shape

```
  BR-E1 remote  ──bluetooth──╮
  (or shutter button)        ▼
                        Canon EOS R50 ──USB-C──▶ EOS Utility ──▶ watch folder
                                                 (Canon, free)        │
                                                                      │ new .JPG
  Webcam ──USB──╮                                                     ▼
  (posing mirror)│      ┌───────────────────────────────────────────────────┐
                 └─────▶│  Photobooth.Server   (single .exe, Kestrel :5000) │
                        │                                                   │
                        │   IPreviewSource        WatchFolderCamera         │
                        │        │                       │ PhotoArrived     │
                        │        │                       ▼                  │
                        │        │              Session engine              │
                        │        │                       │                  │
                        │        │                       ▼                  │
                        │        │       Compositor (SkiaSharp) + template   │
                        │        │                       │                  │
                        │        ▼                       ▼                  │
                        │   MJPEG preview         upload queue ─────────────┼──▶ Google Drive
                        └───────────────────────────────────────────────────┘      │
                              ▲                     ▲                              │
                         /operator              /display  ◀── QR (folder link) ◀────╯
                        (laptop screen)      (guest monitor)
```

The app never talks to the camera. It watches a folder, composites, uploads, and shows a QR.

## Why this stack

**.NET 10 (LTS) + ASP.NET Core serving a local web UI.** One process owns the session, serves both browser windows, and ships as a single self-contained `x64` `.exe`. You open two browser windows — operator controls on the laptop, fullscreen guest display dragged to the monitor.

For v1 the camera layer is just a `FileSystemWatcher`, so almost any stack would do. .NET stays the right call because it keeps the **EDSDK upgrade path open**: if you later want app-driven shutter control, EDSDK is a native C DLL with a strict single-thread + message-pump model that .NET P/Invoke handles cleanly, where Node or Python would fight it. Choosing .NET now costs nothing and avoids a rewrite later.

- **Backend**: .NET 10, ASP.NET Core Minimal APIs + SignalR, built `x64`.
- **Frontend**: Vite + React + TypeScript, built into `wwwroot`. Routes: `/operator`, `/display`, `/templates`.
- **Imaging**: SkiaSharp (MIT-family; avoids ImageSharp's commercial split-license question).
- **QR**: QRCoder (MIT). **Drive**: `Google.Apis.Drive.v3`.
- **Storage**: filesystem for photos, SQLite for session metadata and the upload queue.
- **Tests**: xUnit.

## Hardware and setup

**To buy:**

- **DR-E18 / LP-E17 dummy battery + USB-C PD adapter** (~$20). Non-negotiable. The R50 will **not** power itself over USB-C while tethered, so the dummy battery goes in the battery compartment and leaves the port free for data. A booth kills a real LP-E17 in well under an hour.
- **BR-E1 Bluetooth remote** (~$40, or a JJC clone ~$15). The R50 has **no wired remote terminal** — its [manual](https://cam.start.canon/en/C011/manual/html/UG-06_AF-Drive_0140.html) lists only the wireless BR-E1.
- **A cheap 1080p webcam** (~$25) for the posing mirror.
- **Short, good USB-C cable.** The R50 is USB 2.0 (480 Mbit/s) despite the connector; quality matters more than spec.
- Tripod, taped-down position, continuous lighting (no flash sync in v1).

**R50 settings to lock:** `Settings → USB Connection App → Photo Import/Remote Control`; manual exposure; **manual white balance** (not AWB — skin tone will shift shot to shot); fixed ISO; **JPEG only** (RAW roughly triples per-shot transfer over USB 2.0); auto power off disabled; image review off.

> **Verify this before building anything — it is the single biggest unknown in v1.** Canon's tethering guidance says to disable Wi-Fi and Bluetooth *before* connecting USB, and TetherTools recommends airplane mode for tether stability — exactly what the BR-E1 needs turned on. **BR-E1 while USB-tethered is unverified and may not work.**
>
> **10-minute test:** pair the BR-E1, connect USB with EOS Utility in remote-shooting mode saving to a folder, press the remote, and check whether a JPEG lands. If it fails, the fallback trigger is the camera's own shutter button — you're standing at the booth anyway, and nothing else in the plan changes.

## Architecture notes

### Photo arrival is an event, not a return value

```csharp
interface ICameraDevice {
    CameraCapabilities Capabilities { get; }   // CanTrigger, CanSetExposure, HasLiveView

    Task ConnectAsync();
    Task RequestCaptureAsync();                // no-op when the trigger is external
    event EventHandler<PhotoArrived> Photo;    // the session engine's real input

    event EventHandler<CameraEvent> Events;    // disconnect, error, stale
}
```

`WatchFolderCamera` reports `CanTrigger = false`, and the operator UI greys out controls it cannot honour. Modelling capture as `Task<Photo> CaptureAsync()` would bake in the assumption that the app drives the shutter — true for a future EDSDK adapter, false for every v1 path. Keeping arrival as the primary contract makes adding EDSDK later additive rather than a refactor.

### Preview is a separate concern from capture

```csharp
interface IPreviewSource {           // the webcam; knows nothing about the R50
    IAsyncEnumerable<PreviewFrame> FramesAsync(CancellationToken ct);
}
```

The guest monitor shows the webcam feed **horizontally flipped** so it behaves like a mirror — guests expect that and find an unflipped feed subtly wrong.

**The framing guide must show the strip's crop, not the camera's frame.** The R50 shoots 3:2, but strip slots are taller (4:3 by default, see below), so the composited photo is centre-cropped and loses the sides. Guests who fill the camera frame will lose their shoulders in the strip. Calibrate the webcam once against the R50's field of view and overlay the *slot* aspect on the posing mirror, so what guests see framed is what ends up on the strip.

> **I originally planned an HDMI capture dongle for a true camera preview.** Dropped from v1: reports indicate the R50 loses HDMI output during recording and EOS Utility's USB live view is the intended path, so HDMI-while-tethered is doubtful on this body. The webcam mirror sidesteps the question for $25.

### Watch-folder ingest is where the bugs will be

`FileSystemWatcher` fires on file *creation*, not completion, so naive handling reads half-written JPEGs. Ingest must:

- **Wait for completion** — retry opening with an exclusive lock, or poll until size is stable across two checks.
- **Filter to `.JPG`** — ignore `.CR3` sidecars if RAW ever gets switched on.
- **Ignore stale files** — accept only files written after the current session started, so leftovers never leak in.
- **Deduplicate** by filename; tolerate out-of-order arrival.
- **Time out** — with an external trigger the app cannot distinguish "not pressed yet" from "camera asleep". A ~20 s no-photo timeout returns to a clear error state instead of hanging.

### Session flow

`Idle → Armed → Collecting(k of N) → ReviewShots → Composing → ReviewStrip → Uploading → ShowQr → Done`

`Collecting` accepts photos as they arrive and updates both screens with a running count and live thumbnails. There is **no selection step** — every captured photo goes on the strip — but any shot can be **retaken**, which discards that file and returns to `Collecting`. The countdown is **advisory, not authoritative**: the app cannot fire the shutter, so the guest sees "3-2-1" and you press the remote. Accept the drift; don't hide it.

## Templates and the frame editor

A template is a JSON document plus an optional overlay PNG:

```jsonc
{
  "name": "Classic 2x6",
  "canvas": { "width": 600, "height": 1800, "dpi": 300 },   // 2in x 6in
  "overlay": "classic-2x6.png",        // frame art drawn on top, transparent where photos show
  "background": "#ffffff",
  "slots": [                            // slot count === shots per session
    { "x": 0.033, "y": 0.017, "w": 0.933, "h": 0.233, "fit": "cover" },
    { "x": 0.033, "y": 0.267, "w": 0.933, "h": 0.233, "fit": "cover" },
    { "x": 0.033, "y": 0.517, "w": 0.933, "h": 0.233, "fit": "cover" }
  ]
}
```

Slots are **normalised 0–1**, not pixels, so a template survives a change of output size or DPI. The defaults above give three 560×420 (4:3) frames with a ~450 px branding footer at the bottom. A 4:3 slot deliberately fills the strip better than the R50's native 3:2 would; the compositor centre-crops with `cover` fit.

**The frame editor** (`/templates`) is a real feature, not a config file: upload a frame PNG, and drag/resize the photo slots visually over it with live sample photos in place. It writes the JSON above. Because slots are normalised and the canvas is declared, the editor is a constrained drag-rectangle UI rather than a general design tool.

Ship a hand-written JSON template first so the compositor is never blocked on the editor — the editor lands after the core flow works end to end.

## Delivery — local archive, mirrored to Google Drive

### Local first

Every session is written to its own folder on disk, and **Drive receives a copy of exactly that folder**:

```
data/sessions/2026-09-05_1942_a7f3c2/
├─ strip.jpg          # the composited 2x6
├─ photo-1.jpg        # raws, in capture order
├─ photo-2.jpg
├─ photo-3.jpg
└─ session.json       # token, template used, timestamps, Drive folder id + URL, upload state
```

The Drive folder carries the **same name**, so the two are trivially cross-referenced. `session.json` is what makes the archive self-describing: any session can be re-published, re-composited, or audited without the database.

This ordering is deliberate — **compose and save locally, then upload.** The local copy is the source of truth and survives everything the network and Google can do to you: a revoked token, a full 15 GB, a deleted account, a venue with no signal. It also answers the request you *will* get a week later — "can you resend our photos?" — by looking in one folder.

Two implementation notes:

- **Copy the raws out of the watch folder, don't move them.** That folder belongs to EOS Utility; moving files out from under it invites trouble. Copy, verify the size, and optionally sweep the watch folder between sessions.
- **Disk budget:** roughly 30 MB per session (3 raws at ~8 MB plus the strip), so ~500 sessions per 15 GB — the same order as the Drive quota. Copy `data/sessions/` to an external drive after each event and you have a complete backup with no extra tooling.

### Then Drive

Each session creates its own Drive folder, shared **"anyone with the link → viewer"**, and the QR encodes that folder's `webViewLink`. Guests see only their own photos; nobody can reach another session by editing the URL. Security level is "anyone with the link can view" — a forwarded link works for whoever holds it, which is the normal photobooth model.

**Upload the strip first**, then the raws — it's one small file, so the folder is never empty if a guest scans immediately.

### Auth — get this configuration right or it breaks weekly

- **OAuth 2.0 installed-app (loopback) flow** against the dedicated booth account. **Not a service account**: service accounts have **0 GB storage quota** and cannot upload to a personal My Drive at all. This is the most common way this pattern fails.
- **Scope: `drive.file` only.** It grants access solely to files the app itself created, and it is a **non-sensitive** scope — so no verification and no security assessment.
- **Set the OAuth consent screen to "In production", not "Testing".** In Testing status Google revokes refresh tokens after **7 days**, so the booth would silently stop uploading about once a week. Because `drive.file` is non-sensitive, publishing needs no review.
- Store the refresh token locally, encrypted with DPAPI. Provide a "re-authorise" button in settings, and surface auth failure loudly on the operator screen rather than failing silently.

### Resilience

Uploads run through a **background queue in SQLite** with retry and backoff. The guest never waits on the network.

- **Local disk is the source of truth**; Drive is a publish target. Photos are written and composited locally before any upload is attempted.
- If **folder creation fails**, the session still completes, is marked "QR pending", and retries; you can publish afterwards from the operator screen. Never block the guest on the network.
- A stuck queue is visible on the operator screen with a count of pending uploads.

### Limits to plan around

- **Storage: 15 GB free** on the dedicated account, shared with that account's Gmail and Photos. At roughly 30 MB per session (3 raws + a strip) that's ~500 sessions. Archive and clear between events, or pay for more.
- **Drive API write limit is 3 sustained requests/second** — irrelevant at 4 files per session.
- **Mobile UX is Drive's, not yours.** iOS shows a "get the Drive app" banner, the page carries Google branding, and downloading several photos on an iPhone is fiddly. Accepted for v1; the branded gallery page below fixes it.
- **Privacy:** guest photos now live in **two** places — a cloud account behind a shareable link, and your laptop. Don't put guest names in folder names, and set one retention period that covers both, including the backup copy you take off the laptop after an event.

## Project layout

```
photobooth-app/
├─ src/
│  ├─ Photobooth.Core/        Session state machine, template model, tokens. No I/O.
│  ├─ Photobooth.Cameras/     ICameraDevice, WatchFolderCamera, MockCamera
│  ├─ Photobooth.Preview/     IPreviewSource, webcam capture
│  ├─ Photobooth.Imaging/     SkiaSharp compositor, QR, print renderer
│  ├─ Photobooth.Delivery/    IGalleryPublisher, DrivePublisher, upload queue
│  ├─ Photobooth.Server/      ASP.NET Core: APIs, SignalR hub, wwwroot
│  └─ Photobooth.Web/         Vite + React + TS → builds into Server/wwwroot
├─ tests/
│  ├─ Photobooth.Core.Tests/
│  ├─ Photobooth.Imaging.Tests/     golden-image comparisons
│  ├─ Photobooth.Cameras.Tests/     ingest edge cases against a temp folder
│  └─ Photobooth.Delivery.Tests/    queue retry/backoff against a fake publisher
├─ templates/                  frame PNGs + layout JSON
├─ data/                       (gitignored)
│  ├─ sessions.db              session metadata + upload queue
│  └─ sessions/<name>/         per-session archive, mirrored to Drive
└─ docs/
```

`Photobooth.Core` depends on neither ASP.NET nor SkiaSharp nor Google's SDK — the session logic stays testable in isolation, which matters because the state machine is where the subtle bugs live.

## Distribution — a build your colleague can actually run

Your colleague has the camera and is somewhere else, so **they perform hardware bring-up and send back a report.** That makes a runnable, self-describing build an early deliverable rather than a packaging afterthought, and it moves bring-up ahead of Drive and the frame editor.

Their build is **camera + local only, with Drive disabled by default** (`Delivery:Drive:Enabled = false`). They test everything the camera touches — capture, ingest, compositing, the local session archive — with no Google account, no OAuth, and no credentials changing hands. You test Drive separately afterwards, since it needs no camera.

### Repo hygiene — treated as public from day one

Costs nothing now and means you can flip the repo public later without an audit:

- `.gitignore` covers `data/` (guest photos and the token store), `appsettings.Local.json`, and build output. **No guest photo ever gets committed.**
- Commit `appsettings.example.json`; real values live in untracked `appsettings.Local.json`.
- No OAuth client file, token, or secret in git — even a desktop client secret, which isn't truly secret but should never be in a public repo.

### Build and release

A GitHub Actions workflow on tag push: build the Vite frontend, `dotnet publish -r win-x64 --self-contained`, zip, and attach to a **GitHub Release**. Your colleague downloads, unzips, and runs — no .NET install, no Node, no toolchain. Every test round is tied to a specific commit, so you always know exactly what they ran.

**Stamp the build with the git SHA and show it in the UI.** Remote debugging where you can't be sure which version produced a result is miserable.

### Making a remote test actually useful

The tester can't show you their screen, so the app has to report on itself. A **`/diagnostics` page** showing:

- Watch folder path, whether it exists and is writable, and files currently seen
- A live ingest log — every file detected, accepted, or rejected, with reasons and timestamps
- **Measured press-to-file latency** per shot (this is a number we're guessing at, and only they can measure it)
- Observed file naming pattern from EOS Utility, versus what the config expects
- Detected webcam devices, free disk space, app version + commit SHA

Plus a rolling log file, and an **"Export diagnostics bundle"** button that zips the logs, `session.json` files, and the composited strips into one file they can send back. **Raw guest photos are excluded by default** — they're the sensitive part, and the strips plus logs answer nearly every question.

Ship a `docs/FIELD-TEST.md` checklist so the report comes back structured rather than as "it mostly worked": the BR-E1 tethering result, observed file naming, measured latency, each deliberate failure case, and a judgement on the 3:2 → 4:3 crop on real faces.

## Building before the camera arrives

The whole app can be built and tested with no camera, because the app's only interface to it is **a folder with JPEGs in it**. Everything upstream — the R50, the remote, EOS Utility — is replaced by a mock that writes files into that same folder. Only bring-up (M6, done remotely by your colleague) and the dress rehearsal need hardware.

**For this to be worth anything, the mock must be adversarial.** A mock that writes files atomically and on a tidy schedule would let every ingest bug survive until the camera arrives, which defeats the purpose. `MockCamera` should reproduce how EOS Utility actually behaves and how it misbehaves:

- Write into the **real watch folder** using EOS Utility's naming pattern (`IMG_0001.JPG`, incrementing).
- **Write slowly, in chunks**, so the half-written-file problem genuinely occurs and the completion check is exercised.
- **Irregular gaps** between shots, since a human presses the remote — not a fixed timer.
- Inject the failure modes on demand: a duplicate filename, a stale file predating the session, a burst of two files from a double-press, and **no file at all** so the timeout path runs.

You'll also need a handful of **real 3:2 JPEGs at full resolution** (~6000×4000) as compositor input. Decoding three 24 MP images and compositing them is not free, and testing against small images would hide how long a session actually takes.

The **laptop's built-in webcam** stands in for the posing mirror during development — the `IPreviewSource` interface doesn't care which camera it is.

**What genuinely waits for hardware**, and what it may cost:

| Needs the camera | Risk if our assumption is wrong |
|---|---|
| BR-E1 + USB tethering conflict test | Changes the trigger, not the architecture |
| EOS Utility's real file naming and write behaviour | Ingest is written against an assumption; keep the file pattern, extension, and stability-check interval **configurable** so a surprise is a settings change, not a rewrite |
| Time from remote press to file appearing | Tunes the timeout value only |
| Framing-guide calibration (webcam vs R50 field of view) | Needs both devices side by side; pure measurement, no code risk |
| Real crop, colour, and image quality | May send you back to the template slot geometry |

Treat hardware bring-up as a real checkpoint that can require rework, not a formality.

## Milestones

Ordered so everything camera-independent comes first.

### M1 — Skeleton + mock camera end to end
Solution, projects, DI wiring. `MockCamera` drops sample JPEGs into a temp folder on a timer to imitate an external trigger. Bare `/operator` and `/display` pages.
**Verify:** `dotnet run`, watch a mock photo appear on the display page.

### M2 — Session engine + two windows + posing mirror
The state machine above, SignalR pushing state to both windows, operator controls (Arm / Retake / Abort / Accept), guest-facing gallery preview filling in as shots arrive. Webcam preview via `IPreviewSource`, mirrored, with the slot-aspect framing guide.
**Verify:** unit tests for illegal transitions, retake discarding exactly one shot, and the no-photo timeout firing. Run a 3-shot mock session and confirm both windows stay in sync.

### M3 — Watch-folder ingest, hardened *(no camera)*
`WatchFolderCamera` with the full ingest hardening above, driven by the adversarial mock. Every assumption about EOS Utility — file pattern, extension, stability-check interval, timeout — lives in configuration rather than in code.
**Verify:** an automated test per failure mode: half-written file, duplicate filename, stale file from a previous session, two files from a double-press, and no file at all. Each must land in a clear state — never a crash, never a wrong photo on the strip.

### M4 — Compositor + the 2×6 strip + local session archive
Template model, `cover` centre-crop, overlay compositing, hand-written `Classic 2x6` template. Writes the per-session folder — strip, raws copied from the watch folder, and `session.json`.
**Verify:** golden-image tests against a checked-in expected PNG. Confirm output is exactly 600×1800 with 300 DPI metadata, and eyeball a real session's strip at full size. Confirm the session folder holds all 4 files plus valid `session.json`, and that the originals are still sitting untouched in the watch folder.

### M5 — Field-test build *(what your colleague runs)*
Self-contained `x64` publish with the frontend bundled. GitHub repo with the hygiene rules above, `.gitignore`, and `appsettings.example.json`. Actions workflow producing a Release ZIP on tag. `/diagnostics` page, rolling log, diagnostics-bundle export. Drive disabled by default. `README.md` covering EOS Utility setup, the R50 settings list, choosing the watch folder, and launching both windows. `docs/FIELD-TEST.md` checklist.
**Verify:** unzip the Release artifact on a **clean machine with no .NET installed** and run a full mock session end to end. This is the real test — if it needs a toolchain, it isn't a field-test build. Confirm the UI shows the commit SHA and that the diagnostics bundle contains no raw guest photos.

### M6 — Remote hardware bring-up *(your colleague, with the camera)*
They run the checklist and send back a diagnostics bundle. In order: **BR-E1 + USB tethering test**; EOS Utility saving into the watch folder; observed file naming versus config; measured press-to-file latency; the deliberate failure cases (double-press, USB unplug mid-session, kill EOS Utility, stale file); and a judgement on the 3:2 → 4:3 crop on real faces.
**Verify:** you receive a bundle showing a complete 3-shot session producing 4 correct local files. Then fold the findings back in — expect to adjust M3's config values and possibly M4's slot geometry. Budget a second build and a second round; one pass rarely settles it.

### M7 — Google Drive delivery + QR (the primary output)
Dedicated account and OAuth app configured per the auth section. `DrivePublisher`: create folder, set link permission, upload strip then raws. SQLite-backed upload queue with retry. QR on the display at session end.
**Verify:** run a session and scan the QR **from a phone on cellular** — confirm the folder holds exactly 4 files and that a second session's link shows different photos. Confirm the Drive folder and the local session folder hold identical files under the same name, and that `session.json` records the Drive URL. Then the failure paths: pull the network mid-upload and confirm the queue drains on reconnect; start a session fully offline and confirm it completes as "QR pending" **with the local archive fully intact**; re-publish that session afterwards from the operator screen; revoke the token and confirm the operator screen says so loudly.

### M8 — Frame upload + visual slot editor
`/templates` page: upload a frame PNG, drag and resize photo slots over it with live sample photos, name and save. Templates become selectable per event, and the chosen template sets the session's shot count.
**Verify:** upload a new frame, position 3 slots, run a session against it, and confirm the strip matches the editor preview pixel-for-pixel. Confirm switching to a 4-slot template makes sessions capture 4 shots with no other change.

### M9 — Event hardening + dress rehearsal
Launcher that starts the server and opens both windows (`msedge --app=http://localhost:5000/display`). Settings UI for watch folder, template, countdown, Drive account. Sessions survive a crash. Free-disk warning on startup.
**Verify:** a **full dress rehearsal on the real rig** — clean boot, 20 sessions back to back, all uploads drained, several QRs scanned on cellular. Watch for memory climbing across sessions and for the R50 warming up under sustained tethered use.

### M10 — Printing
Render the strip at physical size to a Windows printer via `System.Drawing.Printing`, through a **background queue** so guests never wait. Two strips fit on one 4×6 sheet.
**Verify:** print and *measure* — the strip must come out actually 2×6, not scaled-to-fit. Queue three, kill the printer mid-queue, confirm no photos are lost.

### Later
- **Branded gallery page** — your own page reading from Drive, replacing Drive's mobile UI.
- **EDSDK adapter** — app-driven shutter, making the countdown authoritative and unlocking exposure control. Requires Canon developer registration.
- **UVC webcam mode** — the R50 as a plain 1080p webcam; app-controlled timing with no SDK, at 2 MP.
- **PTP/WIA spike** — Windows can pull files off the camera with no Canon software, and WIA exposes a `TAKE_PICTURE` command some cameras implement. Unverified on the R50; ~20 minutes to find out.
- **Kiosk mode** — attract screen, hardware button, auto-advance.

## Risks

| Risk | Mitigation |
|---|---|
| **BR-E1 doesn't work while USB-tethered** | First test at M6 bring-up; fallback is the camera's own shutter button. Nothing built before M6 depends on the answer |
| Ingest built against **assumed** EOS Utility behaviour | File pattern, extension, stability interval and timeout are all configuration; M6 explicitly budgets for correcting them |
| Remote tester can't diagnose what went wrong | `/diagnostics` page, commit SHA in the UI, and an exportable diagnostics bundle; findings come back structured via `docs/FIELD-TEST.md` |
| Guest photos leak into the repo or a bundle | `data/` gitignored from the first commit; diagnostics bundle excludes raw photos by default |
| **OAuth consent screen left in "Testing"** | Refresh tokens die after 7 days and uploads stop silently. Publish to "In production"; `drive.file` is non-sensitive so no review is needed |
| Service account used by mistake | 0 GB quota, uploads fail outright. Installed-app OAuth only |
| Half-written JPEGs read from the watch folder | Explicit completion check in ingest; covered by tests in M3 |
| Guests cropped badly by the 3:2 → 4:3 slot crop | Framing guide on the posing mirror shows the *slot* aspect, not the camera frame |
| External trigger drifts from the countdown | Countdown advisory by design; no-photo timeout prevents hangs |
| Network dies mid-event | Local disk is source of truth; upload queue retries; sessions complete as "QR pending" |
| Guest scans before the upload finishes | Strip uploads first, so the folder is never empty |
| 15 GB Drive quota fills up | ~500 sessions; archive between events or upgrade the plan |
| Laptop disk fills up mid-event | ~30 MB/session; app checks free space on startup and warns below a threshold. Copy `data/sessions/` off after each event |
| EOS Utility crashes or steals focus mid-event | Treated as an external dependency; app surfaces a stale-camera state rather than failing silently |
| R50 battery dies mid-event | DR-E18 dummy battery; USB-C cannot power this body while tethered |

## First actions

The camera is with your colleague, so the goal is to reach **M5 quickly** — the first build they can actually run — then let bring-up happen in parallel with your Drive and editor work.

1. `git init` with the `.gitignore` and `appsettings.example.json` in the **first commit**, so guest photos and secrets are never in history. Scaffold per the layout above and build **M1**.
2. Gather a handful of **full-resolution 3:2 JPEGs** as compositor test input.
3. Build straight through **M2 → M5**. M5 is the milestone that unblocks your colleague; everything after it can wait.
4. Make sure your colleague has the **BR-E1 remote and dummy battery**, and can install EOS Utility (it's a free Canon download, tied to the camera they already have).
5. Send them the Release ZIP plus `docs/FIELD-TEST.md`. **The BR-E1 + tethering test is item one** — it's the single unknown that could change the trigger.
6. While they test, do **M7 (Drive)** yourself — it needs no camera at all.
