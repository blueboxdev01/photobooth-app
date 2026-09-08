# Setting up Google Drive delivery

Fifteen minutes, once. After this the booth uploads each finished session to its
own Drive folder and shows the guest a QR code pointing at it.

**You do not need this to run the app.** With no Google account configured,
sessions are saved to the output folder and the guest screen says "ask us for
your photos". That is how the field-test build ships.

## Two settings decide whether this works

Most of the ways this pattern fails come down to two choices, both easy to get
wrong and neither of which reports a useful error afterwards:

| | |
|---|---|
| **Use an OAuth client, not a service account** | Service accounts have **no storage quota of their own** and cannot upload to a personal Drive at all. Uploads fail outright |
| **Publish the consent screen** | Left in *Testing*, Google revokes the refresh token after **7 days**. The booth then stops uploading, silently, about once a week |

The rest is routine.

## 1. A dedicated booth account

Make a **new Google account** for the booth. Not your personal one.

- The free **15 GB is shared with that account's Gmail and Photos**. At roughly
  30 MB per session that is around **500 sessions** — one busy event is nowhere
  near it, a year of events might be.
- Guest photos should not sit in the same Drive as your own things.
- If someone else ever runs the booth, you hand over one account rather than your
  whole Google identity.

## 2. Create the OAuth client

Signed in as the booth account, at <https://console.cloud.google.com>:

1. **Create a project.** Any name; "Photobooth" is fine.
2. **APIs & Services → Library → Google Drive API → Enable.**
3. **The consent screen.** Google has renamed this: look for **Google Auth
   Platform** in the sidebar, or **APIs & Services → OAuth consent screen** in
   the older layout.
   - User type / audience: **External**.
   - Fill in the app name, your support email, and the developer contact. Nothing
     else is required.
   - Scopes: add **`.../auth/drive.file`** and nothing more. This is the scope
     that only reaches files the app itself created — it cannot see the rest of
     the account's Drive, and because it is **non-sensitive** Google needs no
     verification and no security assessment.
   - **Then press Publish app**, on the **Audience** page in the new layout, so
     the status reads **In production**.

   > **Do not skip that last step, and do not work around it by adding yourself
   > as a test user.** While the app is in *Testing*, signing in fails outright
   > with *"has not completed the Google verification process"* — and even once
   > you add a tester to get past that, **Google revokes the refresh token after
   > seven days**, so the booth would stop uploading roughly once a week with no
   > warning. Publishing costs nothing and takes effect immediately, because the
   > scope is non-sensitive. There is no review to wait for and the app never
   > needs "verifying".
4. **APIs & Services → Credentials → Create credentials → OAuth client ID.**
   - Application type: **Desktop app**.
   - Copy the **client ID** and **client secret**.

## 3. Tell the booth

Next to `Photobooth.Server.exe`, create a file called **`appsettings.Local.json`**:

```json
{
  "Delivery": {
    "Drive": {
      "Enabled": true,
      "ClientId": "PASTE-THE-CLIENT-ID",
      "ClientSecret": "PASTE-THE-CLIENT-SECRET"
    }
  }
}
```

`appsettings.Local.example.json` in the repo is a copy of this to start from.

This file is **gitignored and never committed**. A desktop client secret is not
truly secret — it ships inside every installed app — but it still has no business
in a public repo.

Restart the app.

## 4. Sign in

Open **Setup → Guest delivery** and press **Sign in**. A browser window opens;
choose the booth account and accept.

Google will warn that the app is not verified. That is expected for a
`drive.file` desktop app and is not a problem — press *Advanced → Go to
Photobooth*.

The page should then show the account address and *Uploading: On*.

The refresh token is stored in `data/drive-token-booth.bin`, **encrypted with
DPAPI** — readable only by your Windows user on that machine, and useless if the
file is copied off. Copy `data/` to a new laptop and you will simply be asked to
sign in again.

## Optional: put sessions inside a folder

By default each session folder is created in the root of My Drive. To keep them
together, make a folder in Drive, open it, and copy the id out of the address bar
(`.../folders/THIS-PART`), then add:

```json
"ParentFolderId": "THIS-PART"
```

next to `ClientId` in the same file.

## Checking it works

Run a session with the mock camera and watch **Setup → Guest delivery**. *Waiting*
should go to 1 and back to 0, and the guest screen should show a QR.

**Scan it from a phone on mobile data, not your wifi.** A link that only works on
the booth's own network is the classic way this looks fine in the kitchen and
fails at the venue.

## When it goes wrong

| What you see | What it means |
|---|---|
| **Access blocked: … has not completed the Google verification process** (403 `access_denied`), at sign-in | The consent screen is still in *Testing*. Go to **Audience → Publish app** so it reads *In production*, then try again. Adding yourself as a test user also gets past this screen, but leaves you with the seven-day token expiry above |
| *Not signed in — nothing is being uploaded* | No token, or Google revoked it. Press **Re-authorise**. If this comes back every week, the consent screen is still in *Testing* |
| *The booth's Google account is out of storage* | The 15 GB is full. Not retried, because retrying cannot fix it. Clear space or upgrade |
| Sessions sitting in *Waiting* | No network. They retry on their own with a widening gap and go when the connection returns |
| A session in the failed list | Gave up after several tries. The photos are safe in the output folder; press **Try again** |

**Nothing here can lose a guest's photos.** The strip and the raws are written to
disk before an upload is ever attempted, and the record of what happened lives in
that same folder as `session.json`. A session that never uploaded can be
published later from Setup, days after the event.

## Privacy worth deciding once

The Drive folder is shared **anyone with the link → viewer**. That is the normal
photobooth bargain: a guest can forward it to their family, and the folder id is
random and unguessable so nobody can reach it by editing a URL.

It does mean guest photos exist in two places — a cloud account behind a
shareable link, and your laptop. Don't put guest names in folder names, and pick
one retention period that covers both, including any backup you take off the
laptop after an event.
