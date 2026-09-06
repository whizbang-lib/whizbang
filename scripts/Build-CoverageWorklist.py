#!/usr/bin/env python3
"""Build a deduped coverage worklist from the per-class HTML report.

Why not Summary.txt: its rows are per (assembly, class), and ILRepack merges the
shared generator sources into several assemblies, so the SAME source file appears
in several rows. A 0% row and a 96% row for one file coexist there routinely, and
ranking by those rows sends you to write tests for lines that are already covered
by another assembly's copy.

This keys on the source path from each page's <h2> and treats a line as uncovered
only if every page that reports it says red -- one green anywhere means covered.

Usage:  python3 scripts/Build-CoverageWorklist.py        (run from the repo root)
"""
import re,os,collections
D="coverage-report"
h2=re.compile(r'<h2[^>]*>\s*(/[^<\n]*?\.cs)\s*</h2>')
row=re.compile(r"data-coverage=\"\{[^\"]*?'LVS':\s*'(\w+)'[^\"]*?\}\}\"[\s\S]{0,700}?id=\"file(\d+)_line(\d+)\"")
state={}   # (src,line) -> True if red everywhere seen, False once green anywhere
for fn in sorted(os.listdir(D)):
    if not fn.endswith('.html') or fn=='index.html': continue
    t=open(os.path.join(D,fn),encoding='utf-8',errors='ignore').read()
    files=h2.findall(t)
    if not files: continue
    for m in row.finditer(t):
        lvs,idx,ln = m.group(1),m.group(2),m.group(3)
        i=int(idx)
        if i>=len(files): continue
        k=(files[i],int(ln))
        if lvs=='red': state.setdefault(k,True)
        elif lvs in ('green','orange'): state[k]=False
out=collections.defaultdict(list)
for (src,ln),red in state.items():
    if red: out[src].append(ln)
rows=sorted(((len(v),s,sorted(v)) for s,v in out.items()),reverse=True)
for n,s,l in rows[:45]:
    print(f"{n:4d}  {s.split('/whizbang/')[-1]}")
print("classes with >=8:",sum(1 for n,_,_ in rows if n>=8))
print("TOTAL deduped uncovered:",sum(n for n,_,_ in rows))
