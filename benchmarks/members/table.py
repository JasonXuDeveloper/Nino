"""Prints v3 (two runs), v4 and v4/v3 from the three BenchmarkDotNet reports under the given directory.

usage: table.py <results directory>   (members/run.sh: out-1-v3, out-2-v4, out-3-v3)
"""
import glob
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from bdn import mean_ns, rows  # noqa: E402 (benchmarks/bdn.py)


def parse(directory):
    """-> {case: (mean in ns, allocated)} for the member cases (D... and S..., deserialize and serialize)."""
    report = glob.glob(directory + "/results/*-report-github.md")[0]
    return {row["Method"]: (mean_ns(row["Mean"]), row.get("Allocated", ""))
            for row in rows(report)[1]
            if re.match(r"^[DS][A-Z]", row["Method"]) and mean_ns(row["Mean"]) is not None}


def main():
    v3a, v4, v3b = [parse(d) for d in sorted(glob.glob(sys.argv[1] + "/out-*"))]
    print(f"{'case':10}{'v3a':>10}{'v4':>10}{'v3b':>10}{'v4/v3':>8}   alloc v3 / v4")
    for case, (now, allocated) in v4.items():
        if case not in v3a or case not in v3b:
            print(f"{case:10}{'-':>10}{now:10.1f}{'-':>10}{'-':>8}   - / {allocated}")
            continue
        v3 = (v3a[case][0] + v3b[case][0]) / 2
        print(f"{case:10}{v3a[case][0]:10.1f}{now:10.1f}{v3b[case][0]:10.1f}{now / v3:8.2f}   "
              f"{v3a[case][1]} / {allocated}")


if __name__ == "__main__":
    main()
