#!/usr/bin/env python3
"""Headless smoke test of the web build: boots the engine, starts a match, plays a few
gestures and checks the simulation advances without JavaScript errors.

    python tools/smoke_web.py            # serves web-dist/wwwroot from disk via request routing
    python tools/smoke_web.py https://<user>.github.io/rts-mobile/
"""
import os
import sys
import time

from playwright.sync_api import sync_playwright

URL = sys.argv[1] if len(sys.argv) > 1 else "local"
ROOT = os.path.join(os.path.dirname(__file__), "..", "web-dist", "wwwroot")
MIME = {".wasm": "application/wasm", ".js": "text/javascript", ".json": "application/json", ".html": "text/html",
        ".css": "text/css", ".dat": "application/octet-stream", ".png": "image/png", ".ico": "image/x-icon"}


def serve_from_disk(route):
    """Fulfils requests straight from web-dist (no HTTP server needed; Python's is flaky under Blazor's ~70 parallel fetches)."""
    path = route.request.url.split("://", 1)[1].split("/", 1)[1].split("?")[0] or "index.html"
    full = os.path.join(ROOT, path.replace("/", os.sep))
    if not os.path.isfile(full):
        route.fulfill(status=404, body="")
        return
    with open(full, "rb") as f:
        body = f.read()
    route.fulfill(status=200, body=body, content_type=MIME.get(os.path.splitext(full)[1], "application/octet-stream"))


def main() -> int:
    errors = []
    with sync_playwright() as p:
        browser = p.chromium.launch()
        page = browser.new_page(viewport={"width": 480, "height": 860}, device_scale_factor=2, has_touch=True)
        page.on("pageerror", lambda e: errors.append("pageerror: " + str(e)))
        page.on("console", lambda m: errors.append("console.error: " + m.text) if m.type == "error" else None)
        url = URL
        if URL == "local":
            page.route("http://app.local/**", serve_from_disk)
            url = "http://app.local/"
        t0 = time.time()
        page.goto(url, wait_until="domcontentloaded")
        page.wait_for_function("document.getElementById('playBtn') && !document.getElementById('playBtn').disabled", timeout=120000)
        print(f"engine ready in {time.time() - t0:.1f}s")
        page.screenshot(path="web-dist/smoke-menu.png")
        page.click("#playBtn")
        page.wait_for_selector("#hud:not(.hidden)", timeout=20000)
        page.wait_for_timeout(1500)
        tick1 = page.evaluate("() => JSON.parse(window.__hud || '{}').tick")
        # Drive the game through the same exports the page uses.
        state = page.evaluate("""() => {
            const rt = globalThis.getDotnetRuntime(0);
            return rt.getAssemblyExports('RTS.Web.dll').then(ex => {
                const api = ex.RTS.Web.GameApi; window.__api = api;
                const f = api.Frame(0); return {tick: f[1], count: f[0], w: api.MapWidth(), h: api.MapHeight()};
            });
        }""")
        print("frame:", state)
        assert state["count"] > 50, "expected entities"
        # Select the first villager by tapping its position (from the draw buffer), then a tree.
        found = page.evaluate("""() => {
            const f = window.__api.Frame(0); const n = f[0];
            let v = null, tree = null;
            for (let i = 0; i < n; i++) { const o = 16 + i*12;
                if (f[o] === 1 && f[o+4] === 0 && (f[o+9] & 4) && !v) v = [f[o+2]/64, f[o+3]/64];
                if (f[o] === 3 && f[o+11] === 0 && f[o+4] === -1 && !tree) tree = [f[o+2]/64 + 0.5, f[o+3]/64 + 0.5]; }
            window.__api.Tap(v[0], v[1], 0.6);
            window.__api.Tap(tree[0], tree[1], 0.6);
            return {v, tree};
        }""")
        print("tapped villager + tree:", found)
        page.wait_for_timeout(4000)
        hud = page.evaluate("() => JSON.parse(window.__api.HudJson())")
        print("hud label:", hud["label"], "| food", hud["food"], "wood", hud["wood"])
        f2 = page.evaluate("() => { const f = window.__api.Frame(0); return f[1]; }")
        print("tick after 5.5 s:", f2)
        assert f2 > 60, "simulation should have advanced ~100 ticks"
        # Real touch gestures on the canvas: pan and tap.
        page.touchscreen.tap(240, 400)
        page.wait_for_timeout(300)
        page.mouse.move(240, 500); page.mouse.down(); page.mouse.move(300, 420, steps=8); page.mouse.up()
        page.wait_for_timeout(500)
        page.screenshot(path="web-dist/smoke-game.png")
        # Let it run a while with the AI to catch runtime exceptions.
        page.wait_for_timeout(8000)
        f3 = page.evaluate("() => window.__api.Frame(0)[1]")
        print("tick after run:", f3)
        fps = page.evaluate("""() => new Promise(r => { let n = 0; const t0 = performance.now();
            function step() { n++; if (performance.now() - t0 < 2000) requestAnimationFrame(step); else r(n / 2); } requestAnimationFrame(step); })""")
        print(f"~{fps:.0f} fps (headless)")
        browser.close()
    if errors:
        print("\n".join(errors))
        return 1
    print("smoke ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
