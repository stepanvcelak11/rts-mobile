# 02 — Data schema (JSON source of truth)

All numbers that enter the simulation are **integers or fixed-point
decimals with ≤ 3 decimals** (parsed straight into `Fix64`; no float
round-trip). Times are in **ticks** unless the key ends in `Seconds`
(then converted at load: `seconds × 20`). Distances in **cells** (1 cell = 1 m).

Files: `Assets/_Project/Resources/Data/`
```
economy.json            resources, gather rates, stockpile caps, market
ages.json               Age I–IV, cost, what unlocks
units/<civ|common>.json array of UnitDef
buildings/…json         array of BuildingDef
civs/<id>.json          one CivDef per file
techs/…json             array of TechDef (incl. Home-City shipments)
```
Every file: `{ "$schema": "…/rts-<kind>.schema.json", "items": [...] }` or a
single object for civs. Ids are globally unique strings with a prefix.

---

## Resource & economy — `economy.json`
```jsonc
{
  "resources": ["food", "wood", "gold"],
  "startingStockpile": { "food": 200, "wood": 200, "gold": 100 },
  "stockpileCap": 99999,
  "gatherRates": {                    // resource units per second per villager
    "res.berries":   { "food": 0.67 },
    "res.hunt":      { "food": 0.84 },
    "res.farm":      { "food": 0.50 },
    "res.tree":      { "wood": 0.50 },
    "res.mine":      { "gold": 0.50 }
  },
  "carryCapacity": 10,                // per villager; cargo type is exclusive
  "depositRadius": 1.5,               // cells from drop-off footprint edge
  "resourceNodes": [
    { "id": "res.tree",    "amount": 300,  "footprint": [1,1], "depletes": true,  "regrowSeconds": 0 },
    { "id": "res.berries", "amount": 1000, "footprint": [2,2], "depletes": true },
    { "id": "res.hunt",    "amount": 400,  "footprint": [1,1], "depletes": true, "mobile": true, "fleeRange": 6 },
    { "id": "res.mine",    "amount": 5000, "footprint": [3,3], "depletes": true },
    { "id": "res.farm",    "amount": -1,   "footprint": [3,3], "depletes": false, "requiresBuilding": "bld.mill" }
  ],
  "market": { "enabled": true, "basePrice": 100, "priceStep": 3, "decayPerMinute": 4 },
  "populationCapBase": 10, "populationCapMax": 200
}
```

## Ages — `ages.json`
```jsonc
{ "items": [
  { "id": "age.1", "name": "Discovery",  "cost": {} },
  { "id": "age.2", "name": "Colonial",   "cost": { "food": 800 },              "researchSeconds": 60,  "at": "bld.towncenter" },
  { "id": "age.3", "name": "Fortress",   "cost": { "food": 1200, "gold": 1000 }, "researchSeconds": 90,  "at": "bld.towncenter" },
  { "id": "age.4", "name": "Industrial", "cost": { "food": 2000, "gold": 1200 }, "researchSeconds": 120, "at": "bld.towncenter" }
]}
```

## UnitDef — `units/*.json`
```jsonc
{
  "id": "unit.musketeer",
  "name": "Musketeer",
  "civ": "common",                    // "common" or a civ id (unique unit)
  "age": "age.2",
  "trainedAt": ["bld.barracks"],
  "cost": { "food": 75, "gold": 25 },
  "trainSeconds": 30,
  "population": 1,
  "batchSize": 5,                     // mobile: train in batches by default
  "stats": {
    "hp": 150,
    "speed": 4.0,                     // cells / second
    "los": 12,                        // line of sight, cells
    "armor": { "melee": 0.20, "ranged": 0.0, "siege": 0.0 },   // % reduction
    "sizeClass": "small"              // small|medium|large → flow field variant
  },
  "attacks": [                        // first applicable attack is used
    { "id": "ranged", "damage": 23, "type": "ranged", "range": 12, "minRange": 0,
      "cooldownSeconds": 3.0, "projectile": "proj.bullet",
      "multipliers": { "tag.cavalry": 1.0, "tag.villager": 1.5 } },
    { "id": "melee",  "damage": 13, "type": "melee",  "range": 1, "cooldownSeconds": 1.5,
      "multipliers": { "tag.cavalry": 3.0 } }
  ],
  "tags": ["tag.infantry", "tag.gunpowder"],
  "behaviour": { "canGather": false, "canBuild": false, "aggro": "defensive",
                 "leashRange": 8, "fleeHpPercent": 0 },
  "gather": null,                     // villagers: {"allowed": ["res.*"], "rateMultiplier": 1.0}
  "view": { "prefab": "Units/Musketeer", "icon": "ui/units/musketeer", "scale": 1.0,
            "voiceSet": "vo.musketeer" }
}
```
Villager example differences: `"behaviour": {"canGather": true, "canBuild": true, "aggro": "passive", "fleeHpPercent": 30}`,
`"gather": {"allowed": ["res.*"], "rateMultiplier": 1.0}`.

## BuildingDef — `buildings/*.json`
```jsonc
{
  "id": "bld.barracks",
  "name": "Barracks",
  "civ": "common",
  "age": "age.2",
  "cost": { "wood": 200 },
  "buildSeconds": 45,                 // with 1 builder; more builders → 1/(1+0.5·(n−1))
  "footprint": [4, 4],                // cells, w × h; may be rotated 90°
  "stats": { "hp": 2000, "armor": { "siege": -0.2, "ranged": 0.6, "melee": 0.6 }, "los": 10 },
  "placement": { "terrain": ["grass", "dirt"], "minDistanceToEnemyBuilding": 0,
                 "requiresNear": null },         // e.g. {"id":"res.mine","range":4} for mines
  "trains": ["unit.musketeer", "unit.pikeman"],
  "researches": ["tech.veteran_infantry"],
  "dropOff": [],                      // resources accepted; towncenter: ["food","wood","gold"]
  "populationProvided": 0,            // house: 10
  "attack": null,                     // tower: { "damage": 30, "type":"ranged", "range": 14, "cooldownSeconds": 2.5, "garrisonBonus": 0.1 }
  "garrison": 0,
  "limit": 0,                         // 0 = unlimited; towncenter: 1 in Age I–II
  "queue": { "slots": 5, "parallel": 1 },
  "view": { "prefab": "Buildings/Barracks", "icon": "ui/bld/barracks", "constructionPrefab": "Buildings/_Scaffold4x4" }
}
```

## CivDef — `civs/<id>.json`
```jsonc
{
  "id": "civ.crown",
  "name": "The Crown",
  "tagline": "Line infantry and cheap shipments",
  "color": "#3B82F6",
  "startingUnits": [ { "id": "unit.villager", "count": 6 }, { "id": "unit.explorer", "count": 1 } ],
  "startingStockpile": { "food": 200, "wood": 200, "gold": 100 },
  "uniqueUnits": ["unit.redcoat"],
  "uniqueBuildings": [],
  "replacements": { "unit.musketeer": "unit.redcoat" },   // civ-wide swap
  "modifiers": [                      // applied once at load by CivBaker
    { "target": "tag.infantry", "stat": "hp",         "op": "mul", "value": 1.10 },
    { "target": "bld.house",    "stat": "cost.wood",  "op": "mul", "value": 0.80 },
    { "target": "*",            "stat": "gather.res.farm", "op": "mul", "value": 1.10 },
    { "target": "shipment",     "stat": "xpCost",     "op": "mul", "value": 0.90 }
  ],
  "homeCity": {
    "xpPerShipmentBase": 300, "xpGrowth": 1.25,
    "deck": ["ship.3_villagers", "ship.700_wood", "ship.5_redcoats", "ship.outpost_wagon"],
    "deckSize": 8
  },
  "ai": { "buildOrder": "bo.crown_boom", "preferredComp": ["unit.redcoat", "unit.hussar"] }
}
```
`op`: `mul` | `add` | `set`. `target`: entity id, tag, `*`, or `shipment`.

## TechDef & shipments — `techs/*.json`
```jsonc
{ "id": "tech.veteran_infantry", "name": "Veteran Infantry", "age": "age.3",
  "researchedAt": ["bld.barracks"], "cost": { "wood": 200, "gold": 200 }, "researchSeconds": 40,
  "effects": [ { "target": "tag.infantry", "stat": "hp", "op": "mul", "value": 1.2 },
               { "target": "tag.infantry", "stat": "attacks.*.damage", "op": "mul", "value": 1.2 } ],
  "prerequisites": [] }

{ "id": "ship.700_wood", "name": "700 Wood", "kind": "shipment", "age": "age.2",
  "effects": [ { "target": "player", "stat": "stockpile.wood", "op": "add", "value": 700 } ],
  "spawns": [], "oncePerGame": false }

{ "id": "ship.5_redcoats", "kind": "shipment", "age": "age.3",
  "spawns": [ { "id": "unit.redcoat", "count": 5 } ], "oncePerGame": false }
```

## Map file — `Maps/<name>.map.json` (Phase 2)
```jsonc
{ "size": [128, 128], "players": 2,
  "terrain": "<base64 RLE, 1 byte/cell: 0 grass 1 dirt 2 sand 3 water 4 cliff>",
  "elevation": "<base64 RLE>",
  "nodes": [ { "id": "res.tree", "x": 10, "y": 12, "amount": 300 }, "…" ],
  "starts": [ { "x": 20, "y": 20 }, { "x": 108, "y": 108 } ] }
```

## Validation rules (`tools/validate_data.py`, also at load in Editor)
- every referenced id exists; no id defined twice across files;
- `cost` keys ⊂ `economy.resources`;
- `age` of a unit ≥ age of every building in `trainedAt`;
- `footprint` ≥ [1,1]; `trains` only units whose `trainedAt` contains the building;
- civ `replacements` keys/values are units, and the value has `"civ"` = that civ;
- numbers have ≤ 3 decimals (fixed-point safety).
