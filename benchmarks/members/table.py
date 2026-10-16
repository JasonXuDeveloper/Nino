"""Prints v3 (two runs), v4 and v4/v3 from the three BenchmarkDotNet reports under the given directory."""
import sys,glob,re
S=sys.argv[1]
def parse(d):
    f=glob.glob(d+"/results/*-report-github.md")[0]; r={}; hdr=None
    for line in open(f):
        c=[x.strip() for x in line.strip().strip("|").split("|")]
        if c and c[0]=="Method": hdr=c; continue
        if hdr and len(c)==len(hdr) and re.match(r"^[DS][A-Z]",c[0]):
            v=c[hdr.index("Mean")].replace(",","")
            m=re.match(r"([\d.]+) (ns|μs|us|ms)",v)
            r[c[0]]=(float(m.group(1))*{"ns":1,"μs":1e3,"us":1e3,"ms":1e6}[m.group(2)], c[hdr.index("Allocated")] if "Allocated" in hdr else "")
    return r
a,b,c=[parse(d) for d in sorted(glob.glob(S+"/out-*"))]
print(f"{'case':10}{'v3a':>10}{'v4':>10}{'v3b':>10}{'v4/v3':>8}   alloc v3 / v4")
for k in b:
    if k not in a or k not in c:
        print(f"{k:10}{'-':>10}{b[k][0]:10.1f}{'-':>10}{'-':>8}   - / {b[k][1]}"); continue
    v3=(a[k][0]+c[k][0])/2
    print(f"{k:10}{a[k][0]:10.1f}{b[k][0]:10.1f}{c[k][0]:10.1f}{b[k][0]/v3:8.2f}   {a[k][1]} / {b[k][1]}")
