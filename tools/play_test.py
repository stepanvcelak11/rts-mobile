#!/usr/bin/env python3
"""Plays like a person: real touch taps on sprites, then checks that resources actually grow.

    python tools/play_test.py                # local web-dist via request routing
    python tools/play_test.py https://stepanvcelak11.github.io/rts-mobile/
"""
import os
import sys
import time

from playwright.sync_api import sync_playwright

sys.path.insert(0, os.path.dirname(__file__))
from smoke_web import serve_from_disk  # noqa: E402

URL = sys.argv[1] if len(sys.argv) > 1 else "local"


def sprite_center(page, kind, own=None, state=None, avoid=()):
    """Screen centre of the first visible sprite rect of a kind (1 unit, 2 building, 3 node)."""
    return page.evaluate("""([kind, own, state, avoid]) => {
        const R = window.__R; const ents = R.lastEnts || [];
        const cand = R.hitRects.filter(r => r.kind === kind).map(r => ({ r, e: ents.find(e => e.id === r.id) }))
            .filter(({ r, e }) => e && (own === null || e.player === (own ? 0 : -1)) && (state === null || e.state === state)
                && !avoid.includes(e.id) && r.x0 > 4 && r.y0 > 130 && r.x1 < innerWidth - 4 && r.y1 < innerHeight - 250);
        if (!cand.length) return null;
        const { r, e } = cand[0];
        return [(r.x0 + r.x1) / 2, (r.y0 + r.y1) / 2, e.id];
    }""", [kind, own, state, list(avoid)])


def hud(page):
    return page.evaluate("() => JSON.parse(window.__api.HudJson())")


def main() -> int:
    errors = []
    with sync_playwright() as p:
        browser = p.chromium.launch()
        page = browser.new_page(viewport={"width": 412, "height": 915}, device_scale_factor=2.6, has_touch=True, is_mobile=True)
        page.on("pageerror", lambda e: errors.append("pageerror: " + str(e)))
        page.on("console", lambda m: errors.append("console.error: " + m.text) if m.type == "error" else None)
        url = URL
        if URL == "local":
            page.route("http://app.local/**", serve_from_disk)
            url = "http://app.local/"
        page.goto(url, wait_until="domcontentloaded")
        page.wait_for_function("document.getElementById('playBtn') && !document.getElementById('playBtn').disabled", timeout=180000)
        page.tap("#playBtn")
        page.wait_for_selector("#hud:not(.hidden)", timeout=20000)
        page.wait_for_timeout(1500)
        page.evaluate("""() => globalThis.getDotnetRuntime(0).getAssemblyExports('RTS.Web.dll').then(ex => { window.__api = ex.RTS.Web.GameApi; })""")
        page.wait_for_timeout(300)
        h0 = hud(page)
        print("start:", {k: h0[k] for k in ("food", "wood", "gold", "villagers")})

        # 1. Tap a villager sprite (touch), expect it selected.
        v = sprite_center(page, 1, own=True, state=0)
        assert v, "no idle villager sprite on screen"
        page.touchscreen.tap(v[0], v[1])
        page.wait_for_timeout(400)
        label = hud(page)["label"]
        print("after villager tap:", label)
        assert "Villager" in label, "tap on the villager sprite must select it"

        # 2. Tap a tree sprite (touch), expect a gather order and, after 45 s, more wood.
        t = sprite_center(page, 3, own=None, state=0)
        assert t, "no tree sprite on screen"
        page.touchscreen.tap(t[0], t[1])
        page.wait_for_timeout(400)
        label = hud(page)["label"]
        print("after tree tap:", label)
        assert "gather" in label or "move" in label, "villager should head to the tree"
        page.evaluate("() => { for (let i = 0; i < 180; i++) window.__api.Frame(0.25); }")   # 45 s of game time
        page.wait_for_timeout(300)
        h1 = hud(page)
        print("after 45 s:", {k: h1[k] for k in ("food", "wood", "gold")})
        assert h1["wood"] > h0["wood"], "wood must increase after gathering a tree"

        # 3. Same with berries (food) and a second villager, using the "same type" double tap to grab a group.
        v2 = sprite_center(page, 1, own=True, state=0)
        if v2:
            page.touchscreen.tap(v2[0], v2[1]); page.wait_for_timeout(300)
            page.touchscreen.tap(v2[0], v2[1]); page.wait_for_timeout(300)   # second tap = all villagers nearby
            print("group:", hud(page)["label"])
        b = sprite_center(page, 3, own=None, state=1)
        if b:
            page.touchscreen.tap(b[0], b[1]); page.wait_for_timeout(300)
            page.evaluate("() => { for (let i = 0; i < 240; i++) window.__api.Frame(0.25); }")
            h2 = hud(page)
            print("after berries:", {k: h2[k] for k in ("food", "wood", "gold")})
            assert h2["food"] > h1["food"], "food must increase after gathering berries"
        else:
            print("no berries on screen (skipped)")

        # 4. Tap on empty ground clears the selection; a tree tapped with NOTHING selected must still
        #    start gathering (nearest idle villager is sent) - this is the "it does nothing" safety net.
        page.evaluate("() => window.__api.Action('stop', 0)")
        page.evaluate("() => { for (let i = 0; i < 8; i++) window.__api.Frame(0.25); }")
        page.evaluate("() => window.__api.Action('deselect', 0)")
        page.wait_for_timeout(200)
        t2 = sprite_center(page, 3, own=None, state=0)
        assert t2, "no tree sprite on screen for the auto-gather check"
        h3 = hud(page)
        page.touchscreen.tap(t2[0], t2[1]); page.wait_for_timeout(300)
        print("auto-gather label:", hud(page)["label"])
        assert "Villager" in hud(page)["label"], "the nearest idle villager must be selected and sent"
        page.evaluate("() => { for (let i = 0; i < 200; i++) window.__api.Frame(0.25); }")
        h4 = hud(page)
        print("after auto-gather:", {k: h4[k] for k in ("food", "wood", "gold")})
        assert h4["wood"] > h3["wood"], "tapping a tree with nothing selected must send an idle villager"

        # 5. HUD quick action "Gather gold" with villagers selected.
        v3 = sprite_center(page, 1, own=True)
        page.touchscreen.tap(v3[0], v3[1]); page.wait_for_timeout(300)
        acts = hud(page)["actions"]
        gold = next((a for a in acts if a["id"] == "gathernear:2"), None)
        assert gold, "villager selection must offer the Gather gold quick action"
        if gold["enabled"]:
            r = page.evaluate("() => JSON.parse(window.__api.Action('gathernear', 2))")
            assert r.get("gather") == 1, "gathernear must return the node position"
            page.evaluate("() => { for (let i = 0; i < 400; i++) window.__api.Frame(0.25); }")
            h5 = hud(page)
            print("after gather gold:", {k: h5[k] for k in ("food", "wood", "gold")})
            assert h5["gold"] > h4["gold"], "gold must increase after the Gather gold quick action"
        else:
            print("no gold explored yet (skipped)")
        assert page.evaluate("() => document.getElementById('version').textContent").startswith("build ")
        page.screenshot(path="web-dist/play-test.png")
        browser.close()
    if errors:
        print("\n".join(errors))
        return 1
    print("play test ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
