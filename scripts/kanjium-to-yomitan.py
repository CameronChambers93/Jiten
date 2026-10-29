# Usage: python3 scripts/kanjium-to-yomitan.py accents.txt /data/pitch/kanjium, then Jiten.Cli --import-pitch-accents /data/pitch/kanjium
"""Kanjium accents.txt (term \t reading \t accents, e.g. "(副)0,(名)3") -> Yomitan term_meta_bank_1.json + index.json."""
import json, re, sys, os
src, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
rows = []
for line in open(src, encoding='utf-8'):
    parts = line.rstrip('\n').split('\t')
    if len(parts) < 3: continue
    term, reading, accents = parts[0], parts[1] or parts[0], parts[2]
    positions = []
    for a in accents.split(','):
        m = re.search(r'\d+', re.sub(r'\([^)]*\)', '', a))
        if m and int(m.group()) not in positions: positions.append(int(m.group()))
    if positions:
        rows.append([term, 'pitch', {'reading': reading, 'pitches': [{'position': p} for p in positions]}])
json.dump(rows, open(f'{out}/term_meta_bank_1.json', 'w', encoding='utf-8'), ensure_ascii=False)
json.dump({'title': 'Kanjium pitch accents', 'format': 3, 'revision': 'kanjium-accents-txt',
           'attribution': 'Kanjium (https://github.com/mifunetoshiro/kanjium), CC BY-SA 4.0'},
          open(f'{out}/index.json', 'w', encoding='utf-8'), ensure_ascii=False)
print(len(rows), 'entries')
