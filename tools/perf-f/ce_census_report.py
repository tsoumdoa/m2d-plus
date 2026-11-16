"""Markdown table from `ce-census` runs (hillclimb/curved-engine, section 1).

    python tools/perf-f/ce_census_report.py artifacts/curved-engine/census2
"""
import glob, json, os, sys

def main(folder):
    print("| Model | Leaves | Sent to native | Exact now (sphere, cylinder, tube, extruded arc) | Other analytic (torus, cone, several trimmed faces) | Freeform Brep | Mesh, SubD | Free curves |")
    print("|---|---:|---:|---|---|---|---|---|")
    for path in sorted(glob.glob(os.path.join(folder, "*-census.json"))):
        d = json.load(open(path, encoding="utf-8")); curved = d["curved"]
        if curved == 0: continue
        kinds, exact = d["kinds"], d["exact"]
        total = sum(k["Length"] for k in kinds.values()) or 1e-9
        def cell(sources, length, lengths=True):
            if sources == 0: return "—"
            return f"{sources:,} ({sources / curved:.1%}" + (f"; {length / total:.0%} of edge length)" if lengths else ")")
        now = [(v["Sources"], v["Length"]) for k, v in exact.items() if k not in ("not exact", "curve (candidate only)")]
        n_now, l_now = sum(x[0] for x in now), sum(x[1] for x in now)
        def kind(*names): return sum(kinds.get(n, {}).get("Sources", 0) for n in names), sum(kinds.get(n, {}).get("Length", 0) for n in names)
        n_free, l_free = kind("Freeform", "Revolved"); n_mesh, l_mesh = kind("Mesh", "SubD", "Other"); n_curve, l_curve = kind("Curve")
        n_other, l_other = curved - n_now - n_free - n_mesh - n_curve, total - l_now - l_free - l_mesh - l_curve
        names = ", ".join(f"{k} {v['Sources']:,}" for k, v in exact.items() if k not in ("not exact", "curve (candidate only)"))
        print(f"| {d['model']} | {d['leaves']:,} | {curved:,} | {cell(n_now, l_now)}{' — ' + names if names else ''} | {cell(n_other, max(l_other, 0))} | {cell(n_free, l_free)} | {cell(n_mesh, l_mesh, False)} | {cell(n_curve, l_curve)} |")

if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main(sys.argv[1] if len(sys.argv) > 1 else "artifacts/curved-engine/census2")
