import { FsrsState } from '~/types/enums';
import { fieldFor, type AnkiNoteTypeSetup, type KnownWordsState } from '~/utils/ankiSetups';

/** An Anki card as AnkiConnect's cardsInfo returns it (the fields the vocabulary import reads). */
export interface AnkiCardInfo {
  cardId: number;
  note?: number;
  due: number;
  /** -1 suspended, 0 new, 1 learning, 2 review, 3 day learning; -2/-3 buried. */
  queue: number;
  /** 0 new, 1 learning, 2 review, 3 relearning. */
  type: number;
  interval: number;
  factor: number;
  reps: number;
  lapses: number;
  /** Last modified, in seconds. */
  mod: number;
  fields: Record<string, { value: string; order?: number }>;
  modelName: string;
  deckName: string;
}

export type KeptReview = { Rating: number; ReviewDateTime: Date; ReviewDuration: number; State?: FsrsState };
export type CardReviews = { kept: KeptReview[]; lastReview: Date | null };

/** One card for `POST api/user/vocabulary/import-from-anki`. */
export interface AnkiCardPayload {
  Card: {
    Word: string;
    Reading?: string;
    /** The entry, when matched from the note's text; the API then uses it instead of looking the word up. */
    WordId?: number;
    ReadingIndex?: number;
    Stability: number;
    Difficulty: number;
    Reps: number;
    Lapses: number;
    Due: string;
    State: number;
    LastReview?: string;
  };
  ReviewLogs: { Rating: number; ReviewDateTime: string; ReviewDuration: number; State?: FsrsState }[];
}

// Anki revlog types 0-2; filtered-deck, manual and rescheduled entries stay unknown.
export const ANKI_REVLOG_STATE: Record<number, FsrsState> = { 0: FsrsState.Learning, 1: FsrsState.Review, 2: FsrsState.Relearning };

/** Max review-log entries kept per card: the full history of nearly every card; only persistent leeches are clipped. */
export const MAX_REVIEWS_PER_CARD = 100;

/** Days of interval that make a card mature, as Anki counts it. */
export const MATURE_DAYS = 21;

// A Date built from corrupt or out-of-range Anki fields can be Invalid, and toISOString() on it throws.
export const isValidDate = (d: Date | null | undefined): d is Date => !!d && !Number.isNaN(d.getTime());

export const stripRuby = (text: string) => text.replace(/\[.*?\]/g, '');

/** An Anki furigana field as its full kana reading: `下[くだ]さる` → `くださる`; plain kana is returned as is. */
export const furiganaToReading = (text: string) =>
  text
    .replace(/&nbsp;/g, ' ')
    .replace(/([^[\]\s]+)\[([^[\]]*)\]/g, '$2')
    .replace(/\s+/g, '')
    .trim();

/** A card's reviews from getReviewsOfCards, oldest first and capped from the first review on. */
export function keptReviews(reviews: { ease: number; id: number; time: number; type: number }[] | undefined): CardReviews | null {
  if (!reviews || reviews.length === 0) return null;
  const mapped: KeptReview[] = reviews.map((r) => ({
    Rating: r.ease,
    ReviewDateTime: new Date(r.id),
    ReviewDuration: r.time,
    State: ANKI_REVLOG_STATE[r.type],
  }));
  mapped.sort((a, b) => a.ReviewDateTime.getTime() - b.ReviewDateTime.getTime());
  // The window must start at the first review: the optimiser and the replay both start from a new card.
  return { kept: mapped.length > MAX_REVIEWS_PER_CARD ? mapped.slice(0, MAX_REVIEWS_PER_CARD) : mapped, lastReview: mapped[mapped.length - 1]!.ReviewDateTime };
}

/** The import payload for a studied card (not new or suspended; the caller checks), with its reviews when fetched. */
export function ankiCardPayload(
  card: AnkiCardInfo,
  word: string,
  reading: string,
  cardReviews: CardReviews | undefined,
  entry?: WordEntry | null
): AnkiCardPayload {
  const reviews = cardReviews?.kept ?? [];
  const state = card.queue === 1 || card.queue === 3 ? 1 : 2; // Learning : Review
  const stability = card.interval > 0 ? card.interval : 0;
  const difficulty = Math.max(1, Math.min(10, 10 - (card.factor - 1300) / 170.0));

  // From the untruncated history, so a clipped leech isn't scheduled from a stale review. Without history, the card's
  // modification time stands in, so the card still has a last review and stays schedulable.
  const mostRecentReview = cardReviews?.lastReview ?? null;
  const modReview = card.mod ? new Date(card.mod * 1000) : null;
  const lastReview = isValidDate(mostRecentReview) ? mostRecentReview : isValidDate(modReview) ? modReview : null;

  let due: Date | null;
  // Only intraday learning (queue 1) stores `due` as a timestamp; review and day learning store a day number relative to
  // collection creation, so their due is rebuilt from the last review (or mod) and the interval.
  if (card.queue === 1) due = new Date(card.due * 1000);
  else if (lastReview) due = new Date(lastReview.getTime() + card.interval * 86400000);
  else due = new Date(card.mod * 1000 + card.interval * 86400000);
  if (!isValidDate(due)) due = lastReview ?? new Date();

  return {
    Card: {
      Word: word,
      Reading: reading || undefined,
      WordId: entry?.wordId,
      ReadingIndex: entry?.readingIndex,
      Stability: stability,
      Difficulty: difficulty,
      Reps: card.reps,
      Lapses: card.lapses,
      Due: due.toISOString(),
      State: state,
      LastReview: lastReview?.toISOString(),
    },
    ReviewLogs: reviews
      .filter((r) => isValidDate(r.ReviewDateTime))
      .map((r) => ({ Rating: r.Rating, ReviewDateTime: r.ReviewDateTime.toISOString(), ReviewDuration: r.ReviewDuration, State: r.State })),
  };
}

/** Whether a card is studied far enough to make its word known. Suspended and new cards never are. */
export function cardMeetsState(card: Pick<AnkiCardInfo, 'queue' | 'type' | 'interval' | 'reps'>, state: KnownWordsState): boolean {
  if (card.queue === -1 || card.queue === 0 || card.type === 0) return false;
  if (state === 'seen') return card.reps > 0;
  if (state === 'review') return card.type === 2;
  return card.type === 2 && card.interval >= MATURE_DAYS;
}

/** How far along a card is, to keep the strongest card of a word: review beats relearning beats learning, then interval. */
export function cardStrength(card: Pick<AnkiCardInfo, 'type' | 'interval'>): number {
  const rank = card.type === 2 ? 3 : card.type === 3 ? 2 : card.type === 1 ? 1 : 0;
  return rank * 1_000_000 + Math.max(0, card.interval);
}

/** Slack on "changed since the last Sync", for Anki's clock and this browser's disagreeing. */
const CLOCK_SLACK_MS = 10 * 60 * 1000;

export interface KnownCandidate {
  card: AnkiCardInfo;
  word: string;
  reading: string;
  modelName: string;
}

/** A dictionary entry, as `api/mined/match` decides it. */
export interface WordEntry {
  wordId: number;
  readingIndex: number;
}

/** The word and reading a card holds, through its setup's roles. */
export function cardWord(card: AnkiCardInfo, setup: AnkiNoteTypeSetup): { word: string; reading: string } | null {
  const order = Object.entries(card.fields)
    .sort(([, a], [, b]) => (a.order ?? 0) - (b.order ?? 0))
    .map(([name]) => name);
  const wordField = fieldFor(setup.fieldRoles, 'word', order);
  const word = stripRuby((card.fields[wordField ?? '']?.value ?? '').replace(/<[^>]*>/g, '').trim());
  if (!word) return null;
  const readingField = fieldFor(setup.fieldRoles, 'wordReading', order) ?? fieldFor(setup.fieldRoles, 'wordFurigana', order);
  const reading = furiganaToReading(
    (card.fields[readingField ?? '']?.value ?? '')
      .replace(/<rt>(.*?)<\/rt>/g, '[$1]')
      .replace(/<[^>]*>/g, '')
      .trim()
  );
  return { word, reading };
}

/** The cards studied far enough to count, with their words, across every setup with known words on. */
export function knownCandidates(cards: { card: AnkiCardInfo; setup: AnkiNoteTypeSetup }[]): KnownCandidate[] {
  const out: KnownCandidate[] = [];
  for (const { card, setup } of cards) {
    if (!setup.knownWords || !cardMeetsState(card, setup.knownWords)) continue;
    const w = cardWord(card, setup);
    if (w) out.push({ card, ...w, modelName: setup.modelName });
  }
  return out;
}

function changedSince(card: AnkiCardInfo, since: Date | null): boolean {
  return since == null || card.mod * 1000 >= since.getTime() - CLOCK_SLACK_MS;
}

/**
 * The cards whose entry must be matched before picking: those sharing their word and reading with another card (which
 * may be another entry with the same spelling), and those that will be sent.
 */
export function cardsToMatch(candidates: KnownCandidate[], since: Date | null): KnownCandidate[] {
  const count = new Map<string, number>();
  for (const c of candidates) count.set(`${c.word} ${c.reading}`, (count.get(`${c.word} ${c.reading}`) ?? 0) + 1);
  return candidates.filter((c) => count.get(`${c.word} ${c.reading}`)! > 1 || changedSince(c.card, since));
}

/**
 * The strongest card per word across every setup with known words on, among cards studied far enough. A word is its
 * matched entry when `entryOf` knows it, else its word and reading. A word's card is only sent when it changed since
 * `since`, so an unchanged collection sends nothing.
 */
export function pickKnownCards(
  candidates: KnownCandidate[],
  since: Date | null,
  entryOf: (card: AnkiCardInfo) => WordEntry | null | undefined = () => undefined
): KnownCandidate[] {
  const best = new Map<string, KnownCandidate>();
  for (const c of candidates) {
    const entry = entryOf(c.card);
    const key = entry ? `#${entry.wordId}:${entry.readingIndex}` : `${c.word} ${c.reading}`;
    const current = best.get(key);
    if (!current || cardStrength(c.card) > cardStrength(current.card)) best.set(key, c);
  }
  return [...best.values()].filter((c) => changedSince(c.card, since));
}
