#!/usr/bin/env python3
"""Writes the conformance corpus's cases as seed inputs for both targets: corpus/<target>/<case>, the case's bytes
after the index of its type in the target's dispatch (the order check.rs's `names` reads)."""
import json, pathlib, re, sys

here = pathlib.Path(__file__).resolve().parent
corpus = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else here / "../../../conformance/corpus")
targets = {"conformance": ("tests/conformance/types.rs", "pub fn with_type"),
           "generated": ("tests/generated/nino_types.rs", "pub fn with_nino_type")}
index = json.loads((corpus / "index.json").read_text())
for target, (source, function) in targets.items():
    text = (here / ".." / source).read_text()
    body = text[text.index(function):]
    body = body[:body.index("_ => return None")]
    names = [n for n in re.findall(r'"([^"]*)"', body) if n.startswith("Conformance.")]
    out = here / "corpus" / target
    out.mkdir(parents=True, exist_ok=True)
    count = 0
    for group in ("valid", "malicious"):
        for case in index[group]:
            meta = json.loads((corpus / group / f"{case}.json").read_text())
            if meta.get("type") not in names:
                continue
            data = (corpus / group / f"{case}.bin").read_bytes()
            (out / f"{group}-{case}").write_bytes(bytes([names.index(meta["type"])]) + data)
            count += 1
    print(f"{target}: {count} seeds, {len(names)} types")
