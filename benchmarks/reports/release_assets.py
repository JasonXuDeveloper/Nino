#!/usr/bin/env python3
"""The benchmark results attached to a release: their names, the manifest that lists them, and where they are kept
(a GitHub release, or a directory standing in for one in a dry run).

usage:
  release_assets.py fetch --tag TAG --dir DIR [--release-dir FAKE] [NAME...]
      the manifest and the named assets (every asset the manifest lists without NAME) into DIR
  release_assets.py has --tag TAG [--release-dir FAKE] NAME...
      prints true when the release has every NAME: listed in its manifest, or, for a release from before the manifest
      (v4.0.0-rc.9 and older), its section in the body (report.yml skips a benchmark the release already has)

Every asset is named by what it holds, the same for every release (the docs site, nino.docs
scripts/sync-benchmarks.ts, reads them by these names; change one only together with it):

  benchmarks-manifest.json         the manifest: {"schema": 1, "tag", "assets": [entry...]}
  dotnet-x64.md, dotnet-arm64.md   the merged BenchmarkDotNet report of every class (merge_reports.py), per architecture
  dotnet-v3-x64.md                 the merged report of v3 3.3.13's SimpleTest and PolymorphicTest (x64, before v4)
  runtime-<lang>-<arch>.md         the Go, Rust or TypeScript section (runtimes_report.py), lang go, rust, typescript
  runtime-go-<arch>.csv            benchstat's CSV the Go section is made from
  runtime-typescript-<arch>.json   tinybench's results the TypeScript section is made from
  unity-x64.md                     the Unity section (unity_report.py)
  build-x64.md                     the build section (generator_report.py)
  build-<v3|v4>-<roots>.json       Nino.GeneratorBenchmark's --json files the build section is made from

An entry is {"name", "kind" (dotnet, dotnet-v3, runtime, runtime-data, unity, build, build-data), "arch" (x64,
arm64), "lang" (runtimes), "format", "of" (a data file: the section it is made from), "environment" (the machine and
the tools, as the asset names them)}; release_body.py adds "title", "summary" and "chart" (the PNG's path on the
release-notes branch, and "chart_sha", its content's hash) to the entry of each section it draws.

The body's chart images live on the branch release-notes (charts/<tag>/<section>.png), not in the release's assets:
GitHub serves a release asset as application/octet-stream with Content-Disposition: attachment, which its image proxy
(camo) does not show, while raw.githubusercontent.com serves a PNG as image/png. The branch holds the git notes of
release_notes.py too; git notes keeps files whose paths are not commit ids as they are.
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

from bdn import cpu, rows

MANIFEST = "benchmarks-manifest.json"
SCHEMA = 1
REPO = os.environ.get("GITHUB_REPOSITORY", "JasonXuDeveloper/Nino")
CHARTS_BRANCH = "release-notes"
LANGS = ("go", "rust", "typescript")

# (name pattern, fields): the assets a release may have.
NAMES = [
    (r"dotnet-(?P<arch>x64|arm64)\.md", {"kind": "dotnet", "format": "benchmarkdotnet-github-markdown"}),
    (r"dotnet-v3-(?P<arch>x64)\.md", {"kind": "dotnet-v3", "format": "benchmarkdotnet-github-markdown"}),
    (r"runtime-(?P<lang>go|rust|typescript)-(?P<arch>x64|arm64)\.md", {"kind": "runtime", "format": "markdown-section"}),
    (r"runtime-(?P<lang>go)-(?P<arch>x64|arm64)\.csv", {"kind": "runtime-data", "format": "benchstat-csv"}),
    (r"runtime-(?P<lang>typescript)-(?P<arch>x64|arm64)\.json", {"kind": "runtime-data", "format": "tinybench-json"}),
    (r"unity-(?P<arch>x64)\.md", {"kind": "unity", "format": "markdown-section"}),
    (r"build-(?P<arch>x64)\.md", {"kind": "build", "format": "markdown-section"}),
    (r"build-(?:v3|v4)-\d+\.json", {"kind": "build-data", "format": "generator-benchmark-json", "arch": "x64"}),
]


def describe(name):
    """The fields of an asset's name; ValueError for a name outside NAMES."""
    for pattern, fields in NAMES:
        match = re.fullmatch(pattern, name)
        if match:
            entry = {"name": name, **fields, **{k: v for k, v in match.groupdict().items() if v}}
            if entry["kind"] == "runtime-data":
                entry["of"] = f"runtime-{entry['lang']}-{entry['arch']}.md"
            elif entry["kind"] == "build-data":
                entry["of"] = "build-x64.md"
            return entry
    raise ValueError(f"{name} is not a benchmark asset name (release_assets.py, NAMES)")


def bdn_environment(path):
    """'BenchmarkDotNet v0.15.8, Linux Ubuntu 26.04.1 LTS, .NET 10.0.14; runners: AMD EPYC 7763 3.10 GHz, ...'."""
    before, _ = rows(path)
    lines = [line.strip() for line in before if line.strip() and not line.startswith("```")]
    head = re.sub(r"\s*\([^)]*\)$", "", lines[0]) if lines else "BenchmarkDotNet"
    runtime = next((m.group(1) for line in lines if (m := re.search(r"\[Host\]\s*:\s*(\.NET [\d.]+)", line))), "")
    text = open(path, encoding="utf-8").read()
    # The runners merge_reports.py names after the table, else the CPU line of the environment block.
    machines = [m.group(1) for m in re.finditer(r"^- [^:]+: (.+?), \d+ CPU", text, re.M)]
    if not machines and len(lines) > 1:
        machines = [lines[1].split(",")[0]]
    machines = list(dict.fromkeys(cpu(m) for m in machines))
    return f"{head}, {runtime}; " + ("runners: " if len(machines) > 1 else "runner: ") + ", ".join(machines)


def section_environment(entry, text):
    """The machine a section names, as nino.docs reads it (runtimes), or its environment lines (Unity, build)."""
    kind = entry["kind"]
    if kind == "build":
        m = re.search(r"^Environment: (.+)$", text, re.M)
        return m.group(1).strip() if m else None
    if kind == "unity":
        return " ".join(line for line in text.splitlines() if line.startswith("Unity ")) or None
    if entry.get("lang") == "go":
        m = re.search(r"\bon ([^`]+?)\. Allocations", text)
        if m and m.group(1) != "a GitHub runner":
            return re.sub(r"\s+,", ",", cpu(m.group(1)))
    if entry.get("lang") == "typescript":
        m = re.search(r"\bin Node (v[\d.]+)", text)
        if m:
            return f"Node {m.group(1)}"
    return f"a GitHub {entry['arch']} runner"


def environment(entry, path):
    if entry["kind"] in ("dotnet", "dotnet-v3"):
        return bdn_environment(path)
    if entry["kind"] in ("runtime", "unity", "build"):
        return section_environment(entry, open(path, encoding="utf-8").read())
    return None


def new_manifest(tag):
    return {"schema": SCHEMA, "tag": tag, "assets": []}


def load(directory, tag=None):
    path = os.path.join(directory, MANIFEST)
    if not os.path.isfile(path):
        return new_manifest(tag)
    manifest = json.load(open(path, encoding="utf-8"))
    if manifest.get("schema") != SCHEMA:
        raise ValueError(f"{path}: schema {manifest.get('schema')}, this script reads {SCHEMA}")
    return manifest


def save(directory, manifest):
    order = {pattern: i for i, (pattern, _) in enumerate(NAMES)}

    def rank(entry):
        return next(i for p, i in order.items() if re.fullmatch(p, entry["name"])), entry.get("arch") != "x64", \
            entry["name"]

    manifest["assets"].sort(key=rank)
    with open(os.path.join(directory, MANIFEST), "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
        f.write("\n")


def entry(manifest, name):
    return next((e for e in manifest["assets"] if e["name"] == name), None)


def add(directory, manifest, name, source):
    """Copies `source` into the directory as `name` and lists it (replacing an entry of that name: a re-run)."""
    fields = describe(name)
    target = os.path.join(directory, name)
    if os.path.abspath(source) != os.path.abspath(target):
        shutil.copyfile(source, target)
    fields["environment"] = environment(fields, target)
    manifest["assets"] = [e for e in manifest["assets"] if e["name"] != name] + [
        {k: v for k, v in fields.items() if v is not None}]


def download_url(tag, name):
    return f"https://github.com/{REPO}/releases/download/{tag}/{name}"


def chart_url(path, sha):
    """A chart on the release-notes branch, through raw.githubusercontent.com (served as image/png); the hash changes
    the URL when the image does, so GitHub's image proxy never shows a cached old one."""
    return f"https://raw.githubusercontent.com/{REPO}/{CHARTS_BRANCH}/{path}?v={sha}"


def sha(path):
    return hashlib.sha256(open(path, "rb").read()).hexdigest()[:12]


class GitHub:
    """A release on GitHub, through gh (GH_TOKEN) and the checkout's git credentials (the chart branch)."""

    def __init__(self, tag):
        self.tag = tag
        self._release = None

    def release(self):
        if self._release is None:
            self._release = json.loads(subprocess.run(
                ["gh", "api", f"repos/{REPO}/releases/tags/{self.tag}"], check=True, stdout=subprocess.PIPE,
                encoding="utf-8").stdout)
        return self._release

    def names(self):
        return [a["name"] for a in self.release().get("assets", [])]

    def download(self, name, directory):
        asset = next((a for a in self.release().get("assets", []) if a["name"] == name), None)
        if asset is None:
            return False
        with open(os.path.join(directory, name), "wb") as f:
            subprocess.run(["gh", "api", "-H", "Accept: application/octet-stream",
                            f"repos/{REPO}/releases/assets/{asset['id']}"], check=True, stdout=f)
        return True

    def upload(self, paths):
        subprocess.run(["gh", "release", "upload", self.tag, "--repo", REPO, "--clobber", *paths], check=True)

    def body(self):
        self._release = None
        return self.release().get("body") or ""

    def edit_body(self, text):
        with tempfile.NamedTemporaryFile("w", suffix=".md", encoding="utf-8", delete=False) as f:
            f.write(text)
        try:
            subprocess.run(["gh", "release", "edit", self.tag, "--repo", REPO, "--notes-file", f.name], check=True)
        finally:
            os.unlink(f.name)

    def push_charts(self, files):
        """Commits the PNGs as charts/<tag>/<name> onto the release-notes branch (which holds the release notes'
        git notes too) and pushes it, again on a branch that moved meanwhile."""
        if not files:
            return

        def git(*args, env=None, out=False):
            return subprocess.run(["git", *args], check=True, env=env, encoding="utf-8",
                                  stdout=subprocess.PIPE if out else None).stdout

        name = "github-actions[bot]"
        email = "41898282+github-actions[bot]@users.noreply.github.com"
        for attempt in range(5):
            parent = []
            if subprocess.run(["git", "fetch", "-q", "--depth=1", "origin",
                               f"+refs/heads/{CHARTS_BRANCH}:refs/remotes/origin/{CHARTS_BRANCH}"]).returncode == 0:
                parent = [git("rev-parse", f"refs/remotes/origin/{CHARTS_BRANCH}", out=True).strip()]
            with tempfile.TemporaryDirectory() as tmp:
                env = {**os.environ, "GIT_INDEX_FILE": os.path.join(tmp, "index"), "GIT_AUTHOR_NAME": name,
                       "GIT_AUTHOR_EMAIL": email, "GIT_COMMITTER_NAME": name, "GIT_COMMITTER_EMAIL": email}
                if parent:
                    git("read-tree", parent[0], env=env)
                for path in files:
                    blob = git("hash-object", "-w", path, out=True).strip()
                    git("update-index", "--add", "--cacheinfo",
                        f"100644,{blob},charts/{self.tag}/{os.path.basename(path)}", env=env)
                tree = git("write-tree", env=env, out=True).strip()
                commit = git("commit-tree", tree, *sum((["-p", p] for p in parent), []), "-m",
                             f"docs(benchmarks): charts of {self.tag}", env=env, out=True).strip()
            if subprocess.run(["git", "push", "-q", "origin", f"{commit}:refs/heads/{CHARTS_BRANCH}"]).returncode == 0:
                print(f"✅ {len(files)} chart(s) on {CHARTS_BRANCH}: charts/{self.tag}/", file=sys.stderr)
                return
            print(f"⚠️ push to {CHARTS_BRANCH} refused (attempt {attempt + 1}): fetching again", file=sys.stderr)
        raise RuntimeError(f"could not push the charts to {CHARTS_BRANCH}")


class Local:
    """A directory standing in for a release in a dry run: body.md, assets/, and release-notes/ for the chart
    branch's files."""

    def __init__(self, directory, tag):
        self.dir, self.tag = directory, tag
        os.makedirs(os.path.join(directory, "assets"), exist_ok=True)

    def names(self):
        return sorted(os.listdir(os.path.join(self.dir, "assets")))

    def download(self, name, directory):
        source = os.path.join(self.dir, "assets", name)
        if not os.path.isfile(source):
            return False
        shutil.copyfile(source, os.path.join(directory, name))
        return True

    def upload(self, paths):
        for path in paths:
            shutil.copyfile(path, os.path.join(self.dir, "assets", os.path.basename(path)))

    def body(self):
        path = os.path.join(self.dir, "body.md")
        return open(path, encoding="utf-8").read() if os.path.isfile(path) else ""

    def edit_body(self, text):
        open(os.path.join(self.dir, "body.md"), "w", encoding="utf-8").write(text)

    def push_charts(self, files):
        target = os.path.join(self.dir, CHARTS_BRANCH, "charts", self.tag)
        os.makedirs(target, exist_ok=True)
        for path in files:
            shutil.copyfile(path, os.path.join(target, os.path.basename(path)))


def release(tag, release_dir=None):
    return Local(release_dir, tag) if release_dir else GitHub(tag)


def fetch(rel, directory, names=None):
    """The manifest and the named assets (all it lists without names) into the directory -> the manifest, or None
    when the release has none."""
    os.makedirs(directory, exist_ok=True)
    if not rel.download(MANIFEST, directory):
        return None
    manifest = load(directory)
    for name in [e["name"] for e in manifest["assets"]] if names is None else names:
        if not rel.download(name, directory):
            print(f"⚠️ the release has no {name}", file=sys.stderr)
    return manifest


def has(rel, names):
    """Every name in the release's manifest, or (a release from before the manifest) its section in the body."""
    import release_body
    with tempfile.TemporaryDirectory() as tmp:
        manifest = fetch(rel, tmp, []) or new_manifest(rel.tag)
    listed = {e["name"] for e in manifest["assets"]}
    legacy = set(release_body.legacy_sections(rel.body()))
    return all(name in listed or name in legacy for name in names)


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    commands = parser.add_subparsers(dest="command", required=True)
    one = commands.add_parser("fetch")
    one.add_argument("--dir", required=True)
    two = commands.add_parser("has")
    for command in (one, two):
        command.add_argument("--tag", required=True)
        command.add_argument("--release-dir", help="a directory standing in for the release (dry run)")
        command.add_argument("names", nargs="*")
    args = parser.parse_args()
    rel = release(args.tag, args.release_dir)
    if args.command == "fetch":
        if fetch(rel, args.dir, args.names or None) is None:
            sys.exit(f"{args.tag} has no {MANIFEST}")
    else:
        print(str(has(rel, args.names)).lower())


if __name__ == "__main__":
    main()
