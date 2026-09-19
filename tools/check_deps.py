#!/usr/bin/env python3
"""Enforce the dependency direction from docs/01-ARCHITECTURE.md §2.

RTS.Sim, RTS.Data and RTS.Net must never reference UnityEngine/UnityEditor —
the simulation has to compile headless and stay deterministic.
"""
import os
import re
import sys

SCRIPTS = os.path.join(os.path.dirname(__file__), "..", "Assets", "_Project", "Scripts")
ENGINE_FREE = ("Sim", "Data", "Net")
FORBIDDEN = re.compile(r"\b(UnityEngine|UnityEditor|System\.Random\b|DateTime\.Now|float\s+\w+\s*=)")
# `float` is allowed in Data (parsed into Fix64 at load) but not in Sim
SIM_ONLY_FORBIDDEN = re.compile(r"\b(float|double|Math\.(Sin|Cos|Sqrt)|Random)\b")

bad = 0
for folder in ENGINE_FREE:
    for dirpath, _, files in os.walk(os.path.join(SCRIPTS, folder)):
        for name in files:
            if not name.endswith(".cs"):
                continue
            path = os.path.join(dirpath, name)
            with open(path, encoding="utf-8") as f:
                for n, line in enumerate(f, 1):
                    if line.lstrip().startswith("//"):
                        continue
                    if FORBIDDEN.search(line) or (folder == "Sim" and SIM_ONLY_FORBIDDEN.search(line)):
                        print(f"{os.path.relpath(path, SCRIPTS)}:{n}: {line.strip()}")
                        bad += 1
if bad:
    print(f"\n{bad} forbidden reference(s) in engine-free assemblies")
    sys.exit(1)
print("deps ok")
