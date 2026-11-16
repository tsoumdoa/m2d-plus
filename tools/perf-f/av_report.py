"""Markdown tables from stage 1 analytic checks (`av:` runs; hillclimb/curved-engine, stage 1 section).

    python tools/perf-f/av_report.py artifacts/stage1/av
"""
import glob, json, os, re, sys

def load(folder):
    rows = []
    for path in sorted(glob.glob(os.path.join(folder, "*-av.json"))):
        d = json.load(open(path, encoding="utf-8")); d["file"] = os.path.basename(path)[:-8]; rows.append(d)
    return rows

def counts(text):
    m = re.search(r"recognised=(\d+); exact=(\d+); kept[^=]*=(\d+)", text or "")
    return tuple(int(v) for v in m.groups()) if m else (0, 0, 0)

def main(folder):
    rows = load(folder)
    print("| Model, view | Sources | Recognised / exact / kept on native | Before s | After s | Speed-up | Native calls | Native solve s | Unresolved m (share) |")
    print("|---|---:|---|---:|---:|---:|---|---|---|")
    for d in rows:
        b, a = d["before"], d["after"]; rec, exact, kept = counts(d["analytic"])
        def unresolved(x): return f"{x['lengths'].get('Unresolved', 0):.1f} ({x['unresolvedShare']:.1%})"
        print(f"| {d['file']} | {d['sources']:,} | {rec} / {exact} / {kept} | {b['wall']:.2f} | {a['wall']:.2f} | {b['wall'] / max(a['wall'], 1e-9):.1f}× | {b['nativeCalls']} → {a['nativeCalls']} | {b['nativeSolve']:.2f} → {a['nativeSolve']:.2f} | {unresolved(b)} → {unresolved(a)} |")
    print()
    print("| Model, view | After Visible: before Visible / Hidden / Unresolved / no line m | After Hidden: before Visible / Hidden / Unresolved / no line m | Before line with no after line m | Checked: right / flagged / not judged | Repeat identical |")
    print("|---|---|---|---:|---|---|")
    total = [0, 0, 0, 0]
    for d in rows:
        f = d["afterToBefore"]; back = d["beforeToAfter"]
        row = lambda r: f"{f[r][0]:.1f} / {f[r][1]:.1f} / {f[r][2]:.1f} / {f[r][4]:.1f}"
        missing = sum(back[r][4] for r in range(3)); adj = d["adjudication"].values()
        checked = [sum(v[i] for v in adj) for i in (1, 2, 3, 4)]
        for i in range(4): total[i] += checked[i]
        print(f"| {d['file']} | {row(0)} | {row(1)} | {missing:.1f} | {checked[1]} / {checked[2]} / {checked[3]} | {'yes' if d['after']['repeatIdentical'] else 'NO'} |")
    print()
    print(f"Ray check, all runs: {total[0]:,} samples checked, {total[1]:,} right, {total[2]} flagged, {total[3]:,} not judged.")
    print()
    groups = {}
    for d in rows:
        for key, v in d["adjudication"].items():
            groups[key] = [x + y for x, y in zip(groups.get(key, [0] * 5), v)]
    print("| Group (all runs) | Samples | Checked | Right | Flagged | Not judged |")
    print("|---|---:|---:|---:|---:|---:|")
    for key, v in sorted(groups.items()): print(f"| {key} | {v[0]:,} | {v[1]:,} | {v[2]:,} | {v[3]} | {v[4]:,} |")
    for d in rows:
        for line in d.get("flagged", []): print("FLAGGED", d["file"], line)

if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main(sys.argv[1] if len(sys.argv) > 1 else "artifacts/stage1/av")
