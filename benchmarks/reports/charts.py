#!/usr/bin/env python3
"""Draws the benchmark charts: the README's (readme.py) and the release body's (release_body.py), as SVG, and renders
them to PNG with headless Chrome.

usage: charts.py png [--chrome PATH] <file.svg>...   writes <file>.png next to each SVG, at twice its size

A chart is a list of rows; a row is a pair of groups (left, right: serialize and deserialize, or two workloads),
either of which may be None; a group is (title, [(name, value or None, label or None)]). The first entry of a group is
Nino (the accent colour): every other bar is labelled with its multiple of Nino's value. A value of None is drawn as
"n/a"; a label replaces the default text of a value (time() of nanoseconds). Shorter is better on every chart.

PNG: README.md is packed into the NuGet packages and nuget.org shows no SVG, and a release body shows images only
through GitHub's image proxy, so both get PNGs. Headless Chrome paints less than its window: it gets a taller window,
and the image is cut to the chart's height (the viewBox) afterwards, here in pure Python (a PNG's rows depend only on
the rows above them, so the first rows of an image are a valid image of their own once the header says so).
"""
import argparse
import html
import re
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import zlib

from bdn import FLOOR_NS

# Nino v4 in its accent colour, v3 in a darker grey, every other library in one lighter grey (the docs site's rule:
# Nino the accent, everything else neutral). Each image paints its own background (GitHub's page colours):
# prefers-color-scheme follows the reader's system, not the page, so a dark system shows the dark image on nuget.org's
# or GitHub's light page, and on a transparent background its light text vanished.
THEMES = {"light": {"text": "#1f2328", "muted": "#59636e", "background": "#ffffff",
                    "v4": "#3b6fd4", "v3": "#8b95a5", "other": "#cdd2da"},
          "dark": {"text": "#f0f6fc", "muted": "#9198a1", "background": "#0d1117",
                   "v4": "#5b8def", "v3": "#6e7681", "other": "#3d444d"}}
PAD = 20  # the background's margin around the chart
SCALE = 2  # PNG pixels per SVG unit

README_TITLE = "Time per operation (shorter is faster)"
README_NOTE = ("BenchmarkDotNet, .NET 10, server GC, one run on one machine (v3 3.3.13 directly before v4); "
               "N× = N times as long as Nino v4.")


def on_par(r):
    """Within 5%: the docs site's rule (nino.docs src/lib/benchmarks.ts)."""
    return round(abs(r - 1) * 100) <= 5


def one(r):
    """One decimal below 10 (7.2), whole numbers from 10 (23)."""
    return f"{r:.1f}" if r < 10 else f"{r:.0f}"


def time(ns):
    """As the docs site writes times: 151 ns, 17.0 µs, 1.23 ms; under FLOOR_NS, "< 1 ns"."""
    if ns < FLOOR_NS:
        return f"< {FLOOR_NS} ns"
    for unit, scale in (("ns", 1), ("µs", 1e3), ("ms", 1e6), ("s", 1e9)):
        if ns < scale * 1e3 or unit == "s":
            value = ns / scale
            text = f"{value:,.0f}" if value >= 100 else f"{value:.1f}" if value >= 10 else f"{value:.2f}"
            return f"{text} {unit}"


def multiple(value, first, floor=FLOOR_NS):
    """" · 2.1×", " · on par", or nothing. Under the floor (FLOOR_NS for times; 0 for sizes and seconds) a time
    cannot be told apart from an empty method: a first entry under it against a measurable value gets no ratio, and
    both under it are on par (readme.py's rule)."""
    if first < floor:
        return " · on par" if value < floor else ""
    r = value / first
    return " · on par" if on_par(r) else f" · {one(r)}×"


def svg(chart, theme="light", title=README_TITLE, note=README_NOTE, floor=FLOOR_NS):
    """The chart as SVG -> (text, height): per row, the left group and the right group, a bar per entry, its length
    its value on a linear scale of that group, labelled with the value and its multiple of the first entry's (multiple:
    `floor` is FLOOR_NS for nanoseconds, 0 for other units). Names and titles are escaped."""
    c = THEMES[theme]
    column, x0, width = 450, 100, 170
    out, y = [], 78
    for pair in chart:
        bottom = y
        for side, grp in enumerate(pair):
            if grp is None:
                continue
            heading, rows = grp
            rows = [row + (None,) * (3 - len(row)) for row in rows]
            left = side * column
            out.append(f'<text x="{left}" y="{y}" class="g">{html.escape(heading)}</text>')
            by = y + 22
            nino = rows[0][1]
            top = max((value for _, value, _ in rows if value), default=0)
            for i, (name, value, label) in enumerate(rows):
                first = i == 0
                bold = " n" if first else ""
                out.append(f'<text x="{left + x0 - 10}" y="{by}" class="l{bold}">{html.escape(name)}</text>')
                if value is None:
                    out.append(f'<text x="{left + x0}" y="{by}" class="s">n/a</text>')
                else:
                    w = max(3.0, width * value / top) if top else 3.0
                    colour = "a" if first else "b" if name == "Nino v3" else "c"
                    out.append(f'<rect x="{left + x0}" y="{by - 12}" width="{w:.1f}" height="15" rx="3" class="{colour}"/>')
                    text = label if label is not None else time(value)
                    if not first and nino is not None:
                        text += multiple(value, nino, floor)
                    out.append(f'<text x="{left + x0 + w + 8:.1f}" y="{by}" class="v{bold}">{html.escape(text)}</text>')
                by += 20
            bottom = max(bottom, by)
        y = bottom + 18
    height = y - 8 + 2 * PAD
    width = 2 * column + 2 * PAD
    head = f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" width="{width}" height="{height}" font-family="-apple-system,Segoe UI,Helvetica,Arial,sans-serif">
<rect width="{width}" height="{height}" rx="8" fill="{c["background"]}"/>
<g transform="translate({PAD} {PAD})">
<style>.t{{font-size:18px;font-weight:700;fill:{c["text"]}}}.s{{font-size:12px;fill:{c["muted"]}}}.g{{font-size:13px;font-weight:600;fill:{c["text"]}}}.l{{font-size:12px;fill:{c["muted"]};text-anchor:end}}.v{{font-size:12px;fill:{c["text"]}}}.n{{font-weight:700}}.a{{fill:{c["v4"]}}}.b{{fill:{c["v3"]}}}.c{{fill:{c["other"]}}}</style>
<text x="0" y="22" class="t">{html.escape(title)}</text>
<text x="0" y="44" class="s">{html.escape(note)}</text>
'''
    return head + "\n".join(out) + "\n</g>\n</svg>\n", height


def picture(light, dark, alt, fallback=None):
    """A chart that follows the reader's colour scheme: the dark SVG for a dark scheme, the light one for a light
    scheme, and `fallback` (default: the light one) where <picture> is not supported."""
    return "\n".join(["<picture>", f'  <source media="(prefers-color-scheme: dark)" srcset="{dark}">',
                      f'  <source media="(prefers-color-scheme: light)" srcset="{light}">',
                      f'  <img src="{fallback or light}" alt="{html.escape(alt)}">', "</picture>"])


def details(summary, body, open_=False, bold=True):
    """A collapsible block of GitHub Markdown: blank lines around the body, so Markdown inside it renders. A block
    nested in another (the release body's chart groups) keeps its summary plain: GitHub does not indent it."""
    title = f"<b>{html.escape(summary)}</b>" if bold else html.escape(summary)
    return "\n".join([f"<details{' open' if open_ else ''}>", f"<summary>{title}</summary>", "", body, "", "</details>"])


def crop_png(data, height):
    """The first `height` rows of a non-interlaced PNG (Chrome's screenshots)."""
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a PNG")
    chunks, at = [], 8
    while at < len(data):
        size, kind = struct.unpack(">I4s", data[at:at + 8])
        chunks.append((kind, data[at + 8:at + 8 + size]))
        at += 12 + size
    ihdr = chunks[0][1]
    width, full, depth, colour, _, _, interlace = struct.unpack(">IIBBBBB", ihdr)
    if interlace:
        raise ValueError("interlaced PNG")
    if height >= full:
        return data
    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[colour]
    stride = 1 + (width * channels * depth + 7) // 8  # the filter byte, then the row
    pixels = zlib.decompress(b"".join(body for kind, body in chunks if kind == b"IDAT"))[:stride * height]

    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)

    out = [data[:8], chunk(b"IHDR", struct.pack(">II", width, height) + ihdr[8:])]
    out += [chunk(kind, body) for kind, body in chunks[1:] if kind not in (b"IDAT", b"IEND")]
    out += [chunk(b"IDAT", zlib.compress(pixels, 9)), chunk(b"IEND", b"")]
    return b"".join(out)


def chrome_path(given=None):
    """--chrome, $CHROME, or the first of google-chrome, chromium, chromium-browser on the PATH."""
    for candidate in (given, os.environ.get("CHROME"), "google-chrome", "chromium", "chromium-browser"):
        if candidate and (os.path.isfile(candidate) or shutil.which(candidate)):
            return candidate
    sys.exit("no Chrome: pass --chrome or set CHROME")


def png(svg_path, chrome=None):
    """Renders an SVG file of svg() to <name>.png next to it, at SCALE times its size; returns the PNG's path."""
    text = open(svg_path, encoding="utf-8").read()
    width, height = map(int, re.search(r'viewBox="0 0 (\d+) (\d+)"', text).groups())
    out = os.path.splitext(svg_path)[0] + ".png"
    with tempfile.TemporaryDirectory() as tmp:
        page = os.path.join(tmp, "chart.html")
        shot = os.path.join(tmp, "chart.png")
        with open(page, "w", encoding="utf-8") as f:
            f.write(f'<html><body style="margin:0;background:transparent">{text}</body></html>')
        subprocess.run([chrome_path(chrome), "--headless=new", "--no-sandbox", "--disable-gpu", "--hide-scrollbars",
                        f"--force-device-scale-factor={SCALE}", "--default-background-color=00000000",
                        f"--window-size={width},{height + 300}", f"--screenshot={shot}", f"file://{page}"],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=120)
        with open(shot, "rb") as f:
            image = crop_png(f.read(), SCALE * height)
    with open(out, "wb") as f:
        f.write(image)
    return out


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    commands = parser.add_subparsers(dest="command", required=True)
    render = commands.add_parser("png")
    render.add_argument("--chrome")
    render.add_argument("svgs", nargs="+")
    args = parser.parse_args()
    for path in args.svgs:
        print(png(path, args.chrome))


if __name__ == "__main__":
    main()
