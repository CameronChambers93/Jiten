import { ankiConnect } from '~/composables/useAnkiConnect';
import {
  ankiCardPayload,
  cardsToMatch,
  keptReviews,
  knownCandidates,
  pickKnownCards,
  type AnkiCardInfo,
  type AnkiCardPayload,
  type CardReviews,
  type KnownCandidate,
  type WordEntry,
} from '~/utils/ankiCardImport';
import { noteText } from '~/utils/minedImport';
import { setupQuery, type AnkiNoteTypeSetup } from '~/utils/ankiSetups';
import { gzipJsonBody } from '~/utils/gzipJsonBody';

const CARD_FIELDS = ['cardId', 'note', 'due', 'queue', 'type', 'interval', 'factor', 'reps', 'lapses', 'mod', 'fields', 'modelName', 'deckName'];
const CARDS_PER_READ = 500;
const CARDS_PER_UPLOAD = 2000;
/** Words per `api/mined/match` call, its limit. */
const WORDS_PER_MATCH = 500;

export interface KnownWordsResult {
  /** Words sent per note type, by the note type whose card was strongest. */
  byModel: Record<string, number>;
  imported: number;
  updated: number;
  unmatched: string[];
  /** False when this AnkiConnect can't give review history; states were still sent. */
  withHistory: boolean;
}

/**
 * Known words from Anki: what the stock AnkiConnect import does with "Update words you already track", for every note
 * of every setup with known words on. Nothing is forgotten: words whose notes stop counting keep their state.
 */
export function useAnkiKnownWords() {
  const { $api } = useNuxtApp();
  const { setups, knownWordsSyncedAt, setKnownWordsSynced } = useAnkiSetups();

  async function cardsInfo(ids: number[], withFilter: boolean): Promise<AnkiCardInfo[]> {
    return ankiConnect<AnkiCardInfo[]>('cardsInfo', withFilter ? { cards: ids, fields: CARD_FIELDS } : { cards: ids });
  }

  /**
   * The dictionary entry of each card's word, from its note's text, so cards of two entries spelled alike (コート coat
   * and court) count as two words. By card id; null where the word has no entry.
   */
  async function entriesOf(cards: KnownCandidate[]): Promise<Map<number, WordEntry | null>> {
    const map = new Map<number, WordEntry | null>();
    for (let i = 0; i < cards.length; i += WORDS_PER_MATCH) {
      const chunk = cards.slice(i, i + WORDS_PER_MATCH);
      const { matches } = await $api<{ matches: (WordEntry | null)[] }>('mined/match', {
        method: 'POST',
        body: { words: chunk.map((c) => ({ word: c.word, reading: c.reading, text: noteText(c.card.fields) })) },
      });
      chunk.forEach((c, j) => map.set(c.card.cardId, matches[j] ?? null));
    }
    return map;
  }

  async function reviewsOf(ids: number[]): Promise<Map<number, CardReviews> | null> {
    const map = new Map<number, CardReviews>();
    for (let i = 0; i < ids.length; i += CARDS_PER_READ) {
      // Card ids must go as numbers: AnkiConnect looks its results up by the values passed.
      let chunk: Record<string, { ease: number; id: number; time: number; type: number }[]>;
      try {
        chunk = await ankiConnect('getReviewsOfCards', { cards: ids.slice(i, i + CARDS_PER_READ) });
      } catch {
        return null;
      }
      for (const [id, reviews] of Object.entries(chunk)) {
        const kept = keptReviews(reviews);
        if (kept) map.set(Number(id), kept);
      }
    }
    return map;
  }

  async function sync(onStep?: (text: string) => void): Promise<KnownWordsResult | null> {
    const known = setups.value.filter((s) => s.knownWords);
    if (!known.length) return null;
    const startedAt = new Date();
    const previous = knownWordsSyncedAt.value;
    const since = previous ? new Date(previous) : null;

    let withFilter = true;
    try {
      await ankiConnect('cardsInfo', { cards: [], fields: CARD_FIELDS });
    } catch {
      withFilter = false;
    }

    const all: { card: AnkiCardInfo; setup: AnkiNoteTypeSetup }[] = [];
    for (const setup of known) {
      onStep?.(`Reading ${setup.modelName} cards…`);
      const ids = await ankiConnect<number[]>('findCards', { query: setupQuery(setup) });
      for (let i = 0; i < ids.length; i += CARDS_PER_READ) {
        for (const card of await cardsInfo(ids.slice(i, i + CARDS_PER_READ), withFilter)) all.push({ card, setup: setup as AnkiNoteTypeSetup });
      }
    }

    const candidates = knownCandidates(all);
    const toMatch = cardsToMatch(candidates, since);
    if (toMatch.length) onStep?.(`Matching ${toMatch.length} words to the dictionary…`);
    const entries = await entriesOf(toMatch);
    const picked = pickKnownCards(candidates, since, (card) => entries.get(card.cardId));
    const result: KnownWordsResult = { byModel: {}, imported: 0, updated: 0, unmatched: [], withHistory: true };
    for (const c of picked) result.byModel[c.modelName] = (result.byModel[c.modelName] ?? 0) + 1;

    if (picked.length) {
      onStep?.(`Reading the review history of ${picked.length} cards…`);
      const reviews = await reviewsOf(picked.map((c) => c.card.cardId));
      result.withHistory = reviews != null;
      const payloads: AnkiCardPayload[] = picked.map((c) =>
        ankiCardPayload(c.card, c.word, c.reading, reviews?.get(c.card.cardId), entries.get(c.card.cardId))
      );

      for (let i = 0; i < payloads.length; i += CARDS_PER_UPLOAD) {
        onStep?.(`Sending known words ${i + 1}–${Math.min(i + CARDS_PER_UPLOAD, payloads.length)} of ${payloads.length}…`);
        const { body, headers } = await gzipJsonBody({ cards: payloads.slice(i, i + CARDS_PER_UPLOAD), overwrite: true, parseWords: false });
        const r = await $api<{ imported?: number; updated?: number; skippedWords?: string[]; skippedWordsReadingMismatch?: string[] }>(
          'user/vocabulary/import-from-anki',
          { method: 'POST', body, headers }
        );
        result.imported += r.imported ?? 0;
        result.updated += r.updated ?? 0;
        result.unmatched.push(...(r.skippedWords ?? []), ...(r.skippedWordsReadingMismatch ?? []));
      }
    }

    await setKnownWordsSynced(startedAt, previous);
    return result;
  }

  return { sync };
}
