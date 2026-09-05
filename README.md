# Photobooth App

Photobooth software for a Canon EOS R50: EOS Utility saves tethered captures into
a watch folder, this app ingests them, composites a 2x6 strip, and delivers the
session to the guest as a QR code.

**The app never talks to the camera.** Its only interface is a folder with JPEGs
in it, so the whole thing can be built and tested with no camera present.

See [docs/IMPLEMENTATION-PLAN.md](docs/IMPLEMENTATION-PLAN.md) for the full plan.

## Status

M1 complete: skeleton, watch-folder ingest, and an adversarial EOS Utility mock.

## Running it

Requires the .NET 10 SDK.

```
dotnet build
dotnet src/Photobooth.Server/bin/Debug/net10.0/Photobooth.Server.dll
```

Then open <http://localhost:5000/operator> (controls) and
<http://localhost:5000/display> (guest screen).

With no camera attached, the operator page can simulate the shutter. The mock is
deliberately adversarial -- it writes slowly in chunks and can reproduce a stale
file, a duplicate name, and a transfer that stalls -- so the ingest hardening is
exercised before real hardware exists.

## Configuration

`src/Photobooth.Server/appsettings.json`, mirrored by `appsettings.example.json`.
Everything under `Camera:WatchFolder` is an *assumption* about how EOS Utility
behaves that has not yet been checked against a real camera, which is why it is
configuration rather than code.

Relative paths resolve against the app folder, so a published build behaves the
same as `dotnet run`.
