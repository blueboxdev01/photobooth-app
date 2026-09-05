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
| M7 Google Drive delivery + QR | not started |
| M8 Frame upload + slot editor | not started |

42 tests passing. Nothing has yet been verified against a real camera.

Each session writes `data/sessions/<name>/` holding the strip, the raw photos,
and a `session.json` describing them. From M7 the Drive folder receives a copy of
exactly that folder under the same name.

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
| <http://localhost:5000/diagnostics> | what the app is seeing |

With no camera attached, the operator page can simulate the shutter. The mock is
deliberately adversarial — it writes slowly in chunks and can reproduce a stale
file, a duplicate name, and a transfer that stalls — so ingest is exercised
before real hardware exists.

## Field testing

See **[docs/FIELD-TEST.md](docs/FIELD-TEST.md)**. The short version: the build
runs standalone, uploads nothing, and `/diagnostics` reports what the app saw,
what it rejected and why, and exactly which commit produced the answer.

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
