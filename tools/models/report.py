"""Build an offline contact sheet and evidence summary from completed/capped runs."""
import argparse
import base64
from collections import Counter, defaultdict
import html
import json
import math
from pathlib import Path
import statistics
import struct

from common import ROOT, digest, write_json

ORDER = ['XS', 'S', 'M', 'L', 'XL', 'XXL']
ESC = html.escape
DESCRIPTIONS = {'view-sources':'All seven view forms; named parallel/perspective, clip depth/scope/flipped normal.',
       'section-styles':'Layer, custom, clipping-plane and nested-parent styles; no-fill, viewport fill, fallback colours.',
       'cut-topology':'Hollow profiles, nested islands, touching styles, overlap, contacts and a crop splitting an L.',
       'view-range':'Known 2/3/4 m depths, far-plane crossings and a long receding wall.',
       'glass':'Transparent-material curtain wall, mullions, furniture and a column behind.',
       'blocks':'Four nested levels, asymmetric mirror and nonuniform scale; 200 windows plus 200 copies.',
       'edges':'Filleted box, cylinder/revolution seams, SubD canopy and extrusion/Brep twins.',
       'curves-points':'Gridlines, hidden rebar, front/back points and a surface-contact curve.',
       'annotations':'Text, dot, dimension, leader and hatch unsupported-input census.'}


def purpose(model):
    return DESCRIPTIONS.get(model['id'], 'Units/large-coordinate metamorphic building.' if model['id'].startswith('units-')
                           else f"{model.get('tier')} architectural tier, {model.get('flavor')}; {model['expected'].get('buildings')} building(s), {model['expected'].get('floors')} levels.")


def model_gallery(models, folder):
    out = ['<h2>Models</h2><p>Shaded model references, 1200 × 800. Colours follow layers/object overrides. Green dashed outlines show clipping/view-source frames without clipping the model. The offscreen fallback uses simplified annotation labels and opaque materials; capture methods are listed per model.</p><div class="model-grid">']
    entries = []
    for m in models:
        capture_path = folder / (m['id'] + '.capture.json')
        capture = json.loads(capture_path.read_text()) if capture_path.exists() else {}
        current = capture.get('status') == 'ok' and capture.get('fixtureSha256') == m['sha256'] and capture.get('sidecarSha256') == digest(folder.parent / (m['id'] + '.json'))
        method = capture.get('method', 'unavailable')
        out.append(f'<article class="card"><h3>{ESC(m["id"])}</h3><p class="stats">{m["stats"]["objects"]:,} objects · {ESC(purpose(m))}<br>{ESC(method)}</p><div class="model-shots">')
        for view in ['SW', 'Front'] + (['Top'] if m['tier'] else []):
            path = folder / f'{m["id"]}-{view}.png'
            if path.exists() and current:
                encoded = base64.b64encode(path.read_bytes()).decode('ascii')
                out.append(f'<div><small>{view}</small><div class="drawing photo"><img alt="{ESC(m["id"])} {view} shaded model" src="data:image/png;base64,{encoded}"></div></div>')
                width, height = struct.unpack('>II', path.read_bytes()[16:24])
                entries.append({'model': m['id'], 'view': view, 'path': str(path.resolve()), 'width': width, 'height': height,
                                'pngSha256': digest(path), 'fixtureSha256': capture['fixtureSha256'], 'method': method})
            else:
                out.append(f'<p class="error">{view}: capture unavailable or stale</p>')
        out.append('</div></article>')
    out.append('</div>')
    write_json(folder / 'manifest.json', {'methods': sorted(set(e['method'] for e in entries)), 'captures': entries, 'count': len(entries)})
    return ''.join(out)


def records(folder):
    rows = []
    for p in sorted(folder.glob('*.json')):
        if p.name.endswith('.watch.json'):
            continue
        row = json.loads(p.read_text(encoding='utf-8'))
        if 'view' in row and 'model' in row:
            row['_path'] = p
            rows.append(row)
    return rows


def groups(rows):
    grouped = defaultdict(list)
    for r in rows:
        if r.get('tier'):
            grouped[(r.get('engine', 'native'), r['tier'], r['flavor'], r.get('multipleThreads', False))].append(r)
    result = []
    for (engine, tier, flavor, threads), rs in sorted(grouped.items(), key=lambda p: (p[0][0], ORDER.index(p[0][1]), p[0][2], p[0][3])):
        failed = {'wall_timeout', 'private_memory_limit', 'worker_crash', 'exception', 'native_null', 'unsupported', 'view_error', 'no_result'}
        completed = [r for r in rs if r.get('phase') == 'complete' and r.get('computeSeconds') is not None and r['status'] not in failed]
        result.append({'engine': engine, 'tier': tier, 'flavor': flavor, 'threads': threads,
                       'objects': rs[0].get('stats', {}).get('objects'), 'attempts': len(rs), 'completed': len(completed),
                       'statuses': dict(Counter(r['status'] for r in rs)),
                       'medianCompute': statistics.median(r['computeSeconds'] for r in completed) if completed else None,
                       'maxCompute': max((r['computeSeconds'] for r in completed), default=None),
                       'longOverlapCompute': next((r['computeSeconds'] for r in completed if r['view'] == 'Section long overlap'), None),
                       'totalWall': sum(r.get('wallSeconds', 0) for r in rs),
                       'peakGiB': max((r.get('peakPrivateBytes', 0) / 1024 ** 3 for r in rs), default=0)})
    return result


def chart(summary):
    values = [s for s in summary if not s['threads'] and s['longOverlapCompute'] is not None]
    if not values:
        return ''
    width, height = 680, 260
    xmin, xmax = math.log10(50), math.log10(150000)
    ymax = max(.1, max(s['longOverlapCompute'] for s in values)) * 1.12
    def x(n):
        return 65 + (math.log10(n) - xmin) / (xmax - xmin) * 580
    def y(n):
        return 215 - n / ymax * 185
    out = [f'<svg class="chart" viewBox="0 0 {width} {height}" role="img" aria-label="Long-overlap section HLD compute seconds by object count; threading off">']
    for i in range(5):
        v = ymax * i / 4
        out.append(f'<path d="M65 {y(v):.2f}H650" stroke="var(--border,#bcc2cc)"/><text x="55" y="{y(v)+4:.2f}" text-anchor="end">{v:.1f}</text>')
    for n in (50, 500, 5000, 20000, 50000, 150000):
        out.append(f'<text x="{x(n):.2f}" y="235" text-anchor="middle">{n:,}</text>')
    for flavor, color in [('blocks', '#2785c5'), ('exploded', '#d17939')]:
        for ei, engine in enumerate(sorted(set(s['engine'] for s in values))):
            rs = sorted([s for s in values if s['flavor'] == flavor and s['engine'] == engine], key=lambda s: s['objects'])
            dash = ' stroke-dasharray="7 4"' if ei else ''
            out.append('<polyline fill="none" stroke="' + color + '" stroke-width="2"' + dash + ' points="' + ' '.join(f"{x(s['objects']):.2f},{y(s['longOverlapCompute']):.2f}" for s in rs) + '"/>')
            for s in rs:
                out.append(f'<circle cx="{x(s["objects"]):.2f}" cy="{y(s["longOverlapCompute"]):.2f}" r="4" fill="{color}"><title>{engine} {s["tier"]} {flavor}: long-overlap section {s["longOverlapCompute"]:.3f}s</title></circle>')
    out.append('<text x="65" y="15">Long-overlap section compute seconds · threading off</text><text x="170" y="258" style="fill:#2785c5">● blocks</text><text x="310" y="258" style="fill:#d17939">● exploded</text><text x="470" y="258">objects (log scale)</text></svg>')
    return ''.join(out)


def table(summary):
    out = ['<div class="table-wrap"><table><thead><tr><th>Tier</th><th>Form</th><th>Threads</th><th>Objects</th><th>Done / tried</th><th>HLD median / max s</th><th>Total wall s</th><th>Peak GiB</th></tr></thead><tbody>']
    for s in summary:
        times = f"{s['medianCompute']:.3f} / {s['maxCompute']:.3f}" if s['medianCompute'] is not None else '—'
        out.append(f'<tr><td>{s["tier"]}<small> {ESC(s["engine"])}</small></td><td>{s["flavor"]}</td><td>{"on" if s["threads"] else "off"}</td><td>{s["objects"]:,}</td><td>{s["completed"]} / {s["attempts"]}</td><td>{times}</td><td>{s["totalWall"]:.1f}</td><td>{s["peakGiB"]:.2f}</td></tr>')
    return ''.join(out) + '</tbody></table></div>'


def compact_table(summary):
    off = [s for s in summary if not s['threads']]
    out = ['<div class="table-wrap"><table><tr><th>Tier / objects</th><th>Long section HLD s<br>blocks / exploded</th><th>Peak GiB<br>blocks / exploded</th><th>Finished / tried</th></tr>']
    for engine in sorted(set(s['engine'] for s in off)):
        for tier in ORDER:
            rows = {s['flavor']: s for s in off if s['tier'] == tier and s['engine'] == engine}
            if not rows:
                continue
            times, memory = [], []
            for flavor in ('blocks', 'exploded'):
                row = rows.get(flavor, {})
                value = row.get('longOverlapCompute')
                times.append(f'{value:.3f}' if value is not None else '—')
                memory.append(f"{row.get('peakGiB',0):.2f}")
            objects = next(iter(rows.values()))['objects']
            done, tried = sum(r['completed'] for r in rows.values()), sum(r['attempts'] for r in rows.values())
            out.append(f'<tr><td>{ESC(engine)} {tier}<br><small>{objects:,}</small></td><td>{" / ".join(times)}</td><td>{" / ".join(memory)}</td><td>{done} / {tried}</td></tr>')
    return ''.join(out) + '</table></div><p class="muted">Threading off. Peaks and finished counts cover all attempted views; chart/times use the long-overlap section. — means no completed probe. The full report includes threading comparisons and per-view diagnostics.</p>'


CSS = '''
:root{color-scheme:light dark;--foreground:#243041;--muted:#637084;--card:#f6f8fb;--border:#d6dce6;--radius:10px;--font-sans:system-ui,sans-serif}
@media(prefers-color-scheme:dark){:root{--foreground:#e4e9f1;--muted:#a1aec1;--card:#1b2431;--border:#344155}}
html,body{font:14px/1.5 var(--font-sans);color:var(--foreground);margin:0}main{max-width:1500px;margin:auto}h1{font-size:26px}h2{font-size:19px}p{max-width:95ch}small,.muted{color:var(--muted)}
.summary,.panel{padding:16px;background:var(--card);border-radius:var(--radius);margin:16px 0}.chart{width:100%;height:260px;background:transparent!important}.chart text{fill:var(--foreground);font:11px system-ui}
.model-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(350px,1fr));gap:12px}.model-shots{display:grid;grid-template-columns:1fr 1fr;gap:8px}.photo{height:auto!important}.photo img{display:block;width:100%;height:auto}dialog img{width:100%;max-height:800px;object-fit:contain}.drawing>img{width:100%;height:100%;object-fit:contain}
table{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums}td,th{padding:7px 10px;border-bottom:1px solid var(--border);text-align:right}td:first-child,th:first-child{text-align:left}.table-wrap{overflow:auto}
details{margin:16px 0;border:1px solid var(--border);border-radius:var(--radius);padding:12px}summary{cursor:pointer;font-size:18px;font-weight:650}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(280px,1fr));gap:12px;margin-top:12px}.card{background:var(--card);padding:12px;border-radius:8px;min-width:0}.drawing{width:100%;height:220px;overflow:hidden;cursor:zoom-in}.drawing>svg{width:100%;height:100%}.card h3{font-size:14px;margin:4px 0}.badge{display:inline-block;border:1px solid var(--border);padding:1px 5px;border-radius:4px;font-size:11px}.error{color:#d48443}.stats{font-variant-numeric:tabular-nums;font-size:12px}button,select{font:inherit;padding:5px 10px;background:var(--card);color:var(--foreground);border:1px solid var(--border);border-radius:5px}dialog{max-width:95%;width:1200px;border:1px solid var(--border);background:var(--card);color:var(--foreground)}a{color:#2785c5}
'''


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--results', type=Path, default=ROOT / 'artifacts/baseline')
    p.add_argument('--models', type=Path, default=ROOT / 'artifacts/models')
    p.add_argument('--docs', type=Path, default=ROOT / 'docs/research/test-models.md')
    args = p.parse_args(); rows = records(args.results); summary = groups(rows)
    models = [json.loads(f.read_text()) for f in sorted(args.models.glob('*.json')) if f.name != 'validation.json']
    models = [m for m in models if 'views' in m]
    bymodel = defaultdict(list)
    for r in rows:
        bymodel[r['model']].append(r)
    status = dict(Counter(r['status'] for r in rows))
    stress_note = ('XXL has one bounded long-overlap probe per form; other XXL views were not attempted.'
                   if any(r.get('tier') == 'XXL' for r in rows) else 'XXL is generated but not run.')
    info = f'{len(models)} generated models · {len(rows)} view attempts · {sum(r.get("unsetProxies",0) for r in rows):,} Unset proxies recorded'
    intro = '<h1>M2D+ · native HLD baseline</h1><p>' + ESC(info) + '</p><p>Local only. One fresh owned Rhino.Inside process per view, serial admission, hidden on, tangents/seams off. Blue = blocks; orange = exploded. Timings are single trials; completed does not mean correct. XL is one long-overlap section per flavor. ' + stress_note + '</p>'
    notes = '<p><b>Scope:</b> block definitions expand recursively with transforms; SubD is explicitly converted to Brep. Native cut/crop/far planes are passed to HLD. Hatches, semantic region cropping/merging, glass=clear, beyond/fade and no-line far-edge suppression are not supplied by raw HLD. Perspective is an expected view error. Invalid Unset proxies are counted without dereferencing.</p>'
    gallery = model_gallery(models, args.models / 'screenshots')
    out = ['<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>M2D+ native baseline</title><style>', CSS, '</style><main>', intro, gallery, '<h2>Native HLD performance</h2><section class="panel">', chart(summary), table(summary), '</section>', notes,
           '<p class="muted">Click a thumbnail to inspect. Visible: solid; hidden: dashed grey; native section curves: heavier. All drawings and data are embedded; no network required.</p><button onclick="document.querySelectorAll(\'details\').forEach(x=>x.open=true)">Expand all models</button>']
    for m in models:
        rs = bymodel[m['id']]
        out.append(f'<details{ " open" if m["id"] in ("view-sources", "cut-topology") else ""}><summary>{ESC(m["id"])} <small>{m["stats"]["objects"]:,} objects · {m["sizeBytes"]/1e6:.2f} MB · {len(rs)} attempts</small></summary><div class="grid">')
        for r in rs:
            svgpath = r['_path'].with_suffix('.svg')
            # SVG image documents isolate native drawing styles from chart/page CSS.
            drawing = ('<img alt="' + ESC(r['view']) + '" src="data:image/svg+xml;base64,' + base64.b64encode(svgpath.read_bytes()).decode('ascii') + '">') if svgpath.exists() else '<p class="error">No completed drawing: ' + ESC(r['status']) + '</p>'
            counts = ' · '.join(f'{k} {v:,}' for k,v in r.get('pieceCounts', {}).items())
            c = r.get('computeSeconds')
            times = f'HLD {c:.3f}s' if c is not None else 'HLD did not complete'
            out.append('<article class="card"><h3>' + ESC(r['view']) + f' <span class="badge">{ESC(r.get("engine","native"))} · MT {"on" if r.get("multipleThreads") else "off"}</span></h3><div class="drawing">' + drawing + '</div><div class="stats">' +
                       f'{times} · wall {r.get("wallSeconds",0):.2f}s · {r.get("peakPrivateBytes",0)/1024**3:.2f} GiB<br>{ESC(counts)}<br>' +
                       f'<span class="badge">{ESC(r["status"])}</span> · Unset {r.get("unsetProxies","—")} · unsupported {len(r["unsupported"]) if "unsupported" in r else "—"}</div></article>')
        if not rs:
            out.append('<p>Generated; not run. XXL stress is reserved for a separate manual session.</p>')
        out.append('</div></details>')
    out.append('<dialog id="zoom"><button onclick="this.parentElement.close()">Close</button><div></div></dialog><script>document.querySelectorAll(".drawing").forEach(e=>e.onclick=()=>{const d=document.getElementById("zoom");d.querySelector("div").innerHTML=e.innerHTML;d.showModal()})</script></main></html>')
    (args.results / 'index.html').write_text(''.join(out), encoding='utf-8')
    # Compact companion for the conversation, using the same measured chart/table.
    examples = ['<div class="grid">']
    for model, shot in [('view-sources', 'SW'), ('cut-topology', 'SW'), ('section-styles', 'Front'),
                        ('perf-M-blocks', 'SW'), ('perf-L-blocks', 'Front'), ('perf-XL-blocks', 'Top')]:
        path = args.models / 'screenshots' / f'{model}-{shot}.png'
        if path.exists():
            examples.append('<article class="card"><h3>' + model + ' · ' + shot + '</h3><div class="photo"><img alt="' + model + '" src="data:image/png;base64,' + base64.b64encode(path.read_bytes()).decode('ascii') + '"></div></article>')
    examples.append('</div><p class="muted">Offscreen shaded references. The full offline report includes every model/view.</p>')
    (args.results / 'summary.html').write_text('<!doctype html><meta charset="utf-8"><style>' + CSS + '</style>' + '<p>' + ESC(info) + '</p>' + ''.join(examples) + chart(summary) + compact_table(summary) + notes, encoding='utf-8')
    write_json(args.results / 'summary.json', {'models': len(models), 'attempts': len(rows), 'statuses': status, 'performance': summary})
    doc = ['# Local test model suite and native baseline\n',
           'Local-only research tools; no CI, hook integration, plug-in changes, install, commit or push. Generated archives, sidecars, raw results and visuals live under gitignored `artifacts/`. See [exact commands](../../tools/models/README.md).\n',
           '## Scope and reproducibility\n',
           'The generator uses a fixed seed (20261114), metres by default, and the installed RhinoCommon 8 geometry constructors. Existing frozen primitive fixtures are reused with source SHA256 hashes, rather than copied into a second fixture set. The sidecars carry the UX view vocabulary, native depth/scope, product settings and analytic/metamorphic expectations. Native output is a baseline, not an oracle for itself.\n',
           '## Models\n', '| Model | Objects / expanded | MB | Purpose |', '|---|---:|---:|---|']
    for m in models:
        doc.append(f"| {m['id']} | {m['stats']['objects']:,} / {m['stats']['expandedObjects']:,} | {m['sizeBytes']/1e6:.3f} | {purpose(m)} |")
    doc += ['\nPerformance scene cells contain slabs, cores, solid/CHS/RHS columns, piers/lintels around wall openings, glazing/mullions, stair treads and furniture. Roof plant, curved fins and SubD canopies are present; XL/XXL add a 3,200-triangle terrain mesh. Blocks and exploded forms use identical source geometry and placements. Counts exclude clip widgets and definition members. Detailed by-type counts, Brep faces, mesh triangles and definition/instance counts are in every sidecar.\n',
            'XS is one detailed room; S is a two-storey house; M has eight storeys; L has a 30-storey tower plus a three-level podium. XL has five buildings and XXL fifteen. The tier object targets are exact (50, 500, 5,000, 20,000, 50,000 and 150,000), excluding view widgets.\n',
            '## Fixture verification\n',
            'All 13 feature archives were reopened with native read logs, validated as geometry and verified at ≤2,000,000 bytes each. The six independent hollow/overlap section checks pass their region/area/hole expectations. A separate planar Boolean of the L profile and diagonal crop gives two regions of total area 2 m². Clipping depth, scope and flipped normal are read back from the archive. `artifacts/models/validation.json` retains these checks.\n',
            'The installed RhinoCommon 8 API uses `ObjectSectionAttributesSource.FromLayer`, `FromObject`, `FromSectioner` and `FromParent`; custom styles use `SetCustomSectionStyle`. `SectionStyle` exposes `HatchIndex`, `HatchScale`, `BackgroundFillMode`, `BackgroundFillColor`, `BoundaryVisible` and `BoundaryWidthScale`. The saved clipping surfaces use `PlaneDepthEnabled`, `PlaneDepth`, `SetClipParticipation` and `ParticipationListsEnabled`. All requested style properties were set and read back; no unavailable style property is recorded. `File3dm.Objects.AddSurface` preserves the clipping subtype and metadata.\n',
            'All 13 feature archives and both forms of XS and S matched SHA256 byte-for-byte across regeneration (`artifacts/repeat/byte-comparison.json`). New native-assigned definition/member/dimension-style UUIDs are canonicalized in CRC-verified chunks, and native archive reads validate the result. Generation uses a fresh process per model group. Larger tiers use the same deterministic mechanism but have not been independently regenerated twice for byte comparison. Tier archive metadata includes the stable worktree staging path; byte equality is scoped to the same Rhino build/worktree.\n',
            'The 13 small feature archives and their sidecars are prepared under `tests/fixtures/features/`, with hashes and future `hook` eligibility in the manifest. No hook or CI execution is enabled. Performance archives and all report/render artifacts are gitignored. Nothing was staged, committed or pushed.\n',
            'Local verification also passed all six unlicensed archive/supervisor tests, the forced pre-commit hook (Debug build and 27 Core tests), the Release solution build, and normal Debug solution tests (27 Core passed; one licensed Rhino smoke test skipped). The separate offscreen renderer builds with zero warnings.\n',
            '## Model reference PNGs\n',
            'The Models gallery at the top of `artifacts/baseline/index.html` embeds SW and Front views of every feature, and SW/Front/Top of both forms of every tier including XXL. All are 1200×800 with a light background, layer/object colours and green view-source frame overlays. Exact PNG paths are in `artifacts/models/screenshots/manifest.json`.\n',
            'A dedicated hidden Rhino.exe `/nosplash /notemplate /netcore-8 /runscript=...` attempt produced no capture before its 90 s cap (`artifacts/capture-pilot.log`); its owned Job Object alone was closed. The delivered PNGs use the documented **offscreen CPU mesh fallback**, with native Rhino meshing and a depth buffer. They are not claimed to be native viewport screenshots. Materials render opaque; annotation screen labels are simplified. The renderer is outside the plug-in/solution and uses no other Rhino process.\n',
            '## Baseline method\n',
            'One fresh .NET 8 Rhino.Inside worker per view, no interactive documents. The Windows supervisor creates the worker suspended and assigns it to a private Job Object **before any launcher code runs**, includes all job descendants in sampled private memory, enforces an 8 GiB hard job cap and a 600 s total wall cap (startup/read/compute/extraction). Memory is polled every 200 ms; the peak is retained, the logged time series is decimated to roughly one second, and Windows job peak memory is recorded separately. Only that job can be terminated. XS–M use both `multipleThreads` settings; features and L+ use off. Each row records host/engine/input/sidecar/harness hashes, CPU/wall/compute/preparation/read time, counts, errors and native Unset proxies. There is no first-preview stage.\n',
            'Definition recursion retains cumulative instance transforms. SubD conversion to Brep is explicitly recorded; raw HLD has no direct SubD or instance-reference support. Other unsupported AddGeometry results remain visible in the diagnostics. Native clipping is used for section, depth and crop planes, including per-source scope. No heavy custom visibility engine is mixed in.\n',
            'SVGs contain all extractable visible/hidden curves, with heavier native section silhouettes. Curves are tessellated at document tolerance for rendering only; the native curves are not rewritten as an engine output. Hidden points are counted but not drawn. Unset or parentless proxies are counted without dereferencing unsafe native proxies.\n',
            '`Flatten=false` retains native depth during extraction; SVG paths use the resulting view-frame X/Y coordinates. The additional class-length diagnostics measure native 3D segment arclengths before depth is discarded, with unit comparisons normalized to metres. They are invariance checks, not projected plot-length totals. Hidden section curves retain dashed grey styling with the heavier cut width.\n',
            stress_note + '\n',
            '## Measured performance\n',
            '| Tier | Form | MT | Completed / attempted | Median HLD s | Max HLD s | Total wall s | Peak private GiB |', '|---|---|---|---:|---:|---:|---:|---:|']
    for s in summary:
        med = f"{s['medianCompute']:.3f}" if s['medianCompute'] is not None else '—'
        maximum = f"{s['maxCompute']:.3f}" if s['maxCompute'] is not None else '—'
        doc.append(f"| {s['tier']} | {s['flavor']} | {'on' if s['threads'] else 'off'} | {s['completed']}/{s['attempts']} | {med} | {maximum} | {s['totalWall']:.1f} | {s['peakGiB']:.2f} |")
    doc += ['\nSingle cold trials on the shared desktop, not statistical performance claims. Cold means a fresh process/scene; the OS file cache was not flushed. No unrelated processes were inspected or suspended to make the machine quiescent. The chart in `artifacts/baseline/index.html` compares the same long-overlap section view with threading off against object count; the table summarizes all attempted views. Capped attempts are retained, excluded from completed medians and chart points, and must not be read as fast successes. XL/XXL probes have only one long-overlap section per form, so their table timings are not nine-view medians.\n',
            '## Feature view measurements\n',
            '| Model | View | HLD s | Wall s | Visible / hidden / cut pieces | Unset | Status |',
            '|---|---|---:|---:|---:|---:|---|']
    for r in rows:
        if r.get('tier') is not None:
            continue
        c = r.get('computeSeconds'); t = f'{c:.3f}' if c is not None else '—'; pieces = r.get('pieceCounts', {})
        doc.append(f"| {r['model']} | {r['view']} | {t} | {r.get('wallSeconds',0):.3f} | {pieces.get('Visible',0)} / {pieces.get('Hidden',0)} / {pieces.get('Cut',0)} | {r.get('unsetProxies',0)} | {r['status']} |")
    assessment_path = args.results / 'assessment.json'
    pair_path = args.models / 'pair-validation.json'
    if pair_path.exists():
        pairs = json.loads(pair_path.read_text())
        doc += ['\n## Expanded input equivalence\n',
                'Before interpreting blocks/exploded output differences, a separate native geometry comparison expands both archives into world coordinates and checks each named source with Brep.IsDuplicate at 0.001 × model tolerance (exact GeometryEquals for meshes). `artifacts/models/pair-validation.json` records the results.\n']
        for pair in pairs['rows']:
            doc.append(f"- {pair['tier']}: {pair['checkedObjects']:,} sources checked; equivalent={pair['pass']}; {len(pair['mismatches'])} mismatches.")
    validation_path = args.models / 'validation.json'
    if validation_path.exists():
        unit_inputs = json.loads(validation_path.read_text()).get('unitEquivalence', [])
        if unit_inputs:
            doc.append('\nThe unit/large-coordinate inputs are also normalized to metres and translated back before a separate Brep geometry comparison at 1e-7 m:')
            for unit in unit_inputs:
                doc.append(f"- {unit['model']}: {unit['checkedObjects']} sources; equivalent={unit['pass']}; {len(unit['mismatches'])} mismatches.")
    if assessment_path.exists():
        findings = json.loads(assessment_path.read_text())['findings']
        doc += ['\n## Limited output assessments\n',
                f"{sum(f['pass'] for f in findings)} / {len(findings)} limited checks match their expectations or comparison aggregates. These include analytic beam/point occlusion, annotation rejection, unit/translation class lengths, threading comparisons and blocks-versus-exploded class lengths. See `artifacts/baseline/assessment.json` for every result. Equal lengths/counts do not prove equal loci.\n"]
        for f in findings:
            if not f['pass']:
                differences = f.get('lengthDifferenceOnMinusOff', f.get('lengthDifferenceBlocksMinusExploded', f.get('classLengthErrorsMetres', {})))
                numeric = '; '.join(f'{k} {v:+.6f} m' for k, v in differences.items())
                doc.append(f"- **{f['model']} / {f['view']}** — {f['property']}: {numeric or 'differs from expectation'}. {f.get('interpretation','')} The correct full output is not established by this comparison.")
    doc += ['\n## Visual inspection\n',
            'The inspected feature contact sheet includes `cut-topology / Plan cut` and `L split rectangle`, `glass / Front clear`, `curves-points / Top`, `blocks / SW` and `edges / SW`. The L crop shows two disconnected closed outlines; nested RHS/CHS holes and the inner island are visible. Rebar is dashed, the surface-contact curve is solid, and the two rotated window fields are visually comparable. The clear-glass view visibly retains hidden dashed furniture/column geometry. No hatch fill is shown because raw HLD supplies no material-region/style pipeline.\n',
            'An enlarged inspection of **cut-topology / Plan cut** shows the shared interior joint at x=2 m between the same-style touching walls (y=5 to 5.3 m). It remains drawn, just like the joint between the different-colour walls. Raw HLD therefore does not satisfy the product’s same-style merged-region expectation; this is a semantic merge gap, not a claim that native HLD implements that policy incorrectly. `artifacts/qa-touching.html` preserves the enlarged inspection.\n',
            '## Native findings and limits\n', f'Execution statuses: `{json.dumps(status, sort_keys=True)}`. Full per-source records are in `artifacts/baseline/*.json`.\n']
    missing_native_hashes = sum(not r.get('nativeBinaryHashes') for r in rows)
    if missing_native_hashes:
        doc.append(f'{missing_native_hashes} rows record the Rhino build and RhinoCommon SHA256 but have an empty native-binary hash map. The runner now resolves the parent System directory when RhinoCommon is in System/netcore; earlier measurements were preserved without backfilling unobserved per-run fingerprints.\n')
    for r in rows:
        if r.get('unsetProxies') or r.get('extractionErrors') or r['status'] in ('wall_timeout', 'private_memory_limit', 'exception', 'worker_crash', 'view_error', 'unsupported'):
            extra = (f" Last phase {r.get('phase')}; wall {r.get('wallSeconds',0):.2f} s; peak {r.get('peakPrivateBytes',0)/1024**3:.2f} GiB."
                     if r['status'] in ('wall_timeout', 'private_memory_limit', 'worker_crash', 'exception') else '')
            extraction = len(r['extractionErrors']) if 'extractionErrors' in r else 'unmeasured'
            doc.append(f"- **{r['model']} / {r['view']} / MT {'on' if r.get('multipleThreads') else 'off'}**: {r['status']}; Unset proxies {r.get('unsetProxies','unmeasured')}; extraction errors {extraction}; {ESC(r.get('error',''))[:250]}.{extra}")
    doc += ['\nRaw HLD does not consume section styles or materials. `glass / Front clear` therefore remains opaque, like `Front opaque`; this is a known API policy gap, not an implemented clear-glass route. `section-styles` verifies stored style inputs but native HLD produces no hatch fills. `view-range / Range line` and `Range noline` pass the same far clipping geometry: HLD does not provide the product’s separate no-line suppression, Beyond or fade classes. Rectangle cuts do not certify closed material regions. These product expectations remain in sidecars for a future engine adapter.\n',
            'Perspective named views produce a per-view validation error. General curved/freeform full-output truth remains unknown; piece counts alone do not establish missing/extra-locus correctness. Native threading comparisons are single trials and do not establish reproducibility.\n',
            'The named-camera read-back audit exposed detached `ViewInfo.Viewport` mutations when adding a `ViewInfo` to File3dm. The generator now snapshots a standalone `RhinoViewport`, sets the target explicitly, and verifies saved projection/direction/valid target. The earlier run is retained separately in `artifacts/baseline-projection-audit/` (including one Rhino startup E_FAIL for S blocks / Plan level 1 / MT on) and is excluded from all final tables. Intermediate camera probes in `artifacts/camera-pilot/` are also excluded.\n',
            '## Engine extension\n',
            'Pass `--engine module_name` to select another adapter exposing `run(model, sidecar, view, multiple_threads, emit_phase) -> (result_dict, svg_text)`. The worker owns Rhino initialization and File3dm disposal; the adapter owns its drawing and any GH documents. A future M2D+ adapter can initialize headless Grasshopper and solve the component without changing generation, supervision or reporting. M0 has empty outputs, so no fabricated M2D+ comparison is included.\n']
    args.docs.parent.mkdir(parents=True, exist_ok=True)
    args.docs.write_text('\n'.join(doc) + '\n', encoding='utf-8')
    print(info)


if __name__ == '__main__':
    main()
