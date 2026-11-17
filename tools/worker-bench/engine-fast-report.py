"""Summarize current threaded HLD hidden-on/off benchmark manifests."""
import json
import sys
from pathlib import Path

def report(root):
    rows = []
    for path in sorted(root.rglob("engine.json")):
        data = json.loads(path.read_text(encoding="utf-8-sig"))
        for row in data.get("rows", []):
            if "options" not in row:
                continue  # Historical exact/fast evidence has a different, archived contract.
            rows.append({"manifest": str(path), "model": data.get("model"), "hidden": row["hidden"],
                         "seconds": row["seconds"], "options": row["options"], "fingerprint": data.get("fingerprint")})
    return rows

if __name__ == "__main__":
    print(json.dumps(report(Path(sys.argv[1])), indent=2))
