# Phase 1 — Skeleton and Watch-Folder Ingest

The foundation of the photobooth: a working photo pipeline from "shutter fired"
to "photo on screen", built and verified **with no camera present**.

## What this phase establishes

### The camera is not a dependency

The app's only interface to the camera is **a folder with JPEGs in it**. EOS
Utility sits upstream of that folder, and the Canon EOS R50 upstream of EOS
Utility. Nothing in this codebase talks to a camera, an SDK, or a driver.

That is what makes the rest of the project tractable: the entire application can
be built and tested before the hardware is in the same room, and the eventual
field test becomes a verification step rather than a prerequisite.

### Photo arrival is an event, not a return value

```csharp
Task RequestCaptureAsync();             // no-op when the trigger is external
event EventHandler<PhotoArrivedEventArgs> PhotoArrived;
```

In v1 the shutter is fired by a physical BR-E1 remote, so the app can only ever
*observe* photos appearing — it cannot request one. Modelling capture as
`Task<Photo> CaptureAsync()` would bake in the opposite assumption and force
every adapter to fake a return value. `CameraCapabilities.CanTrigger` is `false`
and the operator UI is expected to grey out controls accordingly.

This keeps a future EDSDK adapter (which *can* trigger) additive rather than a
refactor.

### Ingest assumes a file will lie about being ready

`FileSystemWatcher` fires on file **creation**, not completion. Reading a 24 MP
JPEG the moment it appears gets you a truncated image. `WatchFolderCamera`
therefore:

- waits for the file size to hold steady across several samples **and** for an
  exclusive open to succeed — a steady size alone only proves the writer is
  between chunks;
- deduplicates by path, since the watcher, the periodic sweep, and
  Created-then-Renamed can all surface the same file;
- ignores files written before the session began, so leftovers cannot leak onto
  a strip;
- re-scans the folder periodically, because `FileSystemWatcher` drops events
  under load — a dropped event becomes a late photo rather than a lost one;
- gives up on a file that never settles, instead of hanging the session.

### Everything we assume about EOS Utility is configuration

File naming, extensions, the stability interval, the completion timeout — none
of it has been checked against a real camera yet. It all lives in
`appsettings.json` so that when the field test contradicts us, the fix is a
settings change rather than a rewrite.

### The mock is deliberately adversarial

`MockEosUtility` stands in for Canon's software. A mock that wrote files
atomically on a tidy schedule would let every ingest bug survive until the camera
arrived, which defeats the purpose. Instead it writes **slowly, in chunks**, and
can reproduce:

| Mode | What it does |
|---|---|
| `Normal` | Chunked write, like a real USB 2.0 transfer |
| `DuplicateName` | Reuses the previous file name |
| `Stale` | Back-dates the file, imitating a leftover |
| `NeverFinishes` | Writes half and holds the handle open past the timeout |

This paid for itself immediately: the first `NeverFinishes` implementation wrote
half a file and *closed* the handle, so it settled and ingest accepted a
truncated JPEG. A tidier mock would have hidden that until a real stalled
transfer hit it in the field.

## Verified

| Case | Result |
|---|---|
| Normal press | Accepted, **byte-identical** to source, decodes fully |
| Rapid double press | Two distinct photos, both accepted |
| Stale file (back-dated 2h) | Ignored, reason logged |
| Duplicate of an accepted name | Deduplicated, no double-add |
| Stalled transfer | Never accepted — no truncated JPEG reaches the strip |

Every ingest decision is logged with its reason, which is what the remote field
test will depend on.

## Known limitation, deferred by choice

Candidates are processed one at a time to preserve capture order, so a stalled
transfer delays photos queued behind it by up to the completion timeout. The fix
is bounded concurrency plus sorting by write time. It is deferred until the field
test shows whether stalled transfers actually happen in practice.

## Not in this phase

Session state machine, compositing, the 2×6 strip, Google Drive delivery, QR
codes, the frame editor, and printing. See
[docs/IMPLEMENTATION-PLAN.md](../IMPLEMENTATION-PLAN.md) for the full roadmap.

## Running it

Requires the .NET 10 SDK.

```
dotnet build
dotnet src/Photobooth.Server/bin/Debug/net10.0/Photobooth.Server.dll
```

Open <http://localhost:5000/operator>. With no camera attached, the page can
simulate the shutter in any of the four modes above.
