# Photobooth App

Photobooth software for a **Canon EOS R50**: EOS Utility saves tethered captures
into a watch folder, this app ingests them, composites a 2×6 strip, and delivers
the session to the guest as a QR code.

**The app never talks to the camera.** Its only interface is a folder with JPEGs
in it — EOS Utility sits upstream, and the camera upstream of that. That is what
lets the whole thing be built and tested with no camera present.

See [docs/IMPLEMENTATION-PLAN.md](docs/IMPLEMENTATION-PLAN.md) for the full plan.

## Status

| Milestone | State |
|---|---|
| M1 Watch-folder ingest | done |
| M2 Session engine, two-window flow, posing mirror | done |
| M3 Ingest hardening | done — 12 tests, one per failure mode |
| M4 Compositor + 2×6 strip + local archive | done — golden-image tested |
| **M5 Field-test build** | **done — this is what your colleague runs** |
| M6 Remote hardware bring-up | waiting on the camera |
| M7 Google Drive delivery + QR | done — off by default, see [docs/DRIVE-SETUP.md](docs/DRIVE-SETUP.md) |
| M8 Frame upload + slot editor | done — `/templates` |
| Operator console | done — dashboard, light/dark, layout and folder settings |

181 tests passing. Nothing has yet been verified against a real camera.

Each session writes `data/sessions/<name>/` holding the strip, the raw photos,
and a `session.json` describing them. The Drive folder receives a copy of exactly
that folder under the same name.

## Guest delivery

Each finished session becomes **its own Google Drive folder**, shared by link,
and the guest screen shows a QR pointing at it. They scan it and get their own
photos — there is no gallery, and no id to edit to reach anyone else's session.

**Off unless you set it up.** With no Google account configured — which is how
the field-test build ships — sessions are saved to the output folder and the
guest screen says to ask for them. Turning it on is
**[docs/DRIVE-SETUP.md](docs/DRIVE-SETUP.md)**, about fifteen minutes.

**The guest never waits on the network.** The strip is composed and everything is
written to disk *before* an upload is attempted, so a venue with no signal costs
a guest their QR code and nothing else. Uploads run in the background and retry
with a widening gap; a session that never uploaded can be published from Setup
days later.

Sessions are filed **one folder per guest, inside a single parent folder** the
app creates — `Photobooth` by default, renameable in Setup. If they look loose,
that is Drive's **Home** tab listing recent files flat; **My Drive** shows the
folders.

A **`qr.png`** is written into each session's folder — on disk and in Drive —
once there is a link for it to point at. A guest who comes back next week having
lost their link can be found from the folder alone, with no need for the booth to
be running.

**The QR appears before the upload finishes.** The strip goes up first and the
link is published the moment it lands, while the raw photos -- which are most of
the ~25 MB -- are still going. Waiting for the whole session would routinely put
the code on screen after the guest had walked away, which is the same as no code.
A guest who scans early sees their strip and watches the rest arrive.

There is no upload database. The queue **is** the archive — the work is every
session whose `session.json` says it has not been published yet. So it survives
being killed mid-upload with no recovery code, cannot disagree with what is
actually on disk, and a stuck session can be unstuck in Notepad.

Failures are told apart, because retrying does not fix all of them equally: a
dropped network is retried, a revoked sign-in or a full account is not, and both
say so on the operator screen rather than stalling quietly.

## Retaking one shot

Any single shot can be redone from **review**, before you press Accept: press
**Retake this one** on its thumbnail and the booth goes back to waiting for
exactly that pose. Every other shot keeps its photo and its position, and the
replacement drops into the same slot.

Both screens name the pose being redone, so a guest reshooting photo two is not
told they are on photo four.

**Retake** in the rail still means the shot taken *most recently*, whatever
position it has been dragged to — the two are deliberately separate.

## Rearranging the shots

The guest poses N times, and which of those opens the strip is a judgement no
software makes well. So during **review**, before you press Accept, the shots can
be put in any order: drag a thumbnail, or use the arrows on it. **Back to capture
order** undoes the lot.

The order decides everything downstream, not just the preview -- the strip, the
numbering of the raw photos in the session folder, and what the guest screen
shows. `photo-1.jpg` is whichever shot you put first.

Each thumbnail keeps its **capture number**, so a photo dragged to the front is
still labelled "shot 4" and you can talk about it with the person next to you.
Retake still discards the shot taken *last*, whatever position it has been moved
to, and its replacement comes back into the same slot rather than jumping to the
end.

Rearranging is only possible while reviewing: before that the set is incomplete,
and after Accept the strip is already being built.

## Two cameras, two entirely separate paths

| | Capture | Preview |
|---|---|---|
| Device | Canon EOS R50 | any webcam |
| Reaches the app as | JPEGs in a watch folder | a browser video stream |
| Triggered by | a physical BR-E1 remote | n/a |
| App can trigger it? | **no** | n/a |

The app cannot fire the shutter, so a photo *arrives* as an event rather than
being requested. Everything downstream is built around that.

## Running it

**From a release** — download the ZIP, unzip, run `Photobooth.Server.exe`.
Nothing to install.

**From source** — needs the .NET 10 SDK and Node 22:

```bash
cd src/Photobooth.Web && npm ci && npm run build && cd ../..
dotnet build
dotnet src/Photobooth.Server/bin/Debug/net10.0/Photobooth.Server.dll
```

Then open:

| | |
|---|---|
| <http://localhost:5000/operator> | controls — on your laptop |
| <http://localhost:5000/display> | guest screen — fullscreen on the monitor |
| <http://localhost:5000/templates> | frame upload and the slot editor |
| <http://localhost:5000/diagnostics> | booth setup, and what the app is seeing |

With no camera attached, the operator page can simulate the shutter. The mock is
deliberately adversarial — it writes slowly in chunks and can reproduce a stale
file, a duplicate name, and a transfer that stalls — so ingest is exercised
before real hardware exists.

## Before an event

**[docs/QA-CHECKLIST.md](docs/QA-CHECKLIST.md)** — half a day, on the machine and
camera you will actually use. It starts with the three checks that could change
your plans (does the remote work while tethered, does the camera stay powered,
does a press become a strip) and ends in a go/no-go.

## Field testing

See **[docs/FIELD-TEST.md](docs/FIELD-TEST.md)**. The short version: the build
runs standalone, uploads nothing, and `/diagnostics` reports what the app saw,
what it rejected and why, and exactly which commit produced the answer.

## Template art

Art is uploaded in **Templates** and can be either kind. Which one it is gets
**detected from the image**, by checking whether it is transparent where the
photos go:

| | |
|---|---|
| **Backdrop** | Opaque across the photo areas. Drawn *behind*, with the photos on top. PNG or JPEG |
| **Frame** | Transparent where the photos go. Drawn *over* them, showing them through its windows. PNG only |

Judging by transparency **inside the photo slots** rather than overall is what
makes this reliable: a frame with a wide solid border is mostly opaque and is
still a frame. If the guess is ever wrong, the editor has a one-click override.

Upload at exactly these pixel sizes — all at 300 DPI:

| Output size | Pixels |
|---|---|
| Photo strip 2×6 | 600 × 1800 |
| Portrait 4×6 | 1200 × 1800 |
| Portrait 5×7 | 1500 × 2100 |
| Landscape 6×4 | 1800 × 1200 |
| Landscape 7×5 | 2100 × 1500 |

Anything else is scaled to **fill and centre-crop**, so proportions are never
distorted but the edges get trimmed. The editor states the expected size next to
the upload control, and warns when what you uploaded does not match.

Photo slots can be dragged and resized freely in the editor. Note that changing
the photo count or output size in **Setup** regenerates the slots evenly and
discards manual positioning, so settle those first.

## Upgrading a booth

`data/` and `templates/` live **next to the .exe**, so when you unzip a new
release, copy both across from the old folder. `data/` holds every past session;
`templates/` holds any frame you made in the editor.

## Booth setup

Everything an event needs is on the diagnostics page under **Setup**, saved to
`data/settings.json` so it survives a restart.

**Two folders, and they must be different ones.**

| | |
|---|---|
| **Watch folder** | Wherever EOS Utility saves. Every guest's raw frames land here together — it belongs to Canon's software, and the app only copies out of it |
| **Output folder** | Where a finished session is filed, **one subfolder per guest**, holding the raw photos, the strip and a `session.json` |

Paste a path, press *Check* to confirm it is usable, then save. Applied
immediately, no restart.

**Strip layout.** Pick an output size — the shape decides the arrangement, so a
portrait size stacks photos into a strip while a landscape one runs them along a
row and then into a grid. Set how many photos a strip holds, within the
minimum and maximum this event allows, and the slots are placed evenly. They can
still be nudged by hand in the template editor afterwards.

Changing the size or photo count re-lays the slots out, which **detaches frame
art** drawn for the old shape — the PNG stays on disk and can be re-attached in
the editor once you have art for the new layout.

**Guest display.** A backdrop colour and an optional image, so the booth can
match an event.

**Timings.** Countdown and the no-photo timeout. The timeout is a guess until
someone measures the real press-to-file latency, which the same page can do.

## Configuration

`src/Photobooth.Server/appsettings.json`, mirrored by `appsettings.example.json`.

Everything under `Camera:WatchFolder` is an **assumption** about how EOS Utility
behaves that has not been checked against a real camera. It is configuration
rather than code precisely so that when the field test contradicts it, the fix is
a settings change and not a rewrite.

Relative paths resolve against the app folder, so a published build behaves the
same as a local run.

## Releases

Push a tag and CI builds the frontend, runs the tests, publishes a self-contained
`win-x64` single file, and attaches the ZIP to a GitHub release:

```bash
git tag v0.5.0 && git push origin v0.5.0
```

## Repo hygiene

`data/` holds guest photos and is never committed. Secrets live in untracked
`appsettings.Local.json`; only `appsettings.example.json` is tracked. The
diagnostics bundle deliberately excludes photographs.
