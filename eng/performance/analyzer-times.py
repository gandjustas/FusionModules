"""Analyzer and generator times per compilation, from a build log.

The log of `dotnet build --no-incremental -m:1 -v:d -p:ReportAnalyzer=true -clp:PerformanceSummary`
(-m:1 so that the compilations' reports do not interleave); the compiler's output may be English or
Russian. Prints the time of every analyzer and generator, FusionModules' share, its types, and the
compilations it costs most in. eng/performance/measure.sh runs the build and this.

Usage: python eng/performance/analyzer-times.py <build.log> [--detail]
"""
import re
import sys

NUM = r'(<?\d+[.,]\d+)'
TOTAL_ANALYZERS = re.compile(r'(Общее время выполнения анализатора|Total analyzer execution time):\s*' + NUM)
TOTAL_GENERATORS = re.compile(r'(Общее время выполнения генератора|Total generator execution time):\s*' + NUM)
HEADER_ANALYZERS = re.compile(r'(Время \(с\)|Time \(s\))\s+%\s+(Анализатор|Analyzer)\s*$')
HEADER_GENERATORS = re.compile(r'(Время \(с\)|Time \(s\))\s+%\s+(Генератор|Generator)\s*$')
ROW = re.compile(r'^\s+' + NUM + r'\s+<?\d+(\s+)(\S.*)$')
OUT = re.compile(r'/out:\S*?([^\\/]+)\.dll')
CSC_TASK = re.compile(r'^\s*(\d+)\s*(?:мс|ms)\s+(?:вызовов|calls)?\s*(\d+)\s+Csc\s*$')


def number(text):
    return 0.0005 if text.startswith('<') else float(text.replace(',', '.'))


def parse(path):
    blocks = []
    current = None
    table = None
    csc = None
    for line in open(path, encoding='utf-8', errors='replace'):
        line = line.rstrip('\n')
        m = OUT.search(line)
        if m and ('csc.dll' in line or 'csc.exe' in line or '/noconfig' in line):
            current = {'project': m.group(1), 'analyzers': 0.0, 'generators': 0.0,
                       'fm_analyzers': 0.0, 'fm_generators': 0.0, 'fm_detail': [], 'top': []}
            blocks.append(current)
            table = None
            continue
        m = CSC_TASK.match(line)
        if m:
            csc = (int(m.group(1)), int(m.group(2)))
        if current is None:
            continue
        m = TOTAL_ANALYZERS.search(line)
        if m:
            current['analyzers'] = number(m.group(2))
            continue
        m = TOTAL_GENERATORS.search(line)
        if m:
            current['generators'] = number(m.group(2))
            continue
        if HEADER_ANALYZERS.search(line):
            table = 'analyzers'
            continue
        if HEADER_GENERATORS.search(line):
            table = 'generators'
            continue
        m = ROW.match(line)
        if m and table:
            seconds, indent, name = number(m.group(1)), len(m.group(2)), m.group(3)
            top_level = indent <= 3
            if top_level:
                current['top'].append((table, seconds, name.split(',')[0]))
            if name.startswith('FusionModules'):
                if top_level:
                    current['fm_' + table] += seconds
                else:
                    current['fm_detail'].append((table, seconds, name))
    return blocks, csc


def main():
    blocks, csc = parse(sys.argv[1])
    detail = '--detail' in sys.argv
    with_fm = [b for b in blocks if b['fm_analyzers'] or b['fm_generators']]
    print(f'compilations: {len(blocks)}, with FusionModules: {len(with_fm)}')
    if csc:
        print(f'Csc task: {csc[0] / 1000:.1f} s over {csc[1]} calls')
    ta = sum(b['analyzers'] for b in blocks)
    tg = sum(b['generators'] for b in blocks)
    fa = sum(b['fm_analyzers'] for b in blocks)
    fg = sum(b['fm_generators'] for b in blocks)
    print(f'all analyzers: {ta:.3f} s   FusionModules analyzers: {fa:.3f} s ({100 * fa / ta if ta else 0:.1f}%)')
    print(f'all generators: {tg:.3f} s   FusionModules generators: {fg:.3f} s ({100 * fg / tg if tg else 0:.1f}%)')

    # Per analyzer / generator type, summed over all compilations.
    per_type = {}
    for b in blocks:
        for table, seconds, name in b['fm_detail']:
            key = (table, re.sub(r'\s*\(.*$', '', name))
            per_type[key] = per_type.get(key, 0.0) + seconds
    print('FusionModules by type:')
    for (table, name), seconds in sorted(per_type.items(), key=lambda item: -item[1]):
        print(f'  {seconds:7.3f} s  {table[:-1]:9} {name}')

    # The biggest assemblies of analyzers and generators, for scale.
    per_assembly = {}
    for b in blocks:
        for table, seconds, name in b['top']:
            per_assembly[(table, name)] = per_assembly.get((table, name), 0.0) + seconds
    print('largest analyzer/generator assemblies:')
    for (table, name), seconds in sorted(per_assembly.items(), key=lambda item: -item[1])[:8]:
        print(f'  {seconds:7.3f} s  {table[:-1]:9} {name}')

    print('slowest compilations for FusionModules:')
    for b in sorted(with_fm, key=lambda b: -(b['fm_analyzers'] + b['fm_generators']))[:5]:
        print(f"  {b['project']}: analyzers {b['fm_analyzers']:.3f} s of {b['analyzers']:.3f}, generators {b['fm_generators']:.3f} s of {b['generators']:.3f}")
    if detail:
        for b in blocks:
            print(b['project'], b['analyzers'], b['generators'], b['fm_analyzers'], b['fm_generators'])


main()
