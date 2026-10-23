#!/usr/bin/env python3
"""Moves the test and benchmark projects out of src/ and rewrites every path that refers to them.

Layout it produces:

    Nino.slnx              the solution: src/Nino.slnx, or src/Nino.sln migrated with `dotnet sln migrate`
    Directory.Build.props  moved up from src/, with nuget.config and .editorconfig (and Directory.Build.targets,
                           Directory.Packages.props, global.json when src/ has them): MSBuild, NuGet and the analyzers
                           look for them in the project's directory and its parents, and tests/ and benchmarks/ are
                           not under src/
    src/                   the packages, paths unchanged (src/Nino.Unity too: OpenUPM publishes from it)
    tests/                 Nino.UnitTests, Nino.UnitTests.Subset, Nino.RuntimeTests, Nino.AotTests,
                           Nino.AspNetCore.Tests, Nino.TypeScript.Tests, and any other src/ project whose name ends in
                           Tests, Test or Fuzz (Nino.Fuzz, Nino.Codegen.Tests, ...)
    benchmarks/            Nino.Benchmark and Nino.GeneratorBenchmark (any src/ project named *Benchmark*), next to
                           the scripts

Rerun it after merging other branches, from the repository root of a checkout with no uncommitted changes, in
whatever state the tree is (before, partly or fully after the move):

    python3 tools/restructure.py            # move, rewrite, then check (needs the .NET SDK for `dotnet sln migrate`)
    python3 tools/restructure.py --check    # only the check
    python3 tools/restructure.py --dry-run  # print the moves and the files it would change, touch nothing

It is idempotent: a second run moves and changes nothing. It stages what it moves and changes (git mv, git add);
it never commits.

What it rewrites (every tracked text file except src/Nino.Unity, which it leaves byte for byte):
  * MSBuild files (*.csproj, *.props, *.targets): every path that starts with ../ or ..\\ (ProjectReference,
    Compile and None Include with their Link, Import Project, AdditionalFiles, Exclude, conditions) is resolved from
    the file's old directory, mapped to the new layout and made relative to the file's new directory, keeping its
    separator style and anything after a $(...), %(...) or wildcard segment.
  * Properties/launchSettings.json: targetProject, relative to the project directory.
  * The solution: each project's path, now relative to the root, grouped in /src/, /tests/ and /benchmarks/ folders.
  * Workflows (.github/workflows/*.yml): a working-directory of src (the old solution directory) becomes the
    repository root, any other one is mapped (./src/Nino.Benchmark -> ./benchmarks/Nino.Benchmark). Every path
    relative to a step's working directory (run commands, `cd` targets, matrix values such as
    project: Nino.UnitTests/Nino.UnitTests.csproj) is resolved from the old one and made relative to the new one;
    a path that starts with ./ keeps pointing where it did (./TestResults run in src becomes ./src/TestResults, which
    the upload step's path names). Keys whose values are relative to the workspace whatever the working directory
    (path, key, restore-keys, projectPath, hashFiles, uses) get only the root-relative rewrite below. The `paths:`
    filters that list 'src/**' also list the moved projects, the solution and the root files.
  * Markdown: links that start with ../ (relative to the file), and shell code blocks, where `cd src` is dropped and
    the commands after it run from the root (dotnet test Nino.UnitTests -> dotnet test tests/Nino.UnitTests).
  * Everywhere (scripts, docs, workflows, comments, .gitignore, C# comments): a path from the root such as
    src/Nino.Benchmark, $ROOT/src/Nino.Benchmark, head/src/Nino.Benchmark or src\\Nino.UnitTests becomes
    benchmarks/Nino.Benchmark (tests\\Nino.UnitTests ...); src/Nino.sln and src/Nino.slnx become Nino.slnx;
    src/Directory.Build.props, src/nuget.config ... lose their src/.
  * A few constructs no rule above can read, each matched exactly (SPECIAL below): benchmarks/asm.py's project path
    (with a fallback to src/Nino.Benchmark for commits from before the move, which `asm.py compare <ref>` builds),
    release.yml's `dotnet pack "$proj/$proj.csproj"`, dependabot's NuGet directories, the .gitignore comment.

What it checks (also after every run; exits 1 on any failure):
  * no tracked text file (but this script) still names an old path: src/<moved project>, src/Nino.sln(x), a root
    file under src/, `cd src` in a code block or workflow, os.path.join(..., "src", "<moved project>");
  * every ../ path in an MSBuild file, launchSettings.json or a Markdown link resolves to a tracked file or folder
    (up to its first $(...) or wildcard segment), and every project of Nino.slnx exists;
  * every working-directory of a workflow exists;
  * nothing is left tracked under an old place, and src/Nino.Unity is unchanged.
It also prints, for review, workflow lines whose cwd it changed that hold a path it cannot resolve ("$var/...").

Files other branches add are handled by the same rules when they follow the same patterns: a new src/*Tests,
src/*Fuzz or src/*Benchmark* project moves with the others; runtimes/ and conformance/ are left where they are, only
their references to moved paths are rewritten; a new workflow gets the working-directory and path rewrites. Read the
"review" lines and the check's output after each run.
"""

import argparse
import os
import posixpath
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True,
                      check=True).stdout.strip()
SELF = "tools/restructure.py"

OLD_SOLUTION_DIR = "src"
TESTS = ["Nino.UnitTests", "Nino.UnitTests.Subset", "Nino.RuntimeTests", "Nino.AotTests", "Nino.AspNetCore.Tests",
         "Nino.TypeScript.Tests", "Nino.Codegen.Tests", "Nino.Fuzz"]
BENCHMARKS = ["Nino.Benchmark", "Nino.GeneratorBenchmark"]
TEST_NAME = re.compile(r"(Tests?|Tests\.Subset|Fuzz\w*)$")
BENCHMARK_NAME = re.compile(r"Benchmark")
ROOT_FILES = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json",
              "nuget.config", "NuGet.config", "NuGet.Config", ".editorconfig"]
SOLUTIONS = ["src/Nino.sln", "src/Nino.slnx"]
SOLUTION = "Nino.slnx"
UNITY = "src/Nino.Unity"

TEXT_SUFFIXES = (".cs", ".csproj", ".props", ".targets", ".slnx", ".sln", ".json", ".md", ".yml", ".yaml", ".sh",
                 ".py", ".ts", ".mjs", ".js", ".txt", ".config", ".xml", ".toml", ".go", ".rs", ".ps1", ".cmd",
                 ".editorconfig", ".gitignore", ".gitattributes")
MSBUILD_SUFFIXES = (".csproj", ".props", ".targets")

# Keys of a workflow whose values are relative to the workspace, whatever the step's working directory.
ROOT_KEYS = {"path", "key", "restore-keys", "projectPath", "uses", "name", "if", "hashFiles", "artifactsPath"}


def git(*args, check=True):
    return subprocess.run(["git", "-C", ROOT, *args], capture_output=True, text=True, check=check).stdout


# ---------------------------------------------------------------------------------------------------------------------
# The tree and the moves

class Tree:
    def __init__(self):
        self.files = set(git("ls-files", "-z").split("\0")) - {""}
        self.dirs = {""}
        for f in self.files:
            d = posixpath.dirname(f)
            while d not in self.dirs:
                self.dirs.add(d)
                d = posixpath.dirname(d)

    def exists(self, p):
        return p in self.files or p in self.dirs


def build_moves(tree):
    """old path -> new path, for every place that moves (whether it has moved already or not)."""
    names = set(TESTS) | set(BENCHMARKS)
    for d in tree.dirs:
        parts = d.split("/")
        if len(parts) == 2 and parts[0] == "src" and (TEST_NAME.search(parts[1]) or BENCHMARK_NAME.search(parts[1])):
            names.add(parts[1])
    moves = {}
    for n in sorted(names):
        moves["src/" + n] = ("benchmarks/" if BENCHMARK_NAME.search(n) else "tests/") + n
    for f in ROOT_FILES:
        moves["src/" + f] = f
    for s in SOLUTIONS:
        moves[s] = SOLUTION
    return moves


class Mapper:
    def __init__(self, moves):
        self.moves = moves
        self.keys = sorted(moves, key=len, reverse=True)

    def map(self, p):
        for k in self.keys:
            if p == k or p.startswith(k + "/"):
                return self.moves[k] + p[len(k):]
        return p

    def new_cwd(self, c):
        """A working directory: the old solution directory becomes the root, a moved one its new place."""
        return "" if c == OLD_SOLUTION_DIR else self.map(c)


def norm(p):
    p = posixpath.normpath(p) if p else ""
    return "" if p == "." else p


def rel(target, base):
    return posixpath.relpath(target or ".", base or ".")


# ---------------------------------------------------------------------------------------------------------------------
# Rewrite rules

DYNAMIC = re.compile(r"[$%@*(]")
REL_TOKEN = re.compile(r"""(?<![\w)\]}$%./\\-])((?:\.\.[/\\])+[^\s"'<>;|`,)\]#]*)""")


def static_split(t):
    """'../a/b/$(X)/c' -> (['..', 'a', 'b'], ['$(X)', 'c'])"""
    segs = t.split("/")
    for i, s in enumerate(segs):
        if DYNAMIC.search(s):
            return segs[:i], segs[i:]
    return segs, []


def resolvable(tree, r, dynamic):
    """Whether r is a real place: tracked, or (before a dynamic segment or an output folder) inside a project."""
    if tree.exists(r):
        return True
    a = r
    while a.count("/") >= 1:
        a = posixpath.dirname(a)
        if a.count("/") >= 1 and tree.exists(a):
            return True
    return False


def rewrite_relative(tok, base_old, base_new, tree, mapper):
    """A path that starts with ../ (from a file in base_old, now in base_new)."""
    sep = "\\" if "\\" in tok else "/"
    t = tok.replace("\\", "/")
    trailing = t.endswith("/")
    static, rest = static_split(t.rstrip("/") if not trailing else t[:-1])
    r = norm(posixpath.join(base_old, *static)) if static else base_old
    if r.startswith(".."):
        return tok
    if not resolvable(tree, r, bool(rest)):
        return tok
    new_r = mapper.map(r)
    if new_r == r and base_old == base_new:
        return tok
    out = "/".join([rel(new_r, base_new)] + rest)
    if trailing:
        out += "/"
    return out.replace("/", sep)


def rewrite_rel_tokens(text, base_old, base_new, tree, mapper):
    return REL_TOKEN.sub(lambda m: rewrite_relative(m.group(1), base_old, base_new, tree, mapper), text)


RELOCATED = set()  # (old place, new place) of the outputs of commands whose working directory changed
SHELL_TOKEN = re.compile(r"(?<![\w$./\\@{}%:=+-])((?:\.\.?/)*[A-Za-z0-9_][\w.+-]*(?:/[\w.*+-]*)*)")


def rewrite_cwd_token(tok, c, n, tree, mapper, bare_ok):
    """A path relative to a working directory that was c and is n."""
    if c is None:
        return tok
    explicit = tok.startswith("./") or tok.startswith("../")
    trailing = tok.endswith("/") and len(tok) > 1
    t = tok[:-1] if trailing else tok
    if not explicit:
        if "/" in t:
            if not tree.exists(norm(posixpath.join(c, t.split("/")[0]))):
                return tok
        elif not (bare_ok and norm(posixpath.join(c, t)) in mapper.moves):
            return tok
    r = norm(posixpath.join(c, t))
    if r.startswith(".."):
        return tok
    new_r = mapper.map(r)
    if new_r == r and c == n:
        return tok
    if tok.startswith("./") and n is not None and not resolvable(tree, r, True):
        # An output (./TestResults, ./aot-tests): it is made in the new working directory now.
        first = t[2:].split("/")[0]
        RELOCATED.add((posixpath.join(c, first), posixpath.join(n, first)))
        return tok
    out = rel(new_r, n)
    if tok.startswith("./") and not out.startswith(".."):
        out = "./" + out if out != "." else "."
    if trailing:
        out += "/"
    return out


def split_comment(line):
    """(code, comment) of a shell line: the comment starts at a # after a blank, outside quotes and ${{ }}."""
    quote = None
    depth = 0
    for i, ch in enumerate(line):
        if quote:
            if ch == quote:
                quote = None
        elif ch in "'\"":
            quote = ch
        elif line.startswith("${{", i):
            depth += 1
        elif line.startswith("}}", i) and depth:
            depth -= 1
        elif ch == "#" and depth == 0 and (i == 0 or line[i - 1] in " \t"):
            return line[:i], line[i:]
    return line, ""


def rewrite_shell_line(line, c, n, tree, mapper):
    code, comment = split_comment(line)
    code = SHELL_TOKEN.sub(lambda m: rewrite_cwd_token(m.group(1), c, n, tree, mapper, True), code)
    comment = SHELL_TOKEN.sub(lambda m: rewrite_cwd_token(m.group(1), c, n, tree, mapper, False), comment)
    return code + comment


CD = re.compile(r"^(\s*)cd\s+(\S+)\s*$")


def shell_block(lines, c, n, tree, mapper):
    """Lines of shell run with working directory c (now n), following `cd`. Returns the new lines."""
    out = []
    for line in lines:
        m = CD.match(line)
        if m and c is not None:
            target = m.group(2).strip("'\"")
            if DYNAMIC.search(target) or target.startswith("~") or target.startswith("/"):
                c = n = None
                out.append(line)
                continue
            old = norm(posixpath.join(c, target))
            new = mapper.new_cwd(old)
            hop = rel(new, n)
            c, n = old, new
            if hop == ".":
                continue  # `cd src`: the commands after it run from the root
            out.append(f"{m.group(1)}cd {hop}")
            continue
        out.append(rewrite_shell_line(line, c, n, tree, mapper))
    return out


def root_regex(mapper):
    names = sorted((k[len("src/"):] for k in mapper.moves), key=len, reverse=True)
    alt = "|".join(re.escape(n) for n in names)
    path = re.compile(r"(?<![\w.-])src([/\\])(" + alt + r")(?![\w.-])")
    join = re.compile(r"""(["'])src\1(\s*,\s*)(["'])(""" + alt + r")\3")
    return path, join


def rewrite_root_paths(text, mapper):
    path, join = root_regex(mapper)

    def sub_path(m):
        return mapper.map("src/" + m.group(2)).replace("/", m.group(1))

    def sub_join(m):
        new = mapper.map("src/" + m.group(4)).split("/")
        q = m.group(1)
        return (m.group(2)).join(f"{q}{s}{q}" for s in new)

    return join.sub(sub_join, path.sub(sub_path, text))


# --- Markdown

FENCE = re.compile(r"^\s*(```|~~~)\s*([\w+-]*)")
SHELL_LANGS = {"", "sh", "bash", "shell", "console", "zsh", "ps", "powershell", "pwsh", "cmd", "bat"}
MD_LINK = re.compile(r"(\]\(|<(?:img|a)\s[^>]*?(?:src|href)=\")(\.\.[/\\][^)\s\"#]*)")


def rewrite_markdown(text, path, new_path, tree, mapper):
    base_old, base_new = posixpath.dirname(path), posixpath.dirname(new_path)
    lines = text.split("\n")
    out, block, lang = [], None, None
    for line in lines:
        f = FENCE.match(line)
        if block is None:
            if f:
                block, lang = [], f.group(2).lower()
                out.append(line)
                continue
            out.append(MD_LINK.sub(lambda m: m.group(1) + rewrite_relative(m.group(2), base_old, base_new, tree, mapper),
                                   line))
        else:
            if f and not f.group(2):
                out.extend(shell_block(block, "", "", tree, mapper) if lang in SHELL_LANGS else block)
                out.append(line)
                block = None
                continue
            block.append(line)
    if block is not None:
        out.extend(block)
    return "\n".join(out)


# --- Workflows

KEY_LINE = re.compile(r"^(\s*)(-\s+)?([\w.-]+):(?:\s+(.*?))?\s*$")
WD_LINE = re.compile(r"^(\s*(?:-\s+)?working-directory:\s*)(['\"]?)([^'\"#]*?)\2(\s*(?:#.*)?)$")


def indent(line):
    return len(line) - len(line.lstrip(" "))


def workflow_cwds(lines):
    """The working directory of each line ('' = the workspace) from defaults.run and step working-directory."""
    n = len(lines)
    code = [bool(l.strip()) and not l.strip().startswith("#") for l in lines]
    ind = [indent(l) for l in lines]

    def end(i):
        for j in range(i + 1, n):
            if code[j] and ind[j] <= ind[i]:
                return j
        return n

    scopes = []
    for i, l in enumerate(lines):
        m = WD_LINE.match(l)
        if not m or not code[i]:
            continue
        value = m.group(3).strip()
        parents, cur = [], ind[i]
        if l.lstrip().startswith("-"):
            parents.append(i)  # `- working-directory:` as a step's first key
        else:
            for j in range(i - 1, -1, -1):
                if code[j] and ind[j] < cur:
                    parents.append(j)
                    cur = ind[j]
                    if cur == 0:
                        break
        if not parents:
            continue
        p = parents[0]
        if lines[p].lstrip().startswith("-"):
            scopes.append((p, end(p), value, i))
        elif lines[p].strip() == "run:" and len(parents) > 1 and lines[parents[1]].strip() == "defaults:":
            d = parents[1]
            if ind[d] == 0:
                scopes.append((0, n, value, i))
            elif len(parents) > 2:
                scopes.append((parents[2], end(parents[2]), value, i))
    cwd = [""] * n
    for start, stop, value, _ in sorted(scopes, key=lambda s: s[0] - s[1]):
        c = None if "${{" in value or "$" in value else norm(value[2:] if value.startswith("./") else value)
        for k in range(start, stop):
            cwd[k] = c
    return cwd, [s[3] for s in scopes]


def rewrite_workflow(text, tree, mapper, review):
    RELOCATED.clear()
    lines = text.split("\n")
    cwd, wd_lines = workflow_cwds(lines)
    out = []
    i = 0
    block_key, block_col = None, -1
    run_lines = []  # the pending lines of a run: block, with their cwd

    def flush():
        if run_lines:
            c = run_lines[0][1]
            n = None if c is None else mapper.new_cwd(c)
            new = shell_block([l for l, _ in run_lines], c, n, tree, mapper)
            out.extend(new)
            if c is not None and c != n:
                for l in new:
                    if re.search(r"(?<![\w$/])\$(?!GITHUB_|RUNNER_|HOME|\{\{)\w+/", split_comment(l)[0]):
                        review.append(l.strip())
            run_lines.clear()

    for i, line in enumerate(lines):
        stripped = line.strip()
        in_block = block_key is not None and (not stripped or indent(line) > block_col)
        if in_block:
            if block_key == "run":
                run_lines.append((line, cwd[i]))
                continue
            c = cwd[i]
            if block_key not in ROOT_KEYS and c is not None:
                line = SHELL_TOKEN.sub(lambda mm: rewrite_cwd_token(mm.group(1), c, mapper.new_cwd(c), tree, mapper,
                                                                    False), line)
            out.append(line)
            continue
        flush()
        block_key = None
        c = cwd[i]
        n = None if c is None else mapper.new_cwd(c)
        m = KEY_LINE.match(line)
        key = m.group(3) if m else None
        value = (m.group(4) or "") if m else ""
        if m and re.fullmatch(r"[|>][-+]?", value):
            block_key, block_col = key, m.start(3)
        wd = WD_LINE.match(line)
        if wd and i in wd_lines:
            v = wd.group(3).strip()
            if "$" not in v:
                old = norm(v[2:] if v.startswith("./") else v)
                new = mapper.new_cwd(old)
                nv = "." if new == "" else ("./" + new if v.startswith("./") else new)
                line = f"{wd.group(1)}{wd.group(2)}{nv}{wd.group(2)}{wd.group(4)}"
            out.append(line)
            continue
        if m and key == "run" and value and not block_key:
            prefix = line[:m.start(4)]
            new = shell_block([value], c, n, tree, mapper)
            out.append(prefix + (new[0] if new else value))
            continue
        if c is not None and key not in ROOT_KEYS and not stripped.startswith("#"):
            line = SHELL_TOKEN.sub(lambda mm: rewrite_cwd_token(mm.group(1), c, n, tree, mapper, False), line)
        out.append(line)
    flush()
    # The workspace-relative paths (upload-artifact's path, ...) of outputs now made in another directory.
    for i in range(len(out)):
        for was, now in RELOCATED:
            out[i] = re.sub(r"(?<![\w.-])(\./)?" + re.escape(was) + r"(?![\w.-])", lambda mm: (mm.group(1) or "") + now,
                            out[i])
    RELOCATED.clear()
    return "\n".join(out)


PATHS_SRC = re.compile(r"^(\s*)-\s*(['\"]?)src/\*\*\2\s*$")


def add_path_filters(text, tree, mapper):
    """A `paths:` filter that lists src/** also lists what moved out of src/."""
    present = [new for old, new in mapper.moves.items() if tree.exists(old) or tree.exists(new)]
    extra = sorted({"tests/**" if new.startswith("tests/") else new + ("/**" if "/" in new else "")
                    for new in present})
    lines = text.split("\n")
    out = []
    for i, line in enumerate(lines):
        out.append(line)
        m = PATHS_SRC.match(line)
        if not m:
            continue
        listed = set()
        j = i + 1
        while j < len(lines) and re.match(r"^\s*-\s", lines[j]) and indent(lines[j]) == indent(line):
            listed.add(lines[j].strip()[1:].strip().strip("'\""))
            j += 1
        j = i - 1
        while j >= 0 and re.match(r"^\s*-\s", lines[j]) and indent(lines[j]) == indent(line):
            listed.add(lines[j].strip()[1:].strip().strip("'\""))
            j -= 1
        q = m.group(2) or "'"
        for e in extra:
            if e not in listed:
                out.append(f"{m.group(1)}- {q}{e}{q}")
    return "\n".join(out)


# --- Exact rewrites of what no rule above can read. Each is (file pattern, old, new); old is gone once applied.

SPECIAL = [
    (r"benchmarks/asm\.py",
     '        project = os.path.join(tree, "src", "Nino.Benchmark")\n',
     '        # benchmarks/Nino.Benchmark; a commit from before the test and benchmark projects left src/ has it there\n'
     '        project = next(p for p in (os.path.join(tree, d, "Nino.Benchmark") for d in ("benchmarks", "src"))\n'
     '                       if os.path.isdir(p))\n'),
    (r"benchmarks/asm\.py",
     "`dump` builds src/Nino.Benchmark (of the working tree",
     "`dump` builds benchmarks/Nino.Benchmark (of the working tree"),
    (r"\.github/workflows/.*\.ya?ml", 'dotnet pack "$proj/$proj.csproj"', 'dotnet pack "src/$proj/$proj.csproj"'),
    (r"\.github/dependabot\.yml",
     '    directory: "/src"\n',
     '    directories:\n'
     '      - "/src/*"\n'
     '      - "/tests/*"\n'
     '      - "/benchmarks/Nino.Benchmark"\n'
     '      - "/benchmarks/Nino.GeneratorBenchmark"\n'),
    (r"\.gitignore",
     "(.NET projects under src/, tools/ and benchmarks/;",
     "(.NET projects under src/, tests/, tools/ and benchmarks/;"),
]


def apply_special(path, text):
    for pattern, old, new in SPECIAL:
        if re.fullmatch(pattern, path) and old in text:
            text = text.replace(old, new)
    return text


# --- The solution

def rewrite_solution(text, base_old, mapper):
    root = ET.fromstring(text)
    projects, others, folders = [], [], {}
    for child in list(root):
        if child.tag == "Folder":
            rest = [g for g in child if g.tag != "Project"]
            projects.extend(g for g in child if g.tag == "Project")
            if rest:
                f = ET.Element("Folder", child.attrib)
                f.extend(rest)
                folders[child.get("Name")] = f
        elif child.tag == "Project":
            projects.append(child)
        else:
            others.append(child)
        root.remove(child)
    for p in projects:
        r = norm(posixpath.join(base_old, p.get("Path").replace("\\", "/")))
        p.set("Path", rel(mapper.map(r), ""))
    for f in folders.values():
        for g in f.iter("File"):
            r = norm(posixpath.join(base_old, g.get("Path").replace("\\", "/")))
            g.set("Path", rel(mapper.map(r), ""))
    for p in sorted(projects, key=lambda p: p.get("Path").lower()):
        top = p.get("Path").split("/")[0] if "/" in p.get("Path") else ""
        if not top:
            others.append(p)
            continue
        name = f"/{top}/"
        folders.setdefault(name, ET.Element("Folder", {"Name": name})).append(p)
    order = {"/src/": 0, "/tests/": 1, "/benchmarks/": 2}
    root.extend(others)
    root.extend(folders[k] for k in sorted(folders, key=lambda k: (order.get(k, 9), k)))
    ET.indent(root, "  ")
    return ET.tostring(root, encoding="unicode") + "\n"


# ---------------------------------------------------------------------------------------------------------------------

def is_text(path):
    name = posixpath.basename(path)
    return name.endswith(TEXT_SUFFIXES) or name in (".gitignore", ".gitattributes", ".editorconfig")


def rewrite_file(path, text, tree, mapper, review):
    new_path = mapper.map(path)
    name = posixpath.basename(path)
    text = apply_special(path, text)
    if path.endswith(MSBUILD_SUFFIXES):
        text = rewrite_rel_tokens(text, posixpath.dirname(path), posixpath.dirname(new_path), tree, mapper)
    elif name == "launchSettings.json":
        project_old = posixpath.dirname(posixpath.dirname(path))
        project_new = posixpath.dirname(posixpath.dirname(new_path))
        text = rewrite_rel_tokens(text, project_old, project_new, tree, mapper)
    elif path.endswith(".md"):
        text = rewrite_markdown(text, path, new_path, tree, mapper)
    elif re.fullmatch(r"\.github/workflows/[^/]+\.ya?ml", path):
        text = rewrite_workflow(text, tree, mapper, review)
        text = add_path_filters(text, tree, mapper)
    return rewrite_root_paths(text, mapper)


def text_files(tree):
    for f in sorted(tree.files):
        if f == SELF or f == UNITY or f.startswith(UNITY + "/") or not is_text(f):
            continue
        if f.endswith(".sln") or f.endswith(".slnx"):
            continue
        yield f


def read(path):
    with open(os.path.join(ROOT, path), encoding="utf-8", newline="") as fh:
        return fh.read()


def write(path, text):
    full = os.path.join(ROOT, path)
    os.makedirs(os.path.dirname(full) or ".", exist_ok=True)
    with open(full, "w", encoding="utf-8", newline="") as fh:
        fh.write(text)


def unity_digest():
    return git("ls-files", "-s", UNITY)


def apply(dry_run):
    if git("status", "--porcelain", "--untracked-files=no").strip() and not dry_run:
        sys.exit("restructure: commit or stash your changes first")
    tree = Tree()
    mapper = Mapper(build_moves(tree))
    unity_before = unity_digest()
    moves = [(old, new) for old, new in mapper.moves.items() if tree.exists(old) and old not in SOLUTIONS]
    for old, new in moves:
        if tree.exists(new):
            sys.exit(f"restructure: both {old} and {new} exist; merge them by hand, then rerun")
    print("moves:")
    for old, new in moves:
        print(f"  {old} -> {new}")
    sln = [s for s in SOLUTIONS if os.path.exists(os.path.join(ROOT, s))]
    if sln:
        print(f"  {' and '.join(sln)} -> {SOLUTION}")

    review = []
    changed = []
    for f in text_files(tree):
        try:
            text = read(f)
        except UnicodeDecodeError:
            continue
        new = rewrite_file(f, text, tree, mapper, review)
        if new != text:
            changed.append(f)
            if not dry_run:
                write(f, new)

    # The solution.
    slnx_text, slnx_base = None, None
    if os.path.exists(os.path.join(ROOT, "src/Nino.slnx")):
        slnx_text, slnx_base = read("src/Nino.slnx"), "src"
    elif os.path.exists(os.path.join(ROOT, "src/Nino.sln")):
        if not dry_run:
            subprocess.run(["dotnet", "sln", os.path.join(ROOT, "src/Nino.sln"), "migrate"], check=True,
                           stdout=subprocess.DEVNULL)
            slnx_text, slnx_base = read("src/Nino.slnx"), "src"
    elif os.path.exists(os.path.join(ROOT, SOLUTION)):
        slnx_text, slnx_base = read(SOLUTION), ""
    print(f"changed: {len(changed)} files")
    for f in changed:
        print(f"  {f}")
    if dry_run:
        return review

    for old, new in moves:
        os.makedirs(os.path.dirname(os.path.join(ROOT, new)) or ROOT, exist_ok=True)
        git("mv", old, new)
    if slnx_text is not None:
        solution = rewrite_solution(slnx_text, slnx_base, mapper)
        for s in SOLUTIONS:
            if s in tree.files:
                git("rm", "-q", "--cached", s)
            if os.path.exists(os.path.join(ROOT, s)):
                os.remove(os.path.join(ROOT, s))
        if not os.path.exists(os.path.join(ROOT, SOLUTION)) or read(SOLUTION) != solution:
            write(SOLUTION, solution)
        git("add", SOLUTION)
    paths = [mapper.map(f) for f in changed]
    if paths:
        git("add", "--", *paths)

    # What a build left in the old places (bin/, obj/, TestResults/): ignored files only, so it can go.
    for old, _ in moves:
        full = os.path.join(ROOT, old)
        if os.path.isdir(full):
            left = [l for l in git("status", "--porcelain", "--ignored", "--", old).splitlines()
                    if not l.startswith("!!")]
            if left:
                print(f"warning: {old} still holds untracked files, left in place:\n  " + "\n  ".join(left))
            else:
                shutil.rmtree(full)
    if unity_digest() != unity_before:
        sys.exit("restructure: src/Nino.Unity changed")
    return review


# ---------------------------------------------------------------------------------------------------------------------
# The check

def check():
    tree = Tree()
    mapper = Mapper(build_moves(tree))
    path_re, join_re = root_regex(mapper)
    problems = []
    for old in mapper.moves:
        if any(f == old or f.startswith(old + "/") for f in tree.files):
            problems.append(f"{old}: still tracked")
    if SOLUTION not in tree.files:
        problems.append(f"{SOLUTION}: missing")
    cd_src = re.compile(r"^\s*(?:-\s+)?(?:run:\s*)?cd\s+(?:\./)?src/?\s*(?:$|&&|;)")
    for f in sorted(tree.files):
        if f == SELF or not is_text(f) or not os.path.exists(os.path.join(ROOT, f)):
            continue
        try:
            text = read(f)
        except UnicodeDecodeError:
            continue
        for no, line in enumerate(text.split("\n"), 1):
            for rx in (path_re, join_re):
                for m in rx.finditer(line):
                    problems.append(f"{f}:{no}: old path {m.group(0)!r}")
            if (f.endswith(".md") or f.startswith(".github/workflows/")) and cd_src.search(line):
                problems.append(f"{f}:{no}: cd src")
        base = posixpath.dirname(f)
        tokens = []
        if f.endswith(MSBUILD_SUFFIXES):
            tokens = [m.group(1) for m in REL_TOKEN.finditer(text)]
        elif posixpath.basename(f) == "launchSettings.json":
            base = posixpath.dirname(base)
            tokens = [m.group(1) for m in REL_TOKEN.finditer(text)]
        elif f.endswith(".md"):
            tokens = [m.group(2) for m in MD_LINK.finditer(text)]
        for t in tokens:
            static, _ = static_split(t.replace("\\", "/").rstrip("/"))
            r = norm(posixpath.join(base, *static))
            if r.startswith("..") or (f.endswith(".md") and not static):
                continue
            if not resolvable(tree, r, True) and not (f.startswith(UNITY + "/")):
                problems.append(f"{f}: {t} -> {r} does not exist")
        if f.startswith(".github/workflows/"):
            for no, line in enumerate(text.split("\n"), 1):
                m = WD_LINE.match(line)
                if m and "$" not in m.group(3):
                    v = norm(m.group(3).strip().removeprefix("./"))
                    if not tree.exists(v):
                        problems.append(f"{f}:{no}: working-directory {v} does not exist")
    if SOLUTION in tree.files:
        for p in ET.fromstring(read(SOLUTION)).iter("Project"):
            if p.get("Path").replace("\\", "/") not in tree.files:
                problems.append(f"{SOLUTION}: {p.get('Path')} does not exist")
    return problems


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--check", action="store_true", help="only check that no old path remains")
    ap.add_argument("--dry-run", action="store_true", help="print the moves and the files to change")
    args = ap.parse_args()
    if not args.check:
        review = apply(args.dry_run)
        if review:
            print("review: workflow lines whose working directory changed, with a path from a variable:")
            for l in review:
                print("  " + l)
        if args.dry_run:
            return
    problems = check()
    for p in problems:
        print("check: " + p)
    if problems:
        sys.exit(1)
    print("check: no old path remains")


if __name__ == "__main__":
    main()
