#!/usr/bin/env python3
"""Writes src/nuget-readme.md, the README every NuGet package carries, from README.md.

    python3 .github/scripts/nuget_readme.py            # rewrite src/nuget-readme.md
    python3 .github/scripts/nuget_readme.py --check    # fail when src/nuget-readme.md is not what README.md gives (CI)

Why a second file: nuget.org renders a package's README with Markdig and `DisableHtml()` (NuGetGallery,
src/NuGetGallery/Services/MarkdownService.cs), so every HTML tag of README.md (the centered hero, `<br>`, the
`<details>` and `<picture>` of the charts, `<sub>`) shows as literal text there. And a package's README is frozen when
release.yml packs it, while report.yml fills README.md's `<!--bench:...-->` numbers only after the release: a packed
copy would show "—" or the previous release's numbers forever. So the NuGet README is README.md in plain Markdown,
without a single measured number:

- the hero: the title, the tagline and the badges as Markdown, each `<br>` a hard line break (a trailing backslash),
  the logo left out (nuget.org shows the package icon beside the title);
- every block (paragraph, list item, table) that holds a benchmark marker is replaced by REPLACE's text for its
  section and first marker, or dropped when that text is empty; a marker REPLACE does not know fails the script, so a
  new number in README.md is never packed by accident;
- the overview chart is docs/assets/benchmark.png by its raw URL on main, which report.yml redraws for each release.

Edit README.md, never src/nuget-readme.md, and run this script; Directory.Build.props packs src/nuget-readme.md as each
package's README.md. Since every marker's block is replaced whatever it holds, report.yml's commits that refill the
markers leave src/nuget-readme.md unchanged.
"""

import argparse
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
SOURCE = ROOT / "README.md"
TARGET = ROOT / "src" / "nuget-readme.md"

GITHUB = "https://github.com/JasonXuDeveloper/Nino"
CHART = "https://raw.githubusercontent.com/JasonXuDeveloper/Nino/main/docs/assets/benchmark.png"
SITE_BENCHMARKS = "https://nino.xgamedev.net/en/docs/v4/benchmarks"
LIBRARIES = "MemoryPack, MessagePack, protobuf-net, Google.Protobuf and System.Text.Json"

HEADER = ("<!-- Generated from README.md by .github/scripts/nuget_readme.py: edit README.md and run the script. "
          "nuget.org removes this comment. -->")

# (section, first marker of the block) -> what the NuGet README says instead; "" drops the block.
REPLACE = {
    ("Why Nino", "ser.range"):
        f"- **Fastest.** Measured for every release on GitHub's runners against {LIBRARIES}, on 12 real-world "
        "workloads. [Benchmarks](#benchmarks)",
    ("Benchmarks", "ser.range"):
        f"![Time per operation of Nino v4 against v3 and {LIBRARIES}, serialize and deserialize]({CHART})\n\n"
        f"Nino is measured against {LIBRARIES} for every release, on GitHub's runners; the chart is the newest "
        f"release's. Every number is on the [benchmarks page]({SITE_BENCHMARKS}) and in the "
        f"[Benchmarks section on GitHub]({GITHUB}#benchmarks).",
    ("Benchmarks", "charts"): "",
    ("Benchmarks", "env.bdn"): "",
    ("v4 vs v3", "v3.ser"):
        f"Serialize, deserialize and build time against v3 3.3.13, measured on the same machine for every release: "
        f"[v4 vs v3 on GitHub]({GITHUB}#v4-vs-v3).",
}

# Sentences of README.md that speak of a number the NuGet README leaves out: (section, regular expression).
DROP = [
    ("Benchmarks", r"\s+A\s+range\s+covers\s+the\s+workloads\s+Nino\s+wins\s+by\s+more\s+than\s+5%;\s+every\s+time\s+is\s+in\s+the\s+docs\."),
]

MARKER = re.compile(r"<!--bench:([\w.]+)-->")


class ReadmeError(Exception):
    pass


def hero(text):
    """README's centered hero (`<div align="center">` ... `</div>`) as plain Markdown."""
    m = re.match(r'<div align="center">\n(.*?)\n</div>\n', text, re.S)
    if not m:
        raise ReadmeError('README.md no longer starts with <div align="center">: update hero()')
    lines = []
    for line in m.group(1).split("\n"):
        if re.fullmatch(r"<img [^>]*>", line.strip()):
            continue  # the logo: nuget.org shows the package icon
        lines.append(re.sub(r"<br>$", "\\\\", line))
    body = re.sub(r"\n{3,}", "\n\n", "\n".join(lines)).strip("\n")
    return body + "\n" + text[m.end():]


def blocks(text):
    """The blocks of TEXT (separated by blank lines), each fenced code block whole."""
    out, cur, fence = [], [], None
    for line in text.split("\n"):
        if fence:
            cur.append(line)
            if line.strip() == fence:
                fence = None
            continue
        f = re.match(r"\s*(```+|~~~+)", line)
        if f:
            fence = f.group(1)
            cur.append(line)
            continue
        if line.strip():
            cur.append(line)
        elif cur:
            out.append("\n".join(cur))
            cur = []
    if cur:
        out.append("\n".join(cur))
    return out


def items(block):
    """A list block split into its items (a line starting `- ` starts one)."""
    out = []
    for line in block.split("\n"):
        if line.startswith("- ") or not out:
            out.append(line)
        else:
            out[-1] += "\n" + line
    return out


def replacement(section, piece):
    key = (section, MARKER.search(piece).group(1))
    if key not in REPLACE:
        raise ReadmeError(f"README.md's section '{section}' has a benchmark marker '{key[1]}' that "
                          f"nuget_readme.py's REPLACE does not know: add what the NuGet README says instead")
    return REPLACE[key]


def convert(readme):
    text = hero(readme)
    # A marker that spans lines (the charts) becomes an empty one, so its block is one block.
    text = re.sub(r"(<!--bench:[\w.]+-->)\n.*?(<!--/bench-->)", r"\1\2", text, flags=re.S)
    section, out, dropped = "", [], set()
    for block in blocks(text):
        heading = re.match(r"## (.+)", block)
        if heading:
            section = heading.group(1).strip()
        for i, (where, pattern) in enumerate(DROP):
            if where == section and re.search(pattern, block):
                block = re.sub(pattern, "", block)
                dropped.add(i)
        if not MARKER.search(block):
            out.append(block)
        elif block.startswith("- "):
            kept = [replacement(section, it) if MARKER.search(it) else it for it in items(block)]
            out.append("\n".join(it for it in kept if it))
        else:
            new = replacement(section, block)
            if new:
                out.append(new)
    missing = [DROP[i] for i in range(len(DROP)) if i not in dropped]
    if missing:
        raise ReadmeError(f"nuget_readme.py's DROP no longer matches README.md: {missing}")
    result = HEADER + "\n\n" + "\n\n".join(out) + "\n"
    check(result)
    return result


def check(markdown):
    """Fail on anything nuget.org would print as text or blank: HTML outside code, relative links."""
    problems = []
    fence = None
    for n, line in enumerate(markdown.split("\n"), 1):
        if fence:
            fence = None if line.strip() == fence else fence
            continue
        f = re.match(r"\s*(```+|~~~+)", line)
        if f:
            fence = f.group(1)
            continue
        if n == 1 and line == HEADER:
            continue
        prose = re.sub(r"`[^`]*`", "", line)
        if re.search(r"<(?!https?:)[A-Za-z/!]", prose):
            problems.append(f"line {n}: HTML, which nuget.org shows as text: {line.strip()}")
        for url in re.findall(r"\]\(([^)\s]+)", prose):
            if not url.startswith(("https://", "#")):
                problems.append(f"line {n}: a link nuget.org blanks (only https and #anchors work): {url}")
    if problems:
        raise ReadmeError("src/nuget-readme.md would not render on nuget.org:\n  " + "\n  ".join(problems))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check", action="store_true", help="fail when src/nuget-readme.md is out of date")
    args = parser.parse_args()
    try:
        markdown = convert(SOURCE.read_text(encoding="utf-8"))
    except ReadmeError as e:
        sys.exit(f"nuget_readme.py: {e}")
    if args.check:
        # The markers' contents (report.yml refills them after each release) must not reach the NuGet README.
        refilled = re.sub(r"(<!--bench:[\w.]+-->).*?(<!--/bench-->)", r"\1 9.99 \2", SOURCE.read_text(encoding="utf-8"),
                          flags=re.S)
        if convert(refilled) != markdown:
            sys.exit("nuget_readme.py: src/nuget-readme.md depends on a benchmark marker's contents")
        if not TARGET.exists() or TARGET.read_text(encoding="utf-8") != markdown:
            sys.exit("src/nuget-readme.md is out of date with README.md: run python3 .github/scripts/nuget_readme.py")
        return
    TARGET.write_text(markdown, encoding="utf-8")


if __name__ == "__main__":
    main()
