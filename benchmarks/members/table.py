"""Prints v3 (two runs), v4 and v4/v3 from the three BenchmarkDotNet reports under the given directory."""
import sys,glob,re
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from bdn import mean_ns, rows  # noqa: E402 (benchmarks/bdn.py)
S=sys.argv[1]
def parse(d):
    f=glob.glob(d+"/results/*-report-github.md")[0]
    return {row["Method"]: (mean_ns(row["Mean"]), row.get("Allocated", ""))
            for row in rows(f)[1] if re.match(r"^[DS][A-Z]",row["Method"]) and mean_ns(row["Mean"]) is not None}
a,b,c=[parse(d) for d in sorted(glob.glob(S+"/out-*"))]
print(f"{'case':10}{'v3a':>10}{'v4':>10}{'v3b':>10}{'v4/v3':>8}   alloc v3 / v4")
for k in b:
    if k not in a or k not in c:
        print(f"{k:10}{'-':>10}{b[k][0]:10.1f}{'-':>10}{'-':>8}   - / {b[k][1]}"); continue
    v3=(a[k][0]+c[k][0])/2
    print(f"{k:10}{a[k][0]:10.1f}{b[k][0]:10.1f}{c[k][0]:10.1f}{b[k][0]/v3:8.2f}   {a[k][1]} / {b[k][1]}")
