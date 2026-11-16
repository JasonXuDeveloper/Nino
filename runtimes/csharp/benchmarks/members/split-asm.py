"""Splits the JIT listings in a log into <prefix>-x64.txt and <prefix>-arm64.txt by the architecture each names."""
import sys
out = {"x64": [], "arm64": []}
listing = None
for line in open(sys.argv[1], encoding="utf-8", errors="replace"):
    if line.startswith("; Assembly listing for method"):
        listing = [line]
        continue
    if listing is None:
        continue
    listing.append(line)
    if line.startswith("; Total bytes of code"):
        head = "".join(listing[:3])
        out["arm64" if "ARM64" in head else "x64"].extend(listing + ["\n"])
        listing = None
for arch, lines in out.items():
    with open(f"{sys.argv[2]}-{arch}.txt", "w") as f:
        f.writelines(lines)
