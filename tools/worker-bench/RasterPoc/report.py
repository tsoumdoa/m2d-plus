"""Summarize preserved raster receipts without loading private geometry or Rhino."""
import argparse
import json
from pathlib import Path


def summarize(root, matrix, expected_resolutions=3):
    rows = []
    incomplete = []
    failures = []
    completed_cases = set()
    for folder in sorted((root / matrix).iterdir()):
        case_path = folder / 'case.json'
        if not case_path.exists():
            failure = folder / 'failure.json'
            if failure.exists():
                value = json.loads(failure.read_text(encoding='utf-8-sig'))
                failures.append(dict(folder=folder.name, label=value['Label'], view=value['View'], error=value['Error']))
            else:
                incomplete.append(folder.name)
            continue
        case = json.loads(case_path.read_text(encoding='utf-8-sig'))
        threaded_path = folder / 'threaded-timing.json'
        if not threaded_path.exists():
            threaded_path = root / 'threaded' / (case['Label'] + '-' + case['View'].replace(' ', '-')) / 'case.json'
        threaded = json.loads(threaded_path.read_text(encoding='utf-8-sig')) if threaded_path.exists() else None
        completed = 0
        for result_path in sorted(folder.glob('*/result.json'), key=lambda p: int(p.parent.name)):
            result = json.loads(result_path.read_text(encoding='utf-8-sig'))
            timing = result['Timings']
            hld = case['HldPhases']['native solve']
            production = threaded['HldPhases']['native solve'] if threaded else None
            feature_path = result_path.parent / 'error-features.json'
            features = json.loads(feature_path.read_text(encoding='utf-8-sig')) if feature_path.exists() else None
            rows.append(dict(
                label=case['Label'], view=case['View'], resolution=timing['RequestedResolution'],
                width=timing['Width'], height=timing['Height'], pixel_mm=timing['Pixel'] * case['MillimetresPerUnit'],
                hld_single_s=hld, hld_threaded_s=production, raster_s=timing['Total'],
                speedup_single=hld / timing['Total'], speedup_threaded=production / timing['Total'] if production else None,
                wrong_visible_mm=result['WrongVisibleMm'], wrong_visible_pct=result['WrongVisiblePercent'],
                wrong_hidden_mm=result['WrongHiddenMm'], wrong_hidden_pct=result['WrongHiddenPercent'],
                raster_missing_mm=result['RasterMissingMm'], hld_missing_mm=result['HldMissingMm'],
                raster_unresolved_mm=result['Compare']['PipesToHld']['UnresolvedMm'],
                hld_unresolved_mm=result['Compare']['HldToPipes']['UnresolvedMm'],
                raster_points=result['Compare']['PipesToHld']['Points'], hld_points=result['Compare']['HldToPipes']['Points'],
                peak_private_gib=result['PeakPrivateBytes'] / 2**30,
                peak_working_gib=result['PeakWorkingSetBytes'] / 2**30,
                hld_peak_private_gib=case['HldPeakPrivateBytes'] / 2**30,
                phases={k: timing[k] for k in ('Tessellate', 'Raster', 'Silhouette', 'MeshSilhouette', 'Edges', 'Classify')},
                triangles=timing['Triangles'], candidates=timing['Candidates'], silhouettes=timing['Silhouettes'],
                mesh_silhouette_segments=timing['MeshSilhouetteSegments'],
                scene_sources=case['SceneSources'], registered_sources=case['RegisteredSources'],
                registrations=case['Registrations'], authored_depth=case['AuthoredSourceDepth'], resolved_depth=case['ResolvedDepth'],
                preparation_s=case['PreparationSeconds'], features=features, receipt=str(result_path),
                benchmark_hash=case['BenchHash'], fingerprint=case['Fingerprint'],
                invalid_inputs=timing.get('InvalidInputs', 0), without_excluded_layer=result.get('WithoutExcludedLayer')))
            if features is not None:
                completed += 1
        if completed != expected_resolutions:
            incomplete.append(f'{folder.name}: {completed}/{expected_resolutions} complete resolutions')
        else:
            completed_cases.add((case['Label'], case['View']))
    incomplete.extend(f['folder'] for f in failures if (f['label'], f['view']) not in completed_cases)
    priority = {'owner-2': 0, 'owner-1': 1, 'perf-M': 2, 'curved-S': 3}
    rows.sort(key=lambda r: (priority.get(r['label'], 4), r['label'], 0 if r['view'] == 'Top' else 1, r['resolution']))
    return dict(matrix=matrix, rows=rows, incomplete=incomplete, failed_trials=failures)


def ray_summary(root, ray_directory=None):
    result = []
    for path in sorted((ray_directory or root / 'rays').glob('*/ray-controls.json')):
        receipt = json.loads(path.read_text(encoding='utf-8-sig'))
        for kind in ('WrongVisible', 'WrongHidden'):
            rows = [r for r in receipt['Records'] if r['Kind'] == kind]
            def material(r, direction):
                return any(d['InteriorMm'] > 0 for d in r[direction])
            def hits(r, direction):
                return any(d['DepthsMm'] for d in r[direction])
            result.append(dict(
                case=path.parent.name, kind=kind, controls=len(rows),
                candidate_material=sum(material(r, 'CandidateRay') for r in rows),
                centre_material=sum(material(r, 'PixelCentreRay') for r in rows),
                candidate_hits=sum(hits(r, 'CandidateRay') for r in rows),
                centre_hits=sum(hits(r, 'PixelCentreRay') for r in rows),
                candidate_open_hits=sum(any(not d['Solid'] and d['DepthsMm'] for d in r['CandidateRay']) for r in rows),
                candidate_only_material=sum(material(r, 'CandidateRay') and not material(r, 'PixelCentreRay') for r in rows),
                centre_only_material=sum(not material(r, 'CandidateRay') and material(r, 'PixelCentreRay') for r in rows),
                own_candidate_material=sum(any(d['InteriorMm'] > 0 and d['Identity'] == r['SourceIdentity'] for d in r['CandidateRay']) for r in rows),
                tangent_candidates=sum(r['Profile'] for r in rows),
                inside_requested_frame=sum(r['InsideRequestedFrame'] for r in rows),
                failed_native_queries=sum(any(not d['Succeeded'] for d in r['CandidateRay'] + r['PixelCentreRay']) for r in rows)))
    return result


def markdown(summary):
    lines = [
        '| Model / view | Requested px | Actual grid | Pixel mm | HLD serial s | HLD threaded s | Raster s | Threaded speedup | Wrong-visible mm / % | Wrong-hidden mm / % | Raster-only / HLD-only mm | Peak private GiB |',
        '|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|']
    for r in summary['rows']:
        threaded = f"{r['hld_threaded_s']:.3f}" if r['hld_threaded_s'] is not None else '—'
        ratio = f"{r['speedup_threaded']:.2f}×" if r['speedup_threaded'] is not None else '—'
        lines.append(
            f"| {r['label']} / {r['view']} | {r['resolution']} | {r['width']}×{r['height']} | {r['pixel_mm']:.4f} | "
            f"{r['hld_single_s']:.3f} | {threaded} | {r['raster_s']:.3f} | {ratio} | "
            f"{r['wrong_visible_mm']:.2f} / {r['wrong_visible_pct']:.4f} | {r['wrong_hidden_mm']:.2f} / {r['wrong_hidden_pct']:.4f} | "
            f"{r['raster_missing_mm']:.2f} / {r['hld_missing_mm']:.2f} | {r['peak_private_gib']:.2f} |")
    if summary['incomplete']:
        lines += ['', 'Incomplete: ' + '; '.join(summary['incomplete'])]
    return '\n'.join(lines) + '\n'


def detail_tables(summary, rays):
    rows = [r for r in summary['rows'] if r['resolution'] == 8192]
    lines = ['### Highest-resolution phase times', '',
             '| Model / view | Tessellate s | Raster s | Native silhouettes s | Mesh silhouettes alternative s | Edges ms | Classify/output s | Total s |',
             '|---|---:|---:|---:|---:|---:|---:|---:|']
    for r in rows:
        p = r['phases']
        lines.append(f"| {r['label']} / {r['view']} | {p['Tessellate']:.3f} | {p['Raster']:.3f} | {p['Silhouette']:.3f} | {p['MeshSilhouette']:.3f} | {p['Edges']*1000:.2f} | {p['Classify']:.3f} | {r['raster_s']:.3f} |")
    lines += ['', '### Source census and preparation', '',
              '| Model / view | Captured sources | Registration records | Registered sources | Preparation s | HLD serial peak private GiB |',
              '|---|---:|---:|---:|---:|---:|']
    for r in rows:
        lines.append(f"| {r['label']} / {r['view']} | {r['scene_sources']} | {r['registrations']} | {r['registered_sources']} | {r['preparation_s']:.3f} | {r['hld_peak_private_gib']:.2f} |")
    lines += ['', '### Points and native Unresolved at the highest resolution', '',
              'Hidden points are omitted by both adapters; absent visible points do not identify a hidden/Unset reference class. Other controls have no visible points or native Unresolved curve length.', '',
              '| Model / view | Raster visible points | HLD visible points | Raster points absent from HLD | HLD points absent from raster | HLD Unresolved curve mm |',
              '|---|---:|---:|---:|---:|---:|']
    for r in rows:
        a, b = r['raster_points'], r['hld_points']
        if a['Total'] or b['Total'] or r['hld_unresolved_mm']:
            lines.append(f"| {r['label']} / {r['view']} | {a['Total']} | {b['Total']} | {a['Absent']} | {b['Absent']} | {r['hld_unresolved_mm']:.3f} |")
    if rays:
        lines += ['', '### Representative native ray controls', '',
                  'Counts concern the twelve longest spans per direction, one midpoint per span. They are not length-weighted accuracy estimates. Material means positive strict solid-interior length between native forward intersections.', '',
                  '| Case | Direction | Controls | Candidate hits / material | Centre hits / material | Candidate-only material | Centre-only material | Own-body material | In requested frame | Failed queries |',
                  '|---|---|---:|---:|---:|---:|---:|---:|---:|---:|']
        for r in rays:
            lines.append(f"| {r['case']} | {r['kind']} | {r['controls']} | {r['candidate_hits']} / {r['candidate_material']} | {r['centre_hits']} / {r['centre_material']} | {r['candidate_only_material']} | {r['centre_only_material']} | {r['own_candidate_material']} | {r['inside_requested_frame']} | {r['failed_native_queries']} |")
    return '\n'.join(lines) + '\n'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('evidence_root', type=Path)
    parser.add_argument('--matrix', default='matrix-4')
    parser.add_argument('--expected-resolutions', type=int, default=3)
    parser.add_argument('--rays-directory', type=Path)
    parser.add_argument('--output-root', type=Path, help='Keep stage 1 summaries separate from preserved stage 0 evidence')
    parser.add_argument('--doc', type=Path, help='Replace the explicitly marked numerical results block in the research report')
    args = parser.parse_args()
    receipt = summarize(args.evidence_root, args.matrix, args.expected_resolutions)
    output = args.output_root or args.evidence_root
    output.mkdir(parents=True, exist_ok=True)
    rays = ray_summary(args.evidence_root, args.rays_directory)
    (output / 'summary.json').write_text(json.dumps(receipt, indent=2), encoding='utf-8')
    (output / 'summary.md').write_text(markdown(receipt), encoding='utf-8')
    (output / 'rays-summary.json').write_text(json.dumps(rays, indent=2), encoding='utf-8')
    if args.doc:
        if receipt['incomplete'] or len(receipt['rows']) != 24:
            raise ValueError('Refuse to write a complete report from an incomplete matrix')
        content = args.doc.read_text(encoding='utf-8')
        start, end = '<!-- stage0-results:start -->', '<!-- stage0-results:end -->'
        a, b = content.index(start) + len(start), content.index(end)
        content = content[:a] + '\n\n' + markdown(receipt) + '\n' + detail_tables(receipt, rays) + '\n' + content[b:]
        args.doc.write_text(content, encoding='utf-8')
    print(markdown(receipt))
