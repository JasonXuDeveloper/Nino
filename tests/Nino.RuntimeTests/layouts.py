#!/usr/bin/env python3
"""The tables of docs/layouts.md, from the `# layout` lines of Nino.RuntimeTests' output on each runtime (CI's "Runtime
internals" logs, one file per runtime):

    python3 tests/Nino.RuntimeTests/layouts.py "CoreCLR x64=dotnet.txt" "Mono x64=mono.txt" ...

The first runtime is the reference; the others show `=` where a type has the same size and field offsets. Prints the
layout of each type, then the size of Around<T> { byte A; T B; int C; } and the offsets of A, B and C."""
import re, sys
runs = [a.split('=', 1) for a in sys.argv[1:]]
data, order = {}, []
for name, path in runs:
    d = {}
    for line in open(path, encoding='utf-8', errors='replace'):
        m = re.search(r'# layout (.+?): (size .*?); in Around: (size .*)$', line.rstrip('\r\n'))
        if m:
            d[m.group(1)] = (m.group(2), m.group(3))
            if m.group(1) not in order:
                order.append(m.group(1))
    data[name] = d
ref = runs[0][0]

def parse(v):
    parts = v.split(', ')
    size = parts[0].replace('size ', '')
    fields = [p.rsplit('@', 1) for p in parts[1:] if '@' in p]
    return size, [(n.strip(), int(o)) for n, o in fields]

def same(a, b):
    sa, fa = parse(a); sb, fb = parse(b)
    return sa == sb and (not fa or not fb or [o for _, o in fa] == [o for _, o in fb])

def show(v, names):
    size, fields = parse(v)
    if not fields:
        return size
    return size + ': ' + ', '.join((f'{n} {o}' if names else str(o)) for n, o in fields)

def table(index, names, title):
    print(title)
    print()
    print('| Type | ' + ' | '.join(n for n, _ in runs) + ' |')
    print('| --- |' + ' --- |' * len(runs))
    for t in order:
        base = data[ref][t][index]
        cells = []
        for n, _ in runs:
            v = data[n].get(t)
            if v is None:
                cells.append('n/a')
            elif n == ref:
                cells.append(show(base, names))
            elif same(v[index], base):
                cells.append('=')
            else:
                cells.append('**' + show(v[index], False) + '**')
        print(f'| `{t}` | ' + ' | '.join(cells) + ' |')
    print()

table(0, True, '### Each type: size, then the offset of each of its fields')
table(1, False, '### In `Around<T> { byte A; T B; int C; }`: its size, then the offsets of A, B and C')
