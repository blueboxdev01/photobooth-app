namespace Photobooth.Server;

/// <summary>
/// Throwaway M1 pages, just enough to prove the ingest pipeline end to end.
/// M2 replaces both with the real Vite + React frontend built into wwwroot.
/// </summary>
internal static class Pages
{
    private const string Shared = """
        <style>
          :root { color-scheme: dark; }
          body { margin: 0; background: #14161a; color: #e8eaed;
                 font: 14px/1.5 "Segoe UI", system-ui, sans-serif; }
          header { padding: 14px 20px; border-bottom: 1px solid #2a2e35;
                   display: flex; gap: 16px; align-items: baseline; }
          h1 { font-size: 15px; margin: 0; font-weight: 600; }
          main { padding: 20px; }
          code { font-family: Consolas, monospace; color: #9fd0ff; }
          .pill { font-size: 12px; padding: 2px 9px; border-radius: 999px;
                  border: 1px solid #3a4049; }
          .Ready { color: #7ee08a; border-color: #2f6b39; }
          .Faulted, .Disconnected { color: #ff9a8b; border-color: #7a3a33; }
          .grid { display: grid; gap: 14px;
                  grid-template-columns: repeat(auto-fill, minmax(230px, 1fr)); }
          figure { margin: 0; background: #1c1f25; border: 1px solid #2a2e35;
                   border-radius: 8px; overflow: hidden; }
          figure img { width: 100%; display: block; aspect-ratio: 3/2;
                       object-fit: cover; background: #0d0f12; }
          figcaption { padding: 7px 10px; font-size: 12px; color: #9aa3ad; }
          button { font: inherit; padding: 7px 13px; border-radius: 6px;
                   border: 1px solid #3a4049; background: #232830; color: #e8eaed;
                   cursor: pointer; }
          button:hover { background: #2c323c; }
          .row { display: flex; gap: 9px; flex-wrap: wrap; margin-bottom: 18px; }
          .muted { color: #8b939d; }
          .empty { color: #8b939d; padding: 40px 0; text-align: center; }
        </style>
        """;

    public const string Operator = $$"""
        <!doctype html><meta charset="utf-8"><title>Photobooth - Operator</title>
        {{Shared}}
        <header>
          <h1>Operator</h1>
          <span id="status" class="pill">...</span>
          <span class="muted">M1 skeleton - mock camera</span>
        </header>
        <main>
          <p class="muted">Watch folder: <code id="folder">...</code></p>
          <div class="row">
            <button onclick="press('Normal')">Simulate press</button>
            <button onclick="press('DuplicateName')">Duplicate name</button>
            <button onclick="press('Stale')">Stale file</button>
            <button onclick="press('NeverFinishes')">Never finishes</button>
            <button onclick="reset()">Start new session</button>
          </div>
          <p class="muted" id="msg"></p>
          <div class="grid" id="grid"></div>
          <p class="empty" id="empty">No photos accepted yet.</p>
        </main>
        <script>
          async function press(mode) {
            const r = await fetch('/api/mock/press?mode=' + mode, { method: 'POST' });
            const j = await r.json();
            document.getElementById('msg').textContent =
              r.ok ? `wrote ${j.file} (${j.mode})` : `error: ${j.error}`;
          }
          async function reset() {
            await fetch('/api/session/reset', { method: 'POST' });
            document.getElementById('msg').textContent =
              'new session - files already in the folder are now stale';
          }
          {{Poll}}
        </script>
        """;

    public const string Display = $$"""
        <!doctype html><meta charset="utf-8"><title>Photobooth</title>
        {{Shared}}
        <header><h1>Display</h1><span id="status" class="pill">...</span></header>
        <main>
          <div class="grid" id="grid"></div>
          <p class="empty" id="empty">Waiting for photos...</p>
          <p class="muted" hidden><code id="folder"></code></p>
        </main>
        <script>{{Poll}}</script>
        """;

    // Polling is a deliberate M1 shortcut. M2 swaps it for SignalR so both
    // windows share one authoritative session state instead of racing.
    private const string Poll = """
        async function tick() {
          try {
            const s = await (await fetch('/api/state')).json();
            const badge = document.getElementById('status');
            badge.textContent = s.camera.status;
            badge.className = 'pill ' + s.camera.status;
            const folder = document.getElementById('folder');
            if (folder) folder.textContent = s.camera.watchFolder;

            const grid = document.getElementById('grid');
            const empty = document.getElementById('empty');
            empty.hidden = s.photos.length > 0;
            grid.innerHTML = s.photos.map(p => `
              <figure>
                <img src="${p.url}" alt="${p.fileName}">
                <figcaption>${p.fileName} &middot; ${Math.round(p.sizeBytes / 1024)} KB</figcaption>
              </figure>`).join('');
          } catch (e) {
            /* server restarting; the next tick will pick it up */
          }
        }
        tick();
        setInterval(tick, 750);
        """;
}
