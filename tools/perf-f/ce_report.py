"""Markdown tables from curved-engine prototype runs (hillclimb/curved-engine).

    python tools/perf-f/ce_report.py artifacts/curved-engine/final

Reads every *-ce.json under the folder. Lengths are metres in the drawing.
"""
import glob, json, os, sys

def rows(folder):
    for path in sorted(glob.glob(os.path.join(folder, "**", "*-ce.json"), recursive=True)):
        data = json.load(open(path, encoding="utf-8"))
        native = next(r for r in data["rows"] if r["name"] == "native")
        for r in data["rows"]:
            if r["name"] == "engine":
                tier = "" if r["tier1"] else " (mesh only)"
                yield os.path.basename(path)[:-8].replace("curved-perf-S-blocks", "curved S").replace("view-", "") + tier, native, r

def main(folder):
    print("| Model, view | ε mm | Native s | Prototype s | Phase with the own-face ray tests s (tests) | Prototype without that phase s | Speed-up, with / without | Solve s | Triangles | Chords |")
    print("|---|---:|---:|---:|---:|---:|---|---:|---:|---:|")
    for name, native, r in rows(folder):
        local = r["phases"].get("chords + local analysis", 0)
        # The stand-in share of that phase: tests x measured cost is not separable, so report the whole phase when tests ran.
        stand = local if r.get("ownFaceRayTests", 0) else 0
        rest = r["engineSeconds"] - stand
        tests = r.get('ownFaceRayTests', 0)
        print(f"| {name} | {r['eps']*1000:g} | {r['nativeWall']:.2f} | {r['engineSeconds']:.2f} | {(f'{stand:.2f} ({tests:,})') if tests else '—'} | {rest:.2f} | {r['nativeWall']/r['engineSeconds']:.1f}× / {r['nativeWall']/max(rest,1e-9):.1f}× | {r['phases'].get('solve', 0):.2f} | {r['triangles']:,} | {r['chords']:,} |")
    print()
    print("| Model, view | ε mm | Prototype Visible / Hidden / Unresolved m | Unresolved share of all / of non-hidden | Native Visible / Hidden / Unresolved m | Native Unresolved share of all | Native line with no prototype line m | Prototype line with no native line m |")
    print("|---|---:|---|---|---|---:|---:|---:|")
    for name, native, r in rows(folder):
        l, n = r["length"], r["nativeLength"]; total = l["visible"] + l["hidden"] + l["unresolved"]
        miss = sum(row[3] for row in r["nativeToEngine"]); extra = sum(row[3] for row in r["engineToNative"])
        ntotal = n['visible'] + n['hidden'] + n['unresolved']
        print(f"| {name} | {r['eps']*1000:g} | {l['visible']:.1f} / {l['hidden']:.1f} / {l['unresolved']:.1f} | {l['unresolved']/max(total,1e-9):.1%} / {l['unresolved']/max(l['visible']+l['unresolved'],1e-9):.1%} | {n['visible']:.1f} / {n['hidden']:.1f} / {n['unresolved']:.1f} | {n['unresolved']/max(ntotal,1e-9):.1%} | {miss:.1f} | {extra:.1f} |")
    print()
    print("| Model, view | ε mm | Prototype certain m | Same class as native | Native Unresolved there | Differs near a change point m | Differs m | Prototype Hidden where native draws Visible m | Prototype Visible where native draws Hidden m | Checked: prototype right / flagged / inconclusive / too short |")
    print("|---|---:|---:|---:|---:|---:|---:|---:|---:|---|")
    for name, native, r in rows(folder):
        a = r["adjudication"]; tot = [sum(v[i] for v in a.values()) for i in range(2, 6)]
        m = r["engineToNative"]
        print(f"| {name} | {r['eps']*1000:g} | {r['certain']:.1f} | {r['agree']:.1f} ({r['agree']/max(r['certain'],1e-9):.1%}) | {r['nativeUnresolved']:.1f} | {r['shift']:.1f} | {r['core']:.1f} | {m[1][0]:.1f} | {m[0][1]:.1f} | {tot[0]} / {tot[1]} / {tot[2]} / {tot[3]} |")
    print()
    print("| Group (all runs summed) | Samples | Checked | Prototype right | Flagged | Inconclusive | Too short to check |")
    print("|---|---:|---:|---:|---:|---:|---:|")
    total = {}
    for name, native, r in rows(folder):
        for group, v in r["adjudication"].items():
            label = {"agree": "Same class as native", "native-unresolved": "Prototype certain, native Unresolved", "engine-only": "Prototype line with no native line",
                     "core engine Hidden / native Visible": "Prototype Hidden, native Visible", "core engine Visible / native Hidden": "Prototype Visible, native Hidden",
                     "shift engine Hidden / native Visible": "Prototype Hidden, native Visible, near a change point", "shift engine Visible / native Hidden": "Prototype Visible, native Hidden, near a change point"}.get(group, group)
            total[label] = [a + b for a, b in zip(total.get(label, [0] * 6), v)]
    for label, v in total.items(): print(f"| {label} | {v[0]:,} | {v[1]:,} | {v[2]:,} | {v[3]} | {v[4]} | {v[5]:,} |")
    v = [sum(x[i] for x in total.values()) for i in range(6)]; print(f"| **All** | {v[0]:,} | {v[1]:,} | {v[2]:,} | {v[3]} | {v[4]} | {v[5]:,} |")
    print()
    print("| Model, view | ε mm | Unresolved, by cause (m) |")
    print("|---|---:|---|")
    for name, native, r in rows(folder):
        print(f"| {name} | {r['eps']*1000:g} | {'; '.join(f'{k} {v:.1f}' for k, v in r.get('unresolvedWhy', {}).items()) or 'none'} |")

if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main(sys.argv[1] if len(sys.argv) > 1 else "artifacts/curved-engine/final")
