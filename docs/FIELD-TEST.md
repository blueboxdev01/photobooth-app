# Field test — Canon EOS R50

Thanks for running this. The app has been built entirely **without a camera**, so
everything below is an assumption until you check it. Contradicting us is the
point of the exercise — a "that's wrong" is a more useful result than a pass.

Please work top to bottom and record the answers. **Test 1 is the important one**;
if it fails, say so and carry on with the rest, because nothing else depends on
it.

---

## What you need

| | |
|---|---|
| Camera | Canon EOS R50 |
| Software | Canon **EOS Utility** (free from Canon's support site, using the camera's serial) |
| Power | **DR-E18 / LP-E17 dummy battery.** The R50 will not power itself over USB-C while tethered, and a real battery dies in under an hour |
| Cable | Short, good USB-C. The R50 is USB 2.0 despite the connector |
| Trigger | **BR-E1** Bluetooth remote (or a JJC clone) |
| Second camera | Any **webcam** — this is the guest-facing posing mirror, and is *not* used to take photos |

## Setting up

1. Unzip the release and run `Photobooth.Server.exe`. Nothing to install — no
   .NET, no Node. Windows SmartScreen may warn about an unsigned binary; choose
   *More info → Run anyway*.
2. Open <http://localhost:5000/operator>. Everything else is reachable from the
   left-hand rail: **Session**, **Templates** and **Setup**. The rail also has a
   light/dark switch at the bottom -- a booth is set up in daylight and run in a
   dark room, so use whichever is easier on your eyes.
3. On the camera: `Settings → USB Connection App → Photo Import/Remote Control`.
   Set manual exposure, **manual white balance** (not AWB), fixed ISO,
   **JPEG only** (not RAW), auto power-off disabled.
4. Start EOS Utility and choose remote shooting. Note the folder it saves to.
5. Go to **Setup** and paste that folder into **Watch folder**, press **Check**,
   then **Save watch folder**. It takes effect immediately.
6. Set an **Output folder** too -- somewhere findable such as
   `C:\Users\you\Pictures\PhotoboothSessions`. Finished sessions are filed there,
   one folder per guest. It must be a different folder from the watch one.
7. Still on **Setup**, pick your webcam under **Cameras -> Preview**.

> You no longer need to make EOS Utility save anywhere in particular — point the
> app at wherever it already saves.

> **Nothing uploads anywhere.** Google Drive is not in this build. Photos stay in
> the `data` folder next to the .exe.

---

## Test 1 — Does the BR-E1 remote work while USB-tethered?

**This is the one unknown that could change the design.** Canon's own tethering
advice is to disable Bluetooth *before* connecting USB, which is the opposite of
what the remote needs. We could not determine whether they coexist.

1. Pair the BR-E1 (`Wireless features → Connect to Wireless Remote`).
2. Connect USB with EOS Utility in remote-shooting mode.
3. Press the remote.

- [ ] A JPEG appeared in the watch folder → **remote works while tethered**
- [ ] Nothing happened → **they conflict.** Try again using the camera's own
      shutter button and note whether *that* still delivers a file.

**Result:** ______________________________________________

---

## Test 2 — What does EOS Utility actually name the files?

We assumed `IMG_0001.JPG`, incrementing. The diagnostics page shows the real
names under **Watch folder**.

- Actual file names seen: ______________________________
- Column "Watched" says `yes` for them? ______________
- Does it ever write a temporary name first and rename it? ______________

---

## Test 3 — How long from pressing the remote to the file appearing?

We guessed and set a 20-second timeout. Only you can measure the real number.
Once you have it, set the timeout under **Setup -> Timings**.

1. On **Setup**, hit **Mark shutter press** at the same moment you press the
   remote.
2. Repeat about five times.
3. Read off the average and worst figures.

- Average: ________ s  Worst: ________ s

---

## Test 4 — A normal three-shot session

1. On `/operator`, click **Start session**.
2. Take three photos, pressing on "1" of each countdown.
3. Watch `/display` on the second screen as you go.

- [ ] Both screens stayed in step
- [ ] All three photos appeared, right way round, not duplicated
- [ ] The countdown was close enough to be useful

Notes: __________________________________________________

---

## Test 5 — Deliberate breakage

The app is meant to end up in a clear state, never a crash and never a wrong
photo on the strip. For each, note what the operator screen said.

| What to do | Expected | What happened |
|---|---|---|
| Press the remote twice fast | Two distinct photos, no duplicate | |
| Unplug USB mid-session | A visible error, no crash | |
| Close EOS Utility mid-session | Session times out with an explanation | |
| Press nothing for 30s | "No photo after 20s…" message | |
| Click **Retake last** | Exactly one photo dropped | |
| Copy an old JPEG into the watch folder | Ignored as stale | |

---

## Test 6 — The strip

Accept a session and look at the composited 2×6 strip.

- [ ] Three photos, right order, right way up
- [ ] The strip is saved to your **output folder**, in its own subfolder, with
      the raw photos and a `session.json` beside it
- [ ] The originals are **still** in the watch folder, untouched

If the layout is wrong you can fix it yourself. **Setup -> Strip layout** sets
how many photos and the output size (portrait strip or landscape), placing the
slots evenly. **Templates** lets you drag individual slots and re-render. Tell us
what you changed.

---

## Test 7 — Framing and crop *(shapes the strip design)*

The strip's photo slots are **4:3**, but the R50 shoots **3:2**, so the sides get
cropped. The dashed yellow box on the guest screen marks what survives — but it
is **not calibrated** against your camera yet, since the webcam sits beside the
lens rather than on its axis.

1. Stand where a guest would.
2. Compare what the guide box promises against the actual photo.

- Is the guide roughly right, or does it need moving? ______________
- Do people lose their shoulders at normal booth distance? ______________
- Would you rather the slots were 3:2 and the strip had a bigger footer? ______

---

## Test 8 — Both cameras at once

- [ ] Webcam preview runs while the R50 is tethered and shooting
- [ ] Picking a different webcam on **Setup** changes the mirror
- [ ] The preview is mirrored (raise your right hand; it should appear on the
      right side of the screen, like a mirror)

---

## Sending results back

On **Setup**, click **Download diagnostics bundle** and send the ZIP along with
this filled-in sheet.

It contains the logs, every ingest decision with its reason, the settings the
build was running, and the version. **It contains no photographs** — if a
particular photo matters, send that one deliberately.

Also note the version string at the foot of the left-hand rail:

**Version:** ______________________
