"""Summarize offset plan evidence without modifying models or drawing archives."""
import csv
import json
import re
import sys
from pathlib import Path

root = Path(sys.argv[1]).expanduser()
prefix = sys.argv[2] if len(sys.argv) > 2 else "verified"
records = []
summary = []
phases = set()
for name in ("origin", "100m", "1km"):
    folder = root / (prefix + "-" + name)
    data = json.loads((folder / "offset.json").read_text())
    views = data["timings"]
    summary.append({"offset": name, "route": "local", "seconds": sum(v["seconds"] for v in views),
                    "unresolved": sum(v["unresolved"] for v in views), "pieces": sum(v["pieces"] for v in views),
                    "regions": sum(v["regions"] for v in views), "localValidations": views[-1]["localValidations"],
                    "rawSectionSeconds": sum(v["nativeSectionSeconds"] for v in views)})
    for view in views:
        fallback = re.search(r"exact fallbacks=(\d+)", view["info"])
        work = dict(re.findall(r"(slab patches|slab budget hits|shared-topology nodes|shared-topology budget hits)=(\d+)", view["info"]))
        records.append({"offset": name, "route": "local", "view": view["i"], "wall": view["seconds"],
                        "unresolved": view["unresolved"], "pieces": view["pieces"], "regions": view["regions"],
                        "exact fallbacks": int(fallback[1]) if fallback else 0, **work, **view["phases"]})
        phases.update(view["phases"])
    for file in sorted(folder.glob("pool-10-*.json")):
        previous_pids = None
        for batch in json.loads(file.read_text()):
            retained = bool(previous_pids) and batch["pids"] == previous_pids and not any(v.get("recovered") for v in batch["views"])
            route = file.stem + (" cold" if batch["pass"] == 0 else " warm" if retained else " repeat (resized)")
            previous_pids = batch["pids"]
            summary.append({"offset": name, "route": route, "seconds": batch["seconds"], "workers": batch["sizing"]["Count"],
                            "matched": sum(v.get("match") is True for v in batch["views"]), "views": len(batch["views"])})
            for view in batch["views"]:
                wire = json.loads((folder / f"reply-{view['i']}.json").read_text())
                text = ";".join(wire["Diagnostics"])
                fallback = re.search(r"exact fallbacks=(\d+)", text)
                work = dict(re.findall(r"(slab patches|slab budget hits|shared-topology nodes|shared-topology budget hits)=(\d+)", text))
                records.append({"offset": name, "route": route, "view": view["i"],
                                "unresolved": wire["Values"]["Report"]["UncertainCount"], "pieces": view["pieces"], "regions": view["regions"],
                                "exact fallbacks": int(fallback[1]) if fallback else 0, **work, **view.get("phases", {})})
                phases.update(view.get("phases", {}))

fields = ["offset", "route", "view", "wall", "pieces", "unresolved", "regions", "exact fallbacks",
          "slab patches", "slab budget hits", "shared-topology nodes", "shared-topology budget hits"] + sorted(phases)
with (root / "offset-phases.csv").open("w", newline="") as output:
    writer = csv.DictWriter(output, fields)
    writer.writeheader()
    writer.writerows(records)
(root / "offset-summary.json").write_text(json.dumps(summary, indent=2))
lines = ["| Offset | Route | Wall s | Workers | Strict hashes |", "|---|---|---:|---:|---|"]
for row in summary:
    hashes = f"{row['matched']}/{row['views']}" if "matched" in row else "reference"
    lines.append(f"| {row['offset']} | {row['route']} | {row['seconds']:.3f} | {row.get('workers', 1)} | {hashes} |")
lines += ["", "Phase seconds summed over ten views (nested observations are not additive):", "",
          "| Phase | Origin local | 100 m local | 1 km local |", "|---|---:|---:|---:|"]
for phase in sorted(phases):
    values = [sum(float(r.get(phase, 0)) for r in records if r["offset"] == name and r["route"] == "local") for name in ("origin", "100m", "1km")]
    lines.append(f"| {phase} | " + " | ".join(f"{v:.3f}" for v in values) + " |")
lines += ["", "Cold worker phase seconds summed over ten views (concurrent, not batch critical-path seconds):", "",
          "| Phase | Origin workers | 100 m workers | 1 km workers |", "|---|---:|---:|---:|"]
for phase in sorted(phases):
    values = [sum(float(r.get(phase, 0)) for r in records if r["offset"] == name and r["route"].endswith(" cold")) for name in ("origin", "100m", "1km")]
    lines.append(f"| {phase} | " + " | ".join(f"{v:.3f}" for v in values) + " |")
(root / "offset-tables.md").write_text("\n".join(lines) + "\n")
print("\n".join(lines))
