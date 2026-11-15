"""Opt-in reversed-source native diagnostic, using the existing one-view supervisor."""
from pathlib import Path
from common import digest
import native


def dependency_hashes():
    p=Path(__file__).with_name('native.py'); return {'native.py':digest(p)}


def run(model,sidecar,view,multiple_threads,emit_phase):
    result,svg=native.run(model,sidecar,dict(view,recordLoci=True,reverseSourceOrder=True),multiple_threads,emit_phase)
    result.update(engine='view_order',sourceOrder='reversed expanded source order; identical scene/view/settings')
    return result,svg
