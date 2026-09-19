#!/usr/bin/env python3
"""Validate Assets/_Project/Resources/Data/**.json against the rules in docs/02-DATA-SCHEMA.md.

Runs without Unity (CI + pre-commit). Exit code 1 on any error.
"""
import glob
import json
import os
import sys
from decimal import Decimal

ROOT = os.path.join(os.path.dirname(__file__), "..", "Assets", "_Project", "Resources", "Data")
errors: list[str] = []


def err(msg: str) -> None:
    errors.append(msg)


def load(path: str):
    # parse_float=Decimal keeps the exact literal so we can check the ≤3-decimals rule
    with open(path, encoding="utf-8") as f:
        return json.load(f, parse_float=Decimal)


def walk_numbers(node, where: str):
    if isinstance(node, Decimal):
        if -node.as_tuple().exponent > 3:
            err(f"{where}: number {node} has more than 3 decimals (fixed-point safety)")
    elif isinstance(node, dict):
        for k, v in node.items():
            walk_numbers(v, f"{where}.{k}")
    elif isinstance(node, list):
        for i, v in enumerate(node):
            walk_numbers(v, f"{where}[{i}]")


def items_of(kind: str) -> list[dict]:
    out = []
    for path in sorted(glob.glob(os.path.join(ROOT, kind, "*.json"))):
        data = load(path)
        walk_numbers(data, os.path.relpath(path, ROOT))
        arr = data["items"] if isinstance(data, dict) and "items" in data else [data]
        for it in arr:
            it["_file"] = os.path.relpath(path, ROOT)
            out.append(it)
    return out


def main() -> int:
    economy = load(os.path.join(ROOT, "economy.json"))
    ages = load(os.path.join(ROOT, "ages.json"))["items"]
    walk_numbers(economy, "economy.json")
    units = items_of("units")
    buildings = items_of("buildings")
    civs = items_of("civs")
    techs = items_of("techs")

    resources = set(economy["resources"])
    age_rank = {a["id"]: i for i, a in enumerate(ages)}
    ids: dict[str, str] = {}
    for group, prefix in ((units, "unit."), (buildings, "bld."), (civs, "civ."), (techs, ("tech.", "ship."))):
        for it in group:
            i = it.get("id", "")
            if not i.startswith(prefix):
                err(f"{it['_file']}: id '{i}' must start with {prefix}")
            if i in ids:
                err(f"{it['_file']}: id '{i}' already defined in {ids[i]}")
            ids[i] = it["_file"]
    for node in economy["resourceNodes"]:
        ids[node["id"]] = "economy.json"
    for a in ages:
        ids[a["id"]] = "ages.json"

    def ref(owner: dict, key: str, target: str, allow_glob: bool = False):
        if target in ids or (allow_glob and target.endswith(".*")):
            return
        err(f"{owner['_file']} {owner['id']}: {key} references unknown id '{target}'")

    def check_cost(owner: dict, cost: dict, key: str = "cost"):
        for r in cost:
            if r not in resources:
                err(f"{owner['_file']} {owner['id']}: {key} uses unknown resource '{r}'")

    bld_by_id = {b["id"]: b for b in buildings}
    for u in units:
        check_cost(u, u.get("cost", {}))
        if u["age"] not in age_rank:
            err(f"{u['_file']} {u['id']}: unknown age {u['age']}")
        for b in u.get("trainedAt", []):
            ref(u, "trainedAt", b)
            if b in bld_by_id and age_rank.get(u["age"], 0) < age_rank.get(bld_by_id[b]["age"], 0):
                err(f"{u['_file']} {u['id']}: age earlier than its building {b}")
            if b in bld_by_id and u["id"] not in bld_by_id[b].get("trains", []):
                err(f"{u['_file']} {u['id']}: trainedAt {b} but {b}.trains does not list it")
        if u.get("gather"):
            for g in u["gather"]["allowed"]:
                ref(u, "gather.allowed", g, allow_glob=True)
        if not u.get("attacks"):
            err(f"{u['_file']} {u['id']}: needs at least one attack (even villagers)")
    for b in buildings:
        check_cost(b, b.get("cost", {}))
        fp = b.get("footprint", [0, 0])
        if len(fp) != 2 or min(fp) < 1:
            err(f"{b['_file']} {b['id']}: footprint must be [w,h] ≥ [1,1]")
        for t in b.get("trains", []):
            ref(b, "trains", t)
        for t in b.get("researches", []):
            ref(b, "researches", t)
        for r in b.get("dropOff", []):
            if r not in resources:
                err(f"{b['_file']} {b['id']}: dropOff unknown resource '{r}'")
    unit_by_id = {u["id"]: u for u in units}
    for c in civs:
        check_cost(c, c.get("startingStockpile", {}), "startingStockpile")
        for s in c.get("startingUnits", []):
            ref(c, "startingUnits", s["id"])
        for k, v in c.get("replacements", {}).items():
            ref(c, "replacements", k)
            ref(c, "replacements", v)
            if v in unit_by_id and unit_by_id[v].get("civ") != c["id"]:
                err(f"{c['_file']} {c['id']}: replacement {v} must have civ = {c['id']}")
        for s in c.get("homeCity", {}).get("deck", []):
            ref(c, "homeCity.deck", s)
        for m in c.get("modifiers", []):
            if m.get("op") not in ("mul", "add", "set"):
                err(f"{c['_file']} {c['id']}: modifier op must be mul|add|set")
    for t in techs:
        check_cost(t, t.get("cost", {}))
        for b in t.get("researchedAt", []):
            ref(t, "researchedAt", b)
        for s in t.get("spawns", []):
            ref(t, "spawns", s["id"])
        for p in t.get("prerequisites", []):
            ref(t, "prerequisites", p)

    maps = items_of("maps")
    node_ids = {n["id"] for n in economy["resourceNodes"]}
    for m in maps:
        w, h = m["size"]
        for n in m.get("nodes", []):
            if n["id"] not in node_ids:
                err(f"{m['_file']} {m['id']}: node '{n['id']}' unknown")
            if not (0 <= n["x"] < w and 0 <= n["y"] < h):
                err(f"{m['_file']} {m['id']}: node at ({n['x']},{n['y']}) outside {w}x{h}")
        if len(m.get("starts", [])) < m.get("players", 2):
            err(f"{m['_file']} {m['id']}: fewer starts than players")

    if errors:
        print("\n".join(errors))
        print(f"\n{len(errors)} error(s)")
        return 1
    print(f"data ok: {len(units)} units, {len(buildings)} buildings, {len(civs)} civs, {len(techs)} techs, {len(maps)} maps")
    return 0


if __name__ == "__main__":
    sys.exit(main())
