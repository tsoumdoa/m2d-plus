"""Summarize EngineFastBench and real Make2D records; stdlib only."""
import csv
import json
import sys
from pathlib import Path


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def report(root):
    lines = ["# Opt-in fast engine measurements", "", "Milliseconds/millimetres are not interchanged: all time columns below are seconds. Models are mm, tolerance 0.01 mm.", "", "M2D+ wall is fresh in-process visibility plus presentation; capture, startup, GH publication and evidence serialization are excluded. The actual Make2D command includes its native extraction/rejoin and Rhino output creation. All cells are n=1 on a shared workstation; native/caches and concurrent work are uncontrolled.", "", "| Model / hidden | Exact wall | Fast wall | Actual Make2D wall | Exact native | Fast native | Exact audit | Fast audit |", "|---|---:|---:|---:|---:|---:|---:|---:|"]
    phases = []
    for name, command in [("curved-M", "curved-perf-M-blocks"), ("curved-steel", "curved-steel")]:
        folder = root/(name+"-final") if (root/(name+"-final")/"engine.json").exists() else root/name
        data = read(folder/"engine.json")
        assert len(data["rows"]) == 4
        for hidden in [False, True]:
            exact = next(r for r in data["rows"] if r["engine"] == "exact" and r["hidden"] == hidden)
            fast = next(r for r in data["rows"] if r["engine"] == "fast" and r["hidden"] == hidden)
            native = read(root/f'command-{command}-{"Yes" if hidden else "No"}'/"command.json")
            assert native["ran"] and not native["IsHeadless"] and native["added"] > 0
            assert not exact["MultipleThreads"] and fast["MultipleThreads"] and fast["FastNativeSources"] > 0
            assert all(o["IncludeHiddenCurves"] == hidden and o["MultipleThreads"] for o in fast["native"])
            values = [exact["wallSeconds"], fast["wallSeconds"], native["seconds"], exact["rawPhases"]["native solve"], fast["rawPhases"]["native solve"], exact["rawPhases"]["native ray verification"], fast["rawPhases"]["native ray verification"]]
            lines.append(f'| {name} / {"on" if hidden else "off"} | ' + " | ".join(f"{v:.3f}" for v in values) + " |")
        for row in data["rows"]:
            for scope in ["rawPhases", "presentationPhases"]:
                phases.extend(dict(model=name, engine=row["engine"], hidden=row["hidden"], view=row["Name"], scope=scope, phase=k, seconds=v) for k, v in row[scope].items())
    lines += ["", "Fast native results are not certified and have no cross-run/worker byte-identity promise. Fast hidden-off may still yield a partial native Hidden set; every returned fragment remains eligible for the unchanged audit. Uncomputed hidden curves cannot be checked for false-Hidden. See Info for actual flags, budget/skip counts and contradictions."]
    for name in ["building-M-planar", "building-M-mixed"]:
        folder = root/(name+"-final") if (root/(name+"-final")/"engine.json").exists() else root/name
        data = read(folder/"engine.json")
        assert len(data["rows"]) == 32
        rows = data["rows"]
        lines += ["", "## " + name, "", f'{data["SourceCount"]:,} source leaves. Each row compares the same view and requested hidden setting. Hashes normalize authored engine/settings metadata only, retaining geometry, classes, provenance, quality and routing fields.', "", "| View | Hidden off exact / fast | Hidden on exact / fast | Fast native calls off / on | Exact/fast output hash matches off / on |", "|---|---:|---:|---:|---|"]
        for i in range(8):
            def cell(engine, hidden):
                return next(r for r in rows if r["i"] == i and r["engine"] == engine and r["hidden"] == hidden)
            eo, fo, en, fn = cell("exact",False), cell("fast",False), cell("exact",True), cell("fast",True)
            lines.append(f'| {eo["Name"]} | {eo["wallSeconds"]:.3f} / {fo["wallSeconds"]:.3f} | {en["wallSeconds"]:.3f} / {fn["wallSeconds"]:.3f} | {fo["NativeCalls"]} / {fn["NativeCalls"]} | {fo["planarHashMatch"]} / {fn["planarHashMatch"]} |')
        if name.endswith("planar"):
            assert all(r["NativeCalls"] == 0 and r["FastNativeSources"] == 0 and not r["MultipleThreads"] for r in rows)
            assert all(r["planarHashMatch"] for r in rows if r["engine"] == "fast")
            lines += ["", "**16/16 strict normalized output hashes match**, including the six directions, long section and plan with hidden off/on. No native shortcut or fast remark. The source derivative removes 210 nonplanar top-level objects from the original M-blocks document; its generation manifest records every removed ID/name. Remaining blocks and geometry are unchanged."]
            if folder.name.endswith("-final"):
                previous = read(root/name/"engine.json")["rows"]
                keys = lambda r: (r["i"],r["engine"],r["hidden"])
                hashes = {keys(r): r["hash"] for r in previous}
                assert all(hashes[keys(r)] == r["hash"] for r in rows)
                lines += ["", "Final hidden-contract refactor confirmation: **32/32 hashes also match the preceding implementation**, across both modes, hidden flags and eight views."]
        else:
            lines += ["", "The original M-blocks fixture is mixed, containing CHS, curved fins and a SubD canopy. Native HLD runs in every view, so this is not a planar-invariance control. Full payload hashes differ (they include actual threading and fast-source state); these mismatches alone are not quantitative locus comparisons. No fast byte-equality claim is made."]
        for row in rows:
            for scope in ["rawPhases", "presentationPhases"]:
                phases.extend(dict(model=name, engine=row["engine"], hidden=row["hidden"], view=row["Name"], scope=scope, phase=k, seconds=v) for k, v in row[scope].items())
    lines += ["", "The abandoned `building-M` pilot stopped on its first native call because it incorrectly assumed the original fixture was planar. It is excluded; `building-M-mixed` is the complete rerun. Curved M uses the initial measured binary. The final settings-plumbing refactor restores the universal hidden-on kernel contract and passes RequestedHidden only to native; HLD flags, native geometry and verifier budgets are the same. Final steel and planar confirmation folders are preferred above; all earlier cells and exact fingerprints remain archived."]
    (root/"measurements.md").write_text("\n".join(lines)+"\n", encoding="utf-8")
    with (root/"phases.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=phases[0].keys())
        writer.writeheader()
        writer.writerows(phases)
    print("Verified four curved cells per model and 16 planar hash matches; wrote measurements.md and phases.csv")


if __name__ == "__main__":
    report(Path(sys.argv[1]))
