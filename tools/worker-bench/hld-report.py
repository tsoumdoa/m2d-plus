"""Independent sampled-locus checks for NativeHldBench (numpy + shapely 2).

Run against the stable evidence root. These are numerical regression observations,
not whole-span certificates. Exact detached curves remain in sibling .3dm files.
"""
import argparse
import csv
import hashlib
import json
from pathlib import Path

import numpy as np
import shapely
from shapely import STRtree

SKIP_VERTICES = False
FIRST_MISMATCH = False

def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def metadata(path):
    with path.open(encoding="utf-8-sig") as stream:
        prefix = stream.read(8192)
    marker = ',"curves":'
    return json.loads(prefix.split(marker)[0] + "}") if marker in prefix else None


def curve_hash(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        prefix = stream.read(8192)
        digest.update(prefix.split(b',"curves":', 1)[1])
        while chunk := stream.read(1024*1024):
            digest.update(chunk)
    return digest.hexdigest()


def write_tables(root):
    report = ["# Native curved HLD measurements", "", "Seconds; raw rows measure Compute only. Commands include output creation. See docs/research/native-curved-hld.md for method and limitations.", ""]
    report += ["| Actual command | Hidden | Wall s | Output curves |", "|---|---|---:|---:|"]
    for name in ["command-steel-off-correct", "command-steel-on", "command-M-off", "command-M-on"]:
        path = root/name/"command.json"
        if path.exists():
            d = read(path)
            report.append(f'| {name} | {"on" if "_CreateHiddenLines=_Yes" in d["script"] else "off"} | {d["seconds"]:.3f} | {d["added"]} |')
    phase_rows = []
    for scene in ["mm-steel", "mm-curved-M"]:
        folder = root/scene
        comparisons = read(folder/"locus-comparisons.json") if (folder/"locus-comparisons.json").exists() else {}
        report += ["", "## " + scene, "", "Mismatch counts are reference→variant / variant→reference sample points; V=Visible, H=Hidden. A zero count is not a certificate. First-failure scans cover only a checked prefix, explicitly recorded in locus-comparisons.json.", "", "| Variant | Compute s | At 99% s | Visible / Hidden | Output observation |", "|---|---:|---:|---:|---|"]
        for path in sorted(folder.glob("*.json"), key=lambda p: (p.stem != "baseline", p.stem)):
            d = metadata(path)
            if not d or "experiment" not in d:
                continue
            result = comparisons.get(path.stem, {})
            if path.stem == "baseline":
                note = "Reference"
            elif result.get("exportedCurvePayloadIdentical"):
                note = "Identical exported 2D curve payload"
            elif "Visible" in result:
                note = "; ".join(f'{name[0]} {result[name]["referenceToVariant"]["outside"]:,} / {result[name]["variantToReference"]["outside"]:,}' for name in ["Visible", "Hidden"])
                if result.get("firstMismatchScan"):
                    note += " (first-failure scan)"
            elif path.stem.startswith("count-"):
                note = "Different input subset; not an equivalence control"
            elif path.stem in ("threads-2", "threads-3") and any(key.endswith("__"+path.stem) for key in comparisons):
                note = "Compared to preceding threaded run below"
            else:
                note = "Locus comparison not completed"
            first = f'{d["First99Seconds"]:.3f}' if d["First99Seconds"] is not None else "—"
            counts = d["counts"]
            report.append(f'| {path.stem} | {d["seconds"]:.3f} | {first} | {counts.get("Visible",0):,} / {counts.get("Hidden",0):,} | {note} |')
            phase_rows.append(dict(scene=scene, variant=path.stem, **{key: d.get(key) for key in ["captured", "preparation", "geometrySeconds", "registrationSeconds", "seconds", "extractionSeconds", "registered", "tessellationFailures"]}))
        report += ["", "| Additional comparison | Class | Unmatched points, both directions |", "|---|---|---:|"]
        for key, value in comparisons.items():
            if "__" in key or key.startswith(("command-M-", "command-steel-")):
                for name in ["Visible", "Hidden", "All"]:
                    if name in value:
                        report.append(f'| {key} | {name} | {value[name]["referenceToVariant"]["outside"]:,} / {value[name]["variantToReference"]["outside"]:,} |')
    (root/"measurements.md").write_text("\n".join(report)+"\n", encoding="utf-8")
    if phase_rows:
        with (root/"phases.csv").open("w", newline="", encoding="utf-8") as stream:
            writer = csv.DictWriter(stream, fieldnames=phase_rows[0].keys())
            writer.writeheader()
            writer.writerows(phase_rows)


def lines(curves):
    result = []
    for curve in curves:
        points = np.asarray(curve["points"]).reshape(-1, 2)
        if len(points) == 1:
            points = np.repeat(points, 2, axis=0)
        # A whole long curve has a large bounding box and makes nearest queries
        # on coincident elevation geometry needlessly expensive. Index exact
        # contiguous polyline chunks; this changes neither geometry nor distance.
        for start in range(0, len(points)-1, 32):
            result.append(shapely.LineString(points[start:start+33]))
    return np.asarray(result, dtype=object)


def samples(curves):
    """All tessellation vertices plus uniform arc samples <=20mm, max2001/curve."""
    chunks = []
    for curve in curves:
        points = np.asarray(curve["points"]).reshape(-1, 2)
        if not SKIP_VERTICES:
            chunks.append(points)
        if len(points) < 2:
            continue
        lengths = np.concatenate(([0], np.cumsum(np.linalg.norm(np.diff(points, axis=0), axis=1))))
        count = min(2001, max(9, int(np.ceil(lengths[-1] / 20)) + 1))
        distances = np.linspace(0, lengths[-1], count)
        chunks.append(np.stack([np.interp(distances, lengths, points[:, axis]) for axis in (0, 1)], axis=1))
    return np.concatenate(chunks) if chunks else np.empty((0, 2))


def directed(a, b, tolerance):
    queries = samples(a)
    if not len(queries):
        return dict(samples=0, outside=0, outsideFraction=0)
    if not b:
        return dict(samples=len(queries), outside=len(queries), outsideFraction=1)
    tree = STRtree(lines(b))
    outside = checked = 0
    examples = []
    for chunk in np.array_split(queries, max(1, len(queries) // 20000)):
        indices = tree.query_nearest(shapely.points(chunk), max_distance=tolerance, all_matches=False)
        missed = np.ones(len(chunk), dtype=bool)
        missed[indices[0]] = False
        outside += np.count_nonzero(missed)
        checked += len(chunk)
        if len(examples) < 4:
            examples.extend(chunk[missed][:4-len(examples)].tolist())
        if FIRST_MISMATCH and outside:
            break
    distances = tree.query_nearest(shapely.points(examples), all_matches=False, return_distance=True)[1].tolist() if examples else []
    return dict(samples=len(queries), checkedSamples=checked, stoppedEarly=checked<len(queries), outside=int(outside), outsideFraction=float(outside/checked), examples=examples, exampleDistances=distances)


def compare(a, b, tolerance, classes=True):
    names = ("Visible", "Hidden") if classes else ("All",)
    result = {}
    for name in names:
        ac = [c for c in a["curves"] if not classes or c["visibility"] == name]
        bc = [c for c in b["curves"] if not classes or c["visibility"] == name]
        result[name] = dict(referenceToVariant=directed(ac, bc, tolerance), variantToReference=directed(bc, ac, tolerance))
    return result


def main():
    global SKIP_VERTICES, FIRST_MISMATCH
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    parser.add_argument("--scene", choices=["mm-steel", "mm-curved-M"], required=True)
    parser.add_argument("--variants", help="Comma list; default all completed variants")
    parser.add_argument("--skip-vertices", action="store_true", help="Large scenes: retain arc samples only, rather than every tessellation vertex too")
    parser.add_argument("--tables-only", action="store_true", help="Refresh measurement tables without running locus queries")
    parser.add_argument("--first-mismatch", action="store_true", help="Stop each directed scan after the first failed query chunk; counts cover only the checked prefix")
    args = parser.parse_args()
    if args.tables_only:
        write_tables(args.root)
        return
    SKIP_VERTICES = args.skip_vertices
    FIRST_MISMATCH = args.first_mismatch
    folder = args.root / args.scene
    reference = read(folder / "baseline.json")
    tolerance = reference["AbsoluteTolerance"] * 1.125
    output = folder / "locus-comparisons.json"
    results = read(output) if output.exists() else {}
    paths = [folder / (v+".json") for v in args.variants.split(",")] if args.variants else sorted(folder.glob("*.json"))
    for path in paths:
        if path.stem in results or path.name.endswith("-progress.json") or path.name == "baseline.json":
            continue
        info = metadata(path)
        if not info or info.get("skipTessellation"):
            continue
        if curve_hash(path) == curve_hash(folder/"baseline.json"):
            results[path.stem] = {"exportedCurvePayloadIdentical": True}
            output.write_text(json.dumps(results, indent=2))
            print(args.scene, path.stem, "identical exported curve payload", flush=True)
            continue
        value = read(path)
        result = compare(reference, value, tolerance)
        result["tolerance"] = tolerance
        result["sampling"] = "arc samples only" if SKIP_VERTICES else "all tessellation vertices and arc samples"
        result["firstMismatchScan"] = FIRST_MISMATCH
        result["referenceTessellationFailures"] = reference["tessellationFailures"]
        result["variantTessellationFailures"] = value["tessellationFailures"]
        results[path.stem] = result
        output.write_text(json.dumps(results, indent=2))
        print(args.scene, path.stem, {k: (v["referenceToVariant"]["outside"], v["variantToReference"]["outside"]) for k, v in result.items() if isinstance(v, dict)}, flush=True)
    # Thread repeat comparisons test loci independently of serial/parallel changes.
    for left, right in [("threads", "threads-2"), ("threads-2", "threads-3"), ("baseline", "baseline-2"), ("baseline-2", "baseline-3")]:
        key = left + "__" + right
        if key not in results and (folder/(left+".json")).exists() and (folder/(right+".json")).exists():
            results[key] = compare(read(folder/(left+".json")), read(folder/(right+".json")), tolerance)
            output.write_text(json.dumps(results, indent=2))
            print(args.scene, key, results[key], flush=True)
    for name in (["command-steel-off-correct", "command-steel-on"] if args.scene == "mm-steel" else ["command-M-off", "command-M-on"]):
        path = args.root/name/"command-curves.json"
        if path.exists() and name not in results:
            command = read(path)
            result = compare(reference, command, tolerance, classes=not command["hidden"])
            results[name] = result
            output.write_text(json.dumps(results, indent=2))
            print(args.scene, name, result, flush=True)
    write_tables(args.root)


if __name__ == "__main__":
    main()
