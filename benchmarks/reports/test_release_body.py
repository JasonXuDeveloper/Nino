#!/usr/bin/env python3
"""Tests of release_body.py, release_assets.py and charts.py: a dry run of report.yml's publish jobs against a
directory standing in for a release (no network, no Chrome: --no-png), the body's benchmark part, the old sections of
a release from before the manifest, readme.py --assets, and the PNG crop (run: python3
benchmarks/reports/test_release_body.py)."""
import contextlib
import io
import json
import os
import struct
import sys
import tempfile
import unittest
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import charts  # noqa: E402
import generator_report  # noqa: E402
import readme  # noqa: E402
import release_assets  # noqa: E402
import release_body  # noqa: E402

TAG = "v9.9.9-test"
ENV = """```

BenchmarkDotNet v0.15.8, Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
AMD EPYC 7763 3.10GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.403
  [Host]             : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4

```
"""


def report(rows, types=True):
    """A BenchmarkDotNet github report: rows of (class, method, mean ns)."""
    head = ("| Type | " if types else "| ") + "Method | Mean | Payload | Allocated |"
    lines = [ENV, head, "|---" * (head.count("|") - 1) + "|"]
    for cls, method, ns in rows:
        lines.append(("| " + cls + " " if types else "") + f"| {method} | {ns:,.3f} ns | 10 B | - |")
    return "\n".join(lines) + "\n"


X64 = [("SimpleTest", f"{lib}{verb}{case}", ns * k)
       for case, ns in (("SimpleClass", 50.0), ("SimpleStruct", 0.4))
       for verb in ("Serialize", "Deserialize")
       for lib, k in (("Nino", 1), ("MemoryPack", 2), ("GoogleProtobuf", 5), ("Protobuf", 6), ("Json", 9))]
V3 = [("SimpleTest", f"Nino{verb}{case}", ns) for case, ns in (("SimpleClass", 120.0), ("SimpleStruct", 0.5))
      for verb in ("Serialize", "Deserialize")]
GO = """## 🐹 Go runtime benchmark

<details>
<summary>The Go runtime</summary>

`runtimes/go/bench` on AMD EPYC 7763 64-Core Processor , linux/amd64. Allocations are counted per operation.

| Case | Operation | Nino | Protobuf | JSON |
|---|---|--:|--:|--:|
| simple-class | serialize | 364.00 ns ±1% | 3.79 μs ±2% | 6.96 μs ±1% |
| simple-class | deserialize | 904.00 ns ±1% | 4.86 μs ±1% | 12.20 μs ±1% |
| simple-class | size | 640 B | 700 B | 1.2 KiB |

</details>
"""


class Publish(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = self.tmp.name
        self.release = os.path.join(self.dir, "release")
        os.makedirs(self.release)
        self.write("release/body.md", "## What's Changed\n\n- feat: a thing\n")
        self.write("x64.md", report(X64))
        self.write("arm64.md", report(X64))
        self.write("v3.md", report(V3, types=False))
        self.write("go.md", GO)
        fixtures = os.path.join(HERE, "fixtures", "generator")
        load = lambda side: [json.load(open(os.path.join(fixtures, f), encoding="utf-8"))
                             for f in sorted(os.listdir(fixtures)) if f.startswith(side)]
        self.write("build.md", generator_report.section(TAG, load("v3"), load("v4")))

    def tearDown(self):
        self.tmp.cleanup()

    def write(self, name, text):
        with open(os.path.join(self.dir, name), "w", encoding="utf-8") as f:
            f.write(text)

    def path(self, *parts):
        return os.path.join(self.dir, *parts)

    def publish(self, work, *files):
        args = ["publish", "--tag", TAG, "--dir", self.path(work), "--release-dir", self.release, "--no-png", *files]
        with contextlib.redirect_stderr(io.StringIO()):
            sys.argv = ["release_body.py", *args]
            release_body.main()

    def body(self):
        return open(self.path("release", "body.md"), encoding="utf-8").read()

    def manifest(self):
        return json.load(open(self.path("release", "assets", release_assets.MANIFEST), encoding="utf-8"))

    def test_every_job_adds_its_assets_and_sections(self):
        self.publish("w1", f"dotnet-x64.md={self.path('x64.md')}", f"dotnet-v3-x64.md={self.path('v3.md')}",
                     f"dotnet-arm64.md={self.path('arm64.md')}")
        self.publish("w2", f"runtime-go-x64.md={self.path('go.md')}")
        self.publish("w3", f"build-x64.md={self.path('build.md')}")
        manifest = self.manifest()
        self.assertEqual([e["name"] for e in manifest["assets"]],
                         ["dotnet-x64.md", "dotnet-arm64.md", "dotnet-v3-x64.md", "runtime-go-x64.md", "build-x64.md"])
        self.assertEqual(manifest["tag"], TAG)
        go = release_assets.entry(manifest, "runtime-go-x64.md")
        self.assertEqual((go["kind"], go["lang"], go["arch"]), ("runtime", "go", "x64"))
        self.assertEqual(go["environment"], "AMD EPYC 7763, linux/amd64")
        self.assertIn("summary", go)
        x64 = release_assets.entry(manifest, "dotnet-x64.md")
        self.assertTrue(x64["environment"].startswith("BenchmarkDotNet v0.15.8, Linux Ubuntu 26.04.1 LTS, .NET 10.0.14"))
        self.assertEqual(x64["summary"], "Nino v4 fastest or on par in 4 of 4 cases against every library measured "
                                         "(20 rows)")
        v3 = release_assets.entry(manifest, "dotnet-v3-x64.md")
        # SimpleStruct: both under the 1 ns floor, so on par, never a ratio
        self.assertEqual(v3["summary"], "v4 faster in 2 of 4 cases, on par in 2 (median 2.4× faster)")

        body = self.body()
        self.assertTrue(body.startswith("## What's Changed\n\n- feat: a thing\n\n" + release_body.START))
        self.assertEqual(body.count("<details>"), 5)
        self.assertLess(body.index(".NET x64"), body.index(".NET Arm64"))
        self.assertLess(body.index("Go runtime x64"), body.index("Build:"))
        self.assertIn(f"https://github.com/JasonXuDeveloper/Nino/releases/download/{TAG}/dotnet-x64.md", body)
        self.assertNotIn("| Method", body)
        self.assertLess(len(body), 10_000)
        self.assertEqual(sorted(os.listdir(self.path("release", "assets"))),
                         sorted([release_assets.MANIFEST] + [e["name"] for e in manifest["assets"]]))

        # A re-run replaces the assets and leaves the same body.
        before = body
        self.publish("w4", f"runtime-go-x64.md={self.path('go.md')}")
        self.assertEqual(self.body(), before)

    def test_old_sections_stay_until_the_manifest_has_their_asset(self):
        unity = "## 🎮 Unity benchmark (Burst, IL2CPP)\n\n| Case | Operation |\n|---|---|\n| a | b |"
        self.write("release/body.md", "notes\n\n## 📊 v3 3.3.13 vs v4 (same machine)\n\nv3 table\n\n"
                   "## 📊 Performance Report\n\n| Method | Mean |\n\n---\n*Benchmark generated*\n\n"
                   "## 🐹 Go runtime benchmark\n\nold go\n\n" + unity + "\n")
        self.publish("w1", f"dotnet-x64.md={self.path('x64.md')}", f"dotnet-v3-x64.md={self.path('v3.md')}")
        body = self.body()
        self.assertTrue(body.startswith("notes\n\n" + release_body.START))
        self.assertNotIn("v3 table", body)
        self.assertNotIn("Performance Report", body)
        self.assertIn("old go", body)
        self.assertIn(unity, body)
        self.assertTrue(body.rstrip().endswith(release_body.END))
        self.assertEqual(release_body.legacy_sections(body).keys(), {"runtime-go-x64.md", "unity-x64.md"})
        self.publish("w2", f"runtime-go-x64.md={self.path('go.md')}")
        self.assertNotIn("old go", self.body())
        self.assertIn(unity, self.body())

        rel = release_assets.release(TAG, self.release)
        self.assertTrue(release_assets.has(rel, ["dotnet-x64.md", "unity-x64.md"]))
        self.assertFalse(release_assets.has(rel, ["dotnet-arm64.md"]))

    def test_readme_reads_the_assets(self):
        self.publish("w1", f"build-x64.md={self.path('build.md')}")
        work = self.path("fetched")
        manifest = release_assets.fetch(release_assets.release(TAG, self.release), work)
        self.assertEqual([e["name"] for e in manifest["assets"]], ["build-x64.md"])
        v4, v3, build, tag = readme.from_assets(work)
        self.assertEqual((v4, v3, build, tag), (None, [], os.path.join(work, "build-x64.md"), TAG))
        self.assertIn("doc.build.30", readme.build(build, tag))

    def test_unknown_names_are_refused(self):
        with self.assertRaises(ValueError):
            release_assets.describe("benchmark-x64-full.md")
        self.assertEqual(release_assets.describe("build-v3-100.json")["of"], "build-x64.md")
        self.assertEqual(release_assets.describe("runtime-typescript-arm64.json")["of"], "runtime-typescript-arm64.md")


class Charts(unittest.TestCase):
    def test_the_floor_and_escaping(self):
        image, _ = charts.svg([(("Serialize · List<int>", [("Nino", 0.4), ("A&B", 0.5), ("C", 3.0)]), None)])
        self.assertIn("List&lt;int&gt;", image)
        self.assertIn("A&amp;B", image)
        self.assertIn("&lt; 1 ns · on par", image)  # both under the floor
        self.assertIn(">3.00 ns</text>", image)     # Nino under it against a measurable time: no ratio

    def test_crop_keeps_the_first_rows(self):
        width, height = 3, 10
        raw = b"".join(b"\x02" + bytes([y] * width * 4) for y in range(height))  # filter Up on every row

        def chunk(kind, body):
            return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))

        png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)) + \
            chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b"")
        cut = charts.crop_png(png, 4)
        self.assertEqual(struct.unpack(">II", cut[16:24]), (width, 4))
        at = cut.index(b"IDAT")
        size = struct.unpack(">I", cut[at - 4:at])[0]
        self.assertEqual(zlib.decompress(cut[at + 4:at + 4 + size]), raw[:4 * (1 + width * 4)])


if __name__ == "__main__":
    unittest.main()
