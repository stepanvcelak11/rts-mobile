#!/usr/bin/env python3
"""Regenerates the balance data files under Assets/_Project/Resources/Data from one table.

Edit the numbers here and run `python tools/gen_data.py`, then `python tools/validate_data.py`.
Keeping the source of truth in Python makes balance passes diffable and keeps every file
consistent (a unit added here is automatically listed by its building).
"""
import json
import os

ROOT = os.path.join(os.path.dirname(__file__), "..", "Assets", "_Project", "Resources", "Data")


def dump(rel, obj):
    path = os.path.join(ROOT, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(obj, f, indent=2)
        f.write("\n")


def unit(id, name, civ, age, at, cost, train, pop, batch, hp, speed, los, armor, size, attacks, tags, aggro, leash, flee, gather=None):
    slug = id.split(".")[1]
    return {
        "id": id, "name": name, "civ": civ, "age": age, "trainedAt": at, "cost": cost, "trainSeconds": train,
        "population": pop, "batchSize": batch,
        "stats": {"hp": hp, "speed": speed, "los": los, "armor": armor, "sizeClass": size},
        "attacks": attacks, "tags": tags,
        "behaviour": {"canGather": gather is not None, "canBuild": gather is not None, "aggro": aggro, "leashRange": leash, "fleeHpPercent": flee},
        "gather": gather,
        "view": {"prefab": "Units/" + name.replace(" ", ""), "icon": "ui/units/" + slug, "scale": 1.0, "voiceSet": "vo." + slug},
    }


def ranged(dmg, rng, cd, proj, mult):
    return {"id": "ranged", "damage": dmg, "type": "ranged", "range": rng, "minRange": 0, "cooldownSeconds": cd, "projectile": proj, "multipliers": mult}


def melee(dmg, cd, mult):
    return {"id": "melee", "damage": dmg, "type": "melee", "range": 1, "cooldownSeconds": cd, "multipliers": mult}


NO_ARMOR = {"melee": 0, "ranged": 0, "siege": 0}

COMMON_UNITS = [
    unit("unit.villager", "Villager", "common", "age.1", ["bld.towncenter"], {"food": 100}, 25, 1, 1, 120, 4.0, 10, NO_ARMOR, "small",
         [melee(8, 1.5, {})], ["tag.villager"], "passive", 0, 60, gather={"allowed": ["res.*"], "rateMultiplier": 1.0}),
    unit("unit.musketeer", "Musketeer", "common", "age.2", ["bld.barracks"], {"food": 75, "gold": 25}, 30, 1, 5, 150, 4.0, 12,
         {"melee": 0.2, "ranged": 0, "siege": 0}, "small",
         [ranged(23, 12, 3.0, "proj.bullet", {"tag.villager": 1.5}), melee(13, 1.5, {"tag.cavalry": 3.0})],
         ["tag.infantry", "tag.gunpowder"], "defensive", 8, 0),
    unit("unit.crossbowman", "Crossbowman", "common", "age.2", ["bld.barracks"], {"food": 40, "wood": 40}, 25, 1, 5, 100, 4.0, 14,
         {"melee": 0, "ranged": 0.2, "siege": 0}, "small",
         [ranged(15, 14, 1.5, "proj.bolt", {"tag.infantry": 1.5, "tag.villager": 1.5}), melee(6, 1.5, {})],
         ["tag.infantry", "tag.ranged"], "defensive", 8, 0),
    unit("unit.pikeman", "Pikeman", "common", "age.2", ["bld.barracks"], {"food": 40, "wood": 40}, 25, 1, 5, 120, 4.5, 10,
         {"melee": 0.1, "ranged": 0.1, "siege": 0}, "small",
         [melee(8, 1.5, {"tag.cavalry": 5.0, "tag.building": 3.0})], ["tag.infantry", "tag.melee"], "defensive", 8, 0),
    unit("unit.hussar", "Hussar", "common", "age.2", ["bld.stable"], {"food": 120, "gold": 80}, 40, 2, 3, 320, 6.75, 12,
         {"melee": 0.2, "ranged": 0.2, "siege": 0}, "medium",
         [melee(30, 1.5, {"tag.villager": 2.0, "tag.artillery": 3.0, "tag.ranged": 1.5})], ["tag.cavalry", "tag.melee"], "aggressive", 12, 0),
    unit("unit.falconet", "Falconet", "common", "age.3", ["bld.foundry"], {"wood": 100, "gold": 300}, 50, 3, 1, 200, 3.0, 20,
         {"melee": 0, "ranged": 0.5, "siege": 0}, "large",
         [{"id": "cannon", "damage": 100, "type": "siege", "range": 20, "minRange": 3, "cooldownSeconds": 4.0, "projectile": "proj.cannonball",
           "multipliers": {"tag.infantry": 0.5, "tag.cavalry": 0.5, "tag.building": 2.0}}],
         ["tag.artillery"], "defensive", 6, 0),
]

UNIQUE_UNITS = [
    unit("unit.redcoat", "Redcoat", "civ.crown", "age.2", [], {"food": 80, "gold": 25}, 30, 1, 5, 170, 4.0, 12,
         {"melee": 0.2, "ranged": 0, "siege": 0}, "small",
         [ranged(24, 12, 3.0, "proj.bullet", {"tag.villager": 1.5}), melee(14, 1.5, {"tag.cavalry": 3.0})],
         ["tag.infantry", "tag.gunpowder"], "defensive", 8, 0),
    unit("unit.uhlan", "Uhlan", "civ.compact", "age.2", [], {"food": 100, "gold": 70}, 38, 2, 3, 240, 7.25, 12,
         {"melee": 0.1, "ranged": 0.1, "siege": 0}, "medium",
         [melee(26, 1.5, {"tag.villager": 2.0, "tag.artillery": 3.0, "tag.ranged": 1.5})], ["tag.cavalry", "tag.melee"], "aggressive", 14, 0),
    unit("unit.jaguar", "Jaguar Warrior", "civ.sun", "age.2", [], {"food": 60, "gold": 30}, 28, 1, 5, 180, 5.0, 10,
         {"melee": 0.2, "ranged": 0.1, "siege": 0}, "small",
         [melee(14, 1.5, {"tag.infantry": 2.0, "tag.cavalry": 1.5, "tag.building": 2.0})], ["tag.infantry", "tag.melee"], "defensive", 9, 0),
]


def bld(id, name, age, cost, secs, fp, hp, los, terrain, trains, researches, dropoff, pop, attack, garrison, limit, slots):
    slug = id.split(".")[1]
    return {
        "id": id, "name": name, "civ": "common", "age": age, "cost": cost, "buildSeconds": secs, "footprint": fp,
        "stats": {"hp": hp, "armor": {"siege": -0.2, "ranged": 0.6, "melee": 0.6}, "los": los},
        "placement": {"terrain": terrain, "minDistanceToEnemyBuilding": 0, "requiresNear": None},
        "trains": trains, "researches": researches, "dropOff": dropoff, "populationProvided": pop, "attack": attack,
        "garrison": garrison, "limit": limit, "queue": {"slots": slots, "parallel": 1 if slots else 0},
        "view": {"prefab": "Buildings/" + name.replace(" ", ""), "icon": "ui/bld/" + slug,
                 "constructionPrefab": "Buildings/_Scaffold%dx%d" % (fp[0], fp[1])},
    }


G3 = ["grass", "dirt", "sand"]
G2 = ["grass", "dirt"]
TC_ATTACK = {"damage": 30, "type": "ranged", "range": 14, "cooldownSeconds": 2.5, "garrisonBonus": 0.1}
TOWER_ATTACK = {"damage": 25, "type": "ranged", "range": 12, "cooldownSeconds": 2.0, "garrisonBonus": 0.1}

BUILDINGS = [
    bld("bld.towncenter", "Town Center", "age.1", {"wood": 600}, 150, [6, 6], 7200, 16, G3, ["unit.villager"],
        ["tech.hunting_dogs", "tech.gang_saw", "tech.placer_mines"], ["food", "wood", "gold"], 10, TC_ATTACK, 20, 1, 5),
    bld("bld.house", "House", "age.1", {"wood": 100}, 20, [2, 2], 800, 6, G3, [], [], [], 10, None, 0, 20, 0),
    bld("bld.mill", "Mill", "age.1", {"wood": 400}, 40, [3, 3], 1500, 6, G2, [], [], ["food"], 0, None, 0, 0, 0),
    bld("bld.tower", "Outpost", "age.1", {"wood": 250}, 45, [2, 2], 1500, 14, G3, [], [], [], 0, TOWER_ATTACK, 5, 5, 0),
    bld("bld.barracks", "Barracks", "age.2", {"wood": 200}, 45, [4, 4], 2000, 10, G2, ["unit.musketeer", "unit.crossbowman", "unit.pikeman"],
        ["tech.veteran_infantry"], [], 0, None, 0, 0, 5),
    bld("bld.stable", "Stable", "age.2", {"wood": 200}, 45, [4, 4], 2000, 10, G2, ["unit.hussar"], ["tech.veteran_cavalry"], [], 0, None, 0, 0, 5),
    bld("bld.foundry", "Artillery Foundry", "age.3", {"wood": 300}, 60, [4, 4], 2500, 10, G2, ["unit.falconet"], [], [], 0, None, 0, 0, 5),
]


def mod(target, stat, op, value):
    return {"target": target, "stat": stat, "op": op, "value": value}


def tech(id, name, age, at, cost, secs, effects, pre=()):
    return {"id": id, "name": name, "kind": "tech", "age": age, "researchedAt": at, "cost": cost, "researchSeconds": secs,
            "effects": effects, "spawns": [], "prerequisites": list(pre), "oncePerGame": True}


def ship(id, name, age, effects=(), spawns=(), once=False):
    return {"id": id, "name": name, "kind": "shipment", "age": age, "researchedAt": [], "cost": {}, "researchSeconds": 0,
            "effects": list(effects), "spawns": list(spawns), "prerequisites": [], "oncePerGame": once}


TECHS = [
    tech("tech.hunting_dogs", "Hunting Dogs", "age.1", ["bld.towncenter"], {"food": 100}, 30,
         [mod("*", "gather.res.hunt", "mul", 1.10), mod("*", "gather.res.berries", "mul", 1.10)]),
    tech("tech.gang_saw", "Gang Saw", "age.2", ["bld.towncenter"], {"wood": 150, "gold": 100}, 40, [mod("*", "gather.res.tree", "mul", 1.15)]),
    tech("tech.placer_mines", "Placer Mines", "age.2", ["bld.towncenter"], {"wood": 200}, 40, [mod("*", "gather.res.mine", "mul", 1.15)]),
    tech("tech.veteran_infantry", "Veteran Infantry", "age.3", ["bld.barracks"], {"wood": 200, "gold": 200}, 40,
         [mod("tag.infantry", "hp", "mul", 1.2), mod("tag.infantry", "attacks.*.damage", "mul", 1.2)]),
    tech("tech.veteran_cavalry", "Veteran Cavalry", "age.3", ["bld.stable"], {"wood": 200, "gold": 200}, 40,
         [mod("tag.cavalry", "hp", "mul", 1.2), mod("tag.cavalry", "attacks.*.damage", "mul", 1.2)]),
    ship("ship.3_villagers", "3 Villagers", "age.1", spawns=[{"id": "unit.villager", "count": 3}]),
    ship("ship.4_villagers", "4 Villagers", "age.1", spawns=[{"id": "unit.villager", "count": 4}], once=True),
    ship("ship.700_wood", "700 Wood", "age.2", effects=[mod("player", "stockpile.wood", "add", 700)]),
    ship("ship.700_food", "700 Food", "age.2", effects=[mod("player", "stockpile.food", "add", 700)]),
    ship("ship.600_gold", "600 Gold", "age.2", effects=[mod("player", "stockpile.gold", "add", 600)]),
    ship("ship.1000_wood", "1000 Wood", "age.3", effects=[mod("player", "stockpile.wood", "add", 1000)]),
    ship("ship.5_musketeers", "5 Musketeers", "age.2", spawns=[{"id": "unit.musketeer", "count": 5}]),
    ship("ship.8_pikemen", "8 Pikemen", "age.2", spawns=[{"id": "unit.pikeman", "count": 8}]),
    ship("ship.4_hussars", "4 Hussars", "age.2", spawns=[{"id": "unit.hussar", "count": 4}]),
    ship("ship.2_falconets", "2 Falconets", "age.3", spawns=[{"id": "unit.falconet", "count": 2}]),
    ship("ship.outpost_boost", "Fortified Outposts", "age.2",
         effects=[mod("bld.tower", "hp", "mul", 1.5), mod("bld.tower", "attacks.*.damage", "mul", 1.25)], once=True),
]

CIVS = {
    "crown": {
        "id": "civ.crown", "name": "The Crown", "tagline": "Line infantry and cheap shipments", "color": "#3B82F6",
        "startingUnits": [{"id": "unit.villager", "count": 6}], "startingStockpile": {"food": 200, "wood": 200, "gold": 100},
        "uniqueUnits": ["unit.redcoat"], "uniqueBuildings": [], "replacements": {"unit.musketeer": "unit.redcoat"},
        "modifiers": [mod("tag.infantry", "hp", "mul", 1.10), mod("bld.house", "cost.wood", "mul", 0.80), mod("shipment", "xpCost", "mul", 0.90)],
        "homeCity": {"xpPerShipmentBase": 300, "xpGrowth": 1.25,
                     "deck": ["ship.3_villagers", "ship.700_wood", "ship.700_food", "ship.5_musketeers", "ship.600_gold", "ship.2_falconets"], "deckSize": 8},
        "ai": {"buildOrder": "bo.crown_boom", "preferredComp": ["unit.musketeer", "unit.hussar", "unit.crossbowman"]},
    },
    "compact": {
        "id": "civ.compact", "name": "Iron Compact", "tagline": "Fast cavalry and rich forests", "color": "#DC2626",
        "startingUnits": [{"id": "unit.villager", "count": 6}], "startingStockpile": {"food": 200, "wood": 250, "gold": 100},
        "uniqueUnits": ["unit.uhlan"], "uniqueBuildings": [], "replacements": {"unit.hussar": "unit.uhlan"},
        "modifiers": [mod("tag.cavalry", "attacks.*.damage", "mul", 1.10), mod("bld.stable", "cost.wood", "mul", 0.75), mod("*", "gather.res.tree", "mul", 1.10)],
        "homeCity": {"xpPerShipmentBase": 300, "xpGrowth": 1.25,
                     "deck": ["ship.3_villagers", "ship.700_wood", "ship.4_hussars", "ship.600_gold", "ship.8_pikemen", "ship.2_falconets"], "deckSize": 8},
        "ai": {"buildOrder": "bo.compact_rush", "preferredComp": ["unit.hussar", "unit.pikeman", "unit.musketeer"]},
    },
    "sun": {
        "id": "civ.sun", "name": "Sun Empire", "tagline": "Swift villagers and jaguar warriors", "color": "#F59E0B",
        "startingUnits": [{"id": "unit.villager", "count": 7}], "startingStockpile": {"food": 250, "wood": 200, "gold": 50},
        "uniqueUnits": ["unit.jaguar"], "uniqueBuildings": [], "replacements": {"unit.pikeman": "unit.jaguar"},
        "modifiers": [mod("tag.villager", "speed", "mul", 1.15), mod("*", "gather.res.berries", "mul", 1.15),
                      mod("*", "gather.res.hunt", "mul", 1.15), mod("bld.barracks", "cost.wood", "mul", 0.80)],
        "homeCity": {"xpPerShipmentBase": 300, "xpGrowth": 1.25,
                     "deck": ["ship.4_villagers", "ship.700_food", "ship.700_wood", "ship.8_pikemen", "ship.600_gold", "ship.outpost_boost", "ship.1000_wood"], "deckSize": 8},
        "ai": {"buildOrder": "bo.sun_boom", "preferredComp": ["unit.pikeman", "unit.crossbowman", "unit.hussar"]},
    },
}


def main():
    dump("units/common.json", {"items": COMMON_UNITS})
    dump("units/unique.json", {"items": UNIQUE_UNITS})
    dump("buildings/common.json", {"items": BUILDINGS})
    dump("techs/common.json", {"items": TECHS})
    for key, civ in CIVS.items():
        dump("civs/%s.json" % key, civ)
    print("data written: %d units, %d buildings, %d techs/shipments, %d civs" % (
        len(COMMON_UNITS) + len(UNIQUE_UNITS), len(BUILDINGS), len(TECHS), len(CIVS)))


if __name__ == "__main__":
    main()
