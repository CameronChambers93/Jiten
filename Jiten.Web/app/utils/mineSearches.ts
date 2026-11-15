import type { PipelineHasCard } from '~/utils/pipelines';

/** The Mine page's URL keys a saved search keeps (`filterQuery()` in `mine.vue`); mirrors `MineSearchService.QueryKeys`. */
export const MINE_SEARCH_KEYS = [
  'titles',
  'episodes',
  'dict',
  'sort',
  'desc',
  'fmin',
  'fmax',
  'ranked',
  'lmin',
  'lmax',
  'umin',
  'umax',
  'lrmin',
  'lrmax',
  'status',
  'others',
  'types',
  'script',
  'en',
  'audio',
  'anki',
  'deck',
  'layout',
] as const;

export const MAX_MINE_SEARCHES = 50;
export const MAX_MINE_SEARCH_NAME = 40;
export const MAX_RUN_SENTENCES_PER_WORD = 5;

/** The answer a bulk run gives an Anki step that would ask (GDR-059). */
export type WhenAsked = 'existing' | 'new' | 'skip';

export const WHEN_ASKED_LABELS: Record<WhenAsked, string> = {
  existing: 'Add the sentence to its card',
  new: 'Make a new note',
  skip: 'Skip the word',
};

export interface MineSearchRun {
  pipelineId: string | null;
  perWord: number;
  whenAsked: WhenAsked;
}

export interface MineSearch {
  id: string;
  name: string;
  query: Record<string, string>;
  run?: MineSearchRun | null;
}

/** The query keys a saved search keeps, without empty values. */
export function searchQuery(query: Record<string, unknown>): Record<string, string> {
  const keys = new Set<string>(MINE_SEARCH_KEYS);
  return Object.fromEntries(
    Object.entries(query).filter((e): e is [string, string] => keys.has(e[0]) && typeof e[1] === 'string' && e[1].length > 0)
  );
}

/** Two queries ask for the same search (key order doesn't matter). */
export function sameQuery(a: Record<string, unknown>, b: Record<string, unknown>): boolean {
  const x = searchQuery(a);
  const y = searchQuery(b);
  const keys = Object.keys(x);
  return keys.length === Object.keys(y).length && keys.every((k) => x[k] === y[k]);
}

/** Why a name can't be used, or null: blank, too long, or taken by another search (ignoring case). */
export function nameProblem(name: string, searches: readonly MineSearch[], exceptId?: string): string | null {
  const trimmed = name.trim();
  if (!trimmed) return 'Give the search a name.';
  if (trimmed.length > MAX_MINE_SEARCH_NAME) return `Names go up to ${MAX_MINE_SEARCH_NAME} characters.`;
  if (searches.some((s) => s.id !== exceptId && s.name.toLowerCase() === trimmed.toLowerCase())) return 'Another search has that name.';
  return null;
}

/**
 * What a bulk run does for a word that has a note, given the Anki step's `hasCard`: the word's card, a new note, or
 * nothing. A word this run already gave a card always gets its card, so a word has one note per run (GDR-059, GDR-010);
 * a step that would ask takes the answer chosen before the run; a step with no target for new notes can only add.
 */
export function runPick(hasCard: PipelineHasCard, hasTarget: boolean, whenAsked: WhenAsked, wordCarded: boolean): 'existing' | 'new' | 'skip' {
  if (wordCarded) return 'existing';
  if (hasCard === 'skip') return 'skip';
  if (hasCard === 'add') return 'existing';
  if (hasCard === 'new') return 'new';
  if (!hasTarget) return 'existing';
  return whenAsked;
}

/** Whether the pipeline's Anki steps can ask, so the run needs an answer up front. */
export function pipelineCanAsk(steps: readonly { kind: string; hasCard?: PipelineHasCard }[]): boolean {
  return steps.some((s) => s.kind === 'anki' && s.hasCard === 'ask');
}

/** One word of a run's plan: the word and the sentences to mine for it, in order. */
export interface RunItem<W, S> {
  word: W;
  sentences: S[];
}

/** The plan's sentences one by one, with whether each is the first of its word. */
export function* runSteps<W, S>(items: readonly RunItem<W, S>[]): Generator<{ word: W; sentence: S; index: number }> {
  let index = 0;
  for (const item of items) for (const sentence of item.sentences) yield { word: item.word, sentence, index: index++ };
}

export interface RunTally {
  created: number;
  added: number;
  alreadyOnCard: number;
  kept: number;
  skipped: number;
  alreadyDone: number;
  failed: RunProblem[];
  /** Mined, but with fields left empty (the Warn policies a single click shows as a toast). */
  warned: RunProblem[];
}

export interface RunProblem {
  word: string;
  wordId: number;
  readingIndex: number;
  reason: string;
}

export function emptyTally(): RunTally {
  return { created: 0, added: 0, alreadyOnCard: 0, kept: 0, skipped: 0, alreadyDone: 0, failed: [], warned: [] };
}

/** The run's summary in one line, e.g. "12 new notes · 3 added to cards · 1 failed". */
export function tallyLine(t: RunTally): string {
  const parts: string[] = [];
  const add = (n: number, one: string, many: string) => n > 0 && parts.push(`${n} ${n === 1 ? one : many}`);
  add(t.created, 'new note', 'new notes');
  add(t.added, 'added to a card', 'added to cards');
  add(t.alreadyOnCard, 'already on its card', 'already on their cards');
  add(t.kept, 'kept for Jiten', 'kept for Jiten');
  add(t.skipped, 'skipped', 'skipped');
  add(t.alreadyDone, 'already done', 'already done');
  add(t.warned.length, 'with empty fields', 'with empty fields');
  add(t.failed.length, 'failed', 'failed');
  return parts.join(' · ') || 'Nothing to do';
}
