"""Summarize completed benchmark folders; retain hashes rather than silently accepting failures."""
import hashlib
import json
import sys
from pathlib import Path
root = Path(sys.argv[1])
rows = []
for tier in ("small", "M", "L"):
    folder = root / (tier + "-run")
    if not folder.exists():
        folder = root / tier
    baseline = json.loads((folder / "baseline.json").read_text())
    rows.append(dict(scene=tier, route="local", cold_seconds=sum(v["seconds"] for v in baseline["rows"]),
                     warm_seconds=None, peak_gib=baseline["peakPrivateBytes"] / 2**30,
                     memory_kind="sampled private", matches=len(baseline["rows"]), views=len(baseline["rows"])))
    for count in ((1,) if tier == "small" else (1, 2, 4)):
        path = folder / f"workers-{count}.json"
        if not path.exists():
            path = folder / f"workers-{len(baseline['rows'])}-{count}.json"
        if not path.exists():
            continue
        runs = json.loads(path.read_text())
        if len(runs) != 2:
            continue
        views = [v for run in runs for v in run["perView"]]
        rows.append(dict(scene=tier, route=str(count), cold_seconds=runs[0]["seconds"], warm_seconds=runs[1]["seconds"],
                         peak_gib=max(r["peakCommitUpperBound"] for r in runs) / 2**30,
                         memory_kind="sum job peak commit (upper bound)", matches=sum(v["match"] and not v["error"] for v in views), views=len(views)))
lines = ["| Scene | Route | Cold/local s | Warm s | Peak GiB | Hash matches |", "|---|---|---:|---:|---:|---:|"]
for row in rows:
    warm = "-" if row["warm_seconds"] is None else f'{row["warm_seconds"]:.3f}'
    lines.append(f'| {row["scene"]} | {row["route"]} | {row["cold_seconds"]:.3f} | {warm} | {row["peak_gib"]:.3f} | {row["matches"]}/{row["views"]} |')
(root / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
print("\n".join(lines))
(root / "summary.json").write_text(json.dumps(rows, indent=2))
template = Path(__file__).with_name("chart.html").read_text(encoding="utf-8")
(root / "worker-times.html").write_text(template.replace("__ROWS__", json.dumps(rows)), encoding="utf-8")
files = sorted(p for p in root.rglob("*") if p.is_file() and p.name != "manifest.json" and (len(p.relative_to(root).parts) == 1 or p.relative_to(root).parts[0] in ("small-run", "M-run", "L-run")))
(root / "manifest.json").write_text(json.dumps({"files": [{"path": str(p.relative_to(root)), "sha256": hashlib.sha256(p.read_bytes()).hexdigest(), "bytes": p.stat().st_size} for p in files]}, indent=2))
