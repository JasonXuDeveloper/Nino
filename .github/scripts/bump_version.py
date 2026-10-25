#!/usr/bin/env python3
"""Sets the version of every package a release publishes (release.yml, job "tag").

usage: bump_version.py <version> <unity version>

NuGet (Directory.Build.props), the Unity package (package.json, its OpenUPM version), the Rust crates
(nino-serialization and nino-serialization-derive, which the first pins exactly) and the npm package
nino-serialization all take the release's version; the Go module takes it as the tag runtimes/go/v<version>,
which the module path's major version (runtimes/go/v4) must match. Each file must change as expected, or this fails.
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def sub(path, pattern, repl, count=1, flags=0):
    """Replaces pattern in the file at path (count matches, all of them when 0), failing if it does not match."""
    file = ROOT / path
    text = file.read_text(encoding="utf-8")
    new, n = re.subn(pattern, repl, text, count=count, flags=flags)
    if n == 0:
        sys.exit(f"{path}: {pattern!r} not found")
    file.write_text(new, encoding="utf-8")


def bump_json(path, version, lock=False):
    """Sets "version" in a package.json (and, for a package-lock.json, that of its root package)."""
    file = ROOT / path
    data = json.loads(file.read_text(encoding="utf-8"))
    data["version"] = version
    if lock:
        data["packages"][""]["version"] = version
    file.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def main():
    version, unity = sys.argv[1], sys.argv[2]
    sub("Directory.Build.props", r"<Version>[^<]*</Version>", f"<Version>{version}</Version>")
    sub("src/Nino.Unity/Packages/com.jasonxudeveloper.nino/package.json",
        r'"version": "[^"]*"', f'"version": "{unity}"')

    # The first `version =` of a Cargo.toml is its package's.
    sub("runtimes/rust/Cargo.toml", r'(?m)^version = "[^"]*"', f'version = "{version}"')
    sub("runtimes/rust/nino-derive/Cargo.toml", r'(?m)^version = "[^"]*"', f'version = "{version}"')
    sub("runtimes/rust/Cargo.toml", r'(package = "nino-serialization-derive", version = )"[^"]*"',
        rf'\g<1>"={version}"')
    sub("runtimes/rust/Cargo.lock", r'(name = "nino-serialization(?:-derive)?"\nversion = )"[^"]*"',
        rf'\g<1>"{version}"', count=0)

    bump_json("runtimes/typescript/package.json", version)
    bump_json("runtimes/typescript/package-lock.json", version, lock=True)

    major = version.split(".")[0]
    module = re.search(r"(?m)^module (\S+)", (ROOT / "runtimes/go/go.mod").read_text(encoding="utf-8")).group(1)
    if int(major) >= 2 and not module.endswith(f"/v{major}"):
        sys.exit(f"runtimes/go/go.mod: module {module} cannot take the tag runtimes/go/v{version} (needs /v{major})")


if __name__ == "__main__":
    main()
