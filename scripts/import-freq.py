#!/usr/bin/env python3
"""Loads a Jiten Yomitan frequency dictionary (e.g. jiten.moe's jiten_freq_global.zip) into the local
jmdict.WordFrequencies / WordFormFrequencies tables, replacing their contents.

The export only carries form text and ranks, so this inverts YomitanHelper.GenerateYomitanFrequencyDeck:
  - kanji entries (term, reading, rank) give the rank of the kanji form `term` of the word read `reading`;
  - the same with a ㋕ display value gives the rank of that word's kana form `reading`;
  - standalone kana entries (deduplicated by text in the export) rank a kana-only word's kana form.
When several words share a (kanji, reading) pair, ranks go to them in order of JMdict priority, then WordId.
Word ranks are ordered by each word's best form rank. Raw counts aren't in the export, so ObservedFrequency
is a Zipf estimate from the rank and UsedInMediaAmount is 1 for every observed word or form.

Usage: scripts/import-freq.py /data/freq/jiten_freq_global.zip
Needs psql and PGPASSWORD; connects to host `postgres`, db `jiten`. Afterwards flush Redis and restart the API.
"""
import csv
import io
import json
import subprocess
import sys
import zipfile
from collections import defaultdict

PSQL = ["psql", "-h", "postgres", "-U", "postgres", "-d", "jiten", "-v", "ON_ERROR_STOP=1"]
KANA, KANJI = 1, 0  # JmDictFormType: KanjiForm = 0, KanaForm = 1


def psql_copy_out(query):
    out = subprocess.run(PSQL + ["-c", f"COPY ({query}) TO STDOUT WITH (FORMAT csv)"],
                         check=True, capture_output=True, text=True).stdout
    return list(csv.reader(io.StringIO(out)))


def load_entries(path):
    entries = []
    with zipfile.ZipFile(path) as z:
        for name in sorted(n for n in z.namelist() if n.startswith("term_meta_bank_")):
            entries.extend(json.loads(z.read(name)))
    return entries


def main():
    entries = load_entries(sys.argv[1])
    print(f"{len(entries)} entries")

    rows = psql_copy_out('SELECT f."WordId", f."ReadingIndex", f."Text", f."FormType", '
                         'COALESCE(array_length(f."Priorities", 1), 0) FROM jmdict."WordForms" f')
    forms = {}  # (word, idx) -> (text, type)
    kana_by_word = defaultdict(dict)  # word -> kana text -> idx
    has_kanji = set()
    priority = defaultdict(int)
    kanji_words = defaultdict(list)  # kanji text -> [(word, idx)]
    kana_words = defaultdict(list)  # kana text -> [word]
    for word, idx, text, ftype, prio in rows:
        word, idx, ftype = int(word), int(idx), int(ftype)
        forms[(word, idx)] = (text, ftype)
        priority[word] += int(prio)
        if ftype == KANA:
            kana_by_word[word].setdefault(text, idx)
            kana_words[text].append(word)
        else:
            has_kanji.add(word)
            kanji_words[text].append((word, idx))
    print(f"{len(forms)} forms in {len(priority)} words")

    def order(words):
        return sorted(words, key=lambda w: (-priority[w], w))

    form_rank = {}

    def assign(key, rank):
        if key not in form_rank or rank < form_rank[key]:
            form_rank[key] = rank

    # Group kanji entries by (term, reading, kind) so homographs get ranks in a stable order.
    grouped = defaultdict(list)
    standalone = []
    for term, _, data in entries:
        if "reading" in data:
            freq = data["frequency"]
            kind = "kana" if freq["displayValue"].endswith("㋕") else "kanji"
            grouped[(term, data["reading"], kind)].append(freq["value"])
        else:
            standalone.append((term, data["value"]))

    unmatched = ambiguous = 0
    for (term, reading, kind), ranks in grouped.items():
        cands = order({w for w, _ in kanji_words.get(term, []) if reading in kana_by_word[w]})
        if not cands:
            unmatched += 1
            continue
        if len(cands) > 1:
            ambiguous += 1
        for word, rank in zip(cands, sorted(set(ranks))):
            if kind == "kanji":
                idx = next(i for w, i in kanji_words[term] if w == word)
            else:
                idx = kana_by_word[word][reading]
            assign((word, idx), rank)

    ranked_kana = {}  # kana text -> ranks already given through kanji entries
    for (word, idx), rank in form_rank.items():
        text, ftype = forms[(word, idx)]
        if ftype == KANA:
            ranked_kana.setdefault(text, set()).add(rank)
    for term, rank in standalone:
        if rank in ranked_kana.get(term, ()):
            continue  # the export's kana entry for a word already ranked via its kanji form
        cands = order(w for w in kana_words.get(term, []) if w not in has_kanji) or order(kana_words.get(term, []))
        if not cands:
            unmatched += 1
            continue
        assign((cands[0], kana_by_word[cands[0]][term]), rank)
    print(f"{len(form_rank)} ranked forms; {unmatched} entries unmatched, {ambiguous} ambiguous kanji pairs")

    # Zipf estimate for observed frequency: p(r) = 1 / (r * H_n)
    n = max(form_rank.values())
    harmonic = sum(1 / r for r in range(1, n + 1))

    def zipf(rank):
        return 1 / (rank * harmonic)

    best = defaultdict(lambda: None)
    for (word, _), rank in form_rank.items():
        if best[word] is None or rank < best[word]:
            best[word] = rank
    observed_words = sorted((w for w in priority if best[w] is not None), key=lambda w: best[w])

    word_rows = []
    rank = 0
    previous = None
    for i, word in enumerate(observed_words):
        if best[word] != previous:
            rank = i + 1
            previous = best[word]
        word_rows.append((word, rank, 1, zipf(rank)))
    unranked = len(observed_words) + 1
    word_rows += [(w, unranked, 0, 0.0) for w in priority if best[w] is None]

    form_obs = {key: zipf(r) for key, r in form_rank.items()}
    word_obs_total = defaultdict(float)
    for (word, _), obs in form_obs.items():
        word_obs_total[word] += obs
    zero_rank = n + 1
    form_rows = []
    for (word, idx) in forms:
        if (word, idx) in form_rank:
            obs = form_obs[(word, idx)]
            form_rows.append((word, idx, form_rank[(word, idx)], obs / word_obs_total[word] * 100, obs, 1))
        else:
            form_rows.append((word, idx, zero_rank, 0.0, 0.0, 0))

    def to_csv(rows_):
        buf = io.StringIO()
        csv.writer(buf, lineterminator="\n").writerows(rows_)
        return buf.getvalue()

    script = (
        "BEGIN;\n"
        'TRUNCATE jmdict."WordFrequencies", jmdict."WordFormFrequencies";\n'
        'COPY jmdict."WordFrequencies" ("WordId", "FrequencyRank", "UsedInMediaAmount", "ObservedFrequency") '
        "FROM STDIN WITH (FORMAT csv);\n" + to_csv(word_rows) + "\\.\n"
        'COPY jmdict."WordFormFrequencies" ("WordId", "ReadingIndex", "FrequencyRank", "FrequencyPercentage", '
        '"ObservedFrequency", "UsedInMediaAmount") FROM STDIN WITH (FORMAT csv);\n' + to_csv(form_rows) + "\\.\n"
        "COMMIT;\n"
    )
    subprocess.run(PSQL + ["-q", "-f", "-"], input=script, text=True, check=True)
    print(f"Wrote {len(word_rows)} word rows ({len(observed_words)} observed) and {len(form_rows)} form rows")


if __name__ == "__main__":
    main()
