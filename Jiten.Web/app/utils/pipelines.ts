import type { ExampleSentence } from '~/types';
import { pickTarget, ruleMatches, type CardTargetRule, type MiningParts, type NewCardTarget, type RuleTitle } from '~/utils/ankiSetups';

/** When a step runs: the line's media type or title (either; both empty is any line), and the word's card's note type. */
export interface PipelineCondition {
  mediaTypes: number[];
  titles: RuleTitle[];
  /** The word's existing card is on one of these note types; empty for any word, with a card or without. */
  cardNoteTypes: string[];
}

export type PipelineEnglish = 'subtitlesThenAi' | 'subtitlesOnly' | 'none';
export type PipelineHasCard = 'add' | 'ask' | 'new' | 'skip';
export type PipelineNoCard = 'targets' | 'skip';

export interface PipelineTarget {
  deckName: string;
  modelName: string;
  when: CardTargetRule | null;
}

export interface BuildStep {
  kind: 'build';
  when: PipelineCondition | null;
  audio: boolean;
  picture: boolean;
  video: boolean;
  english: PipelineEnglish;
  padStartMs: number;
  padEndMs: number;
}

export interface AnkiStep {
  kind: 'anki';
  when: PipelineCondition | null;
  hasCard: PipelineHasCard;
  noCard: PipelineNoCard;
  targets: PipelineTarget[];
}

export interface TagsStep {
  kind: 'tags';
  when: PipelineCondition | null;
  tags: string;
  titleTag: boolean;
  mediaTypeTag: boolean;
}

export type PipelineStep = BuildStep | AnkiStep | TagsStep;

export interface Pipeline {
  id: string;
  name: string;
  steps: PipelineStep[];
}

/** The pages with a sentence button, each running its own default pipeline. */
export type PipelinePage = 'word' | 'mine' | 'deckSentences' | 'sentence';

export const PIPELINE_PAGES: { key: PipelinePage; label: string }[] = [
  { key: 'word', label: 'Word page' },
  { key: 'mine', label: 'Mine' },
  { key: 'deckSentences', label: 'Deck sentences' },
  { key: 'sentence', label: 'Sentence page' },
];

export interface MiningPipelines {
  pipelines: Pipeline[];
  defaults: Record<PipelinePage, string>;
  /** False while the pipelines are the ones built from the older card settings, not saved yet. */
  saved: boolean;
}

export const ENGLISH_LABELS: Record<PipelineEnglish, string> = {
  subtitlesThenAi: 'Subtitles then AI',
  subtitlesOnly: 'Subtitles only',
  none: 'None',
};

export const HAS_CARD_LABELS: Record<PipelineHasCard, string> = { add: 'Add to it', ask: 'Ask', new: 'New card', skip: 'Skip' };
export const NO_CARD_LABELS: Record<PipelineNoCard, string> = { targets: 'Targets', skip: 'Skip' };

export function newStep(kind: PipelineStep['kind']): PipelineStep {
  if (kind === 'build') return { kind, when: null, audio: true, picture: true, video: true, english: 'subtitlesThenAi', padStartMs: 250, padEndMs: 250 };
  if (kind === 'anki') return { kind, when: null, hasCard: 'ask', noCard: 'targets', targets: [{ deckName: '', modelName: '', when: null }] };
  return { kind, when: null, tags: '', titleTag: false, mediaTypeTag: false };
}

/** Fills what the server leaves out (null options and empty condition lists), so the editor can bind to every field. */
export function normalizePipelines(raw: MiningPipelines): MiningPipelines {
  const condition = (c: Partial<PipelineCondition> | null | undefined): PipelineCondition | null =>
    c ? { mediaTypes: c.mediaTypes ?? [], titles: c.titles ?? [], cardNoteTypes: c.cardNoteTypes ?? [] } : null;
  return {
    saved: raw.saved,
    defaults: raw.defaults,
    pipelines: raw.pipelines.map((p) => ({
      id: p.id,
      name: p.name,
      steps: (p.steps ?? []).map((s) => {
        const base = { ...newStep(s.kind), ...Object.fromEntries(Object.entries(s).filter(([, v]) => v != null)) } as PipelineStep;
        base.when = condition(s.when);
        // Only what was saved: the blank target a new Anki step starts with isn't one.
        if (base.kind === 'anki') base.targets = ((s as Partial<AnkiStep>).targets ?? []).map((t) => ({ ...t, when: t.when ?? null }));
        return base;
      }),
    })),
  };
}

/** What a condition is checked against: the line, and the note type of the word's card (empty when it has none). */
export interface PipelineContext {
  sentence: Pick<ExampleSentence, 'sourceDeck' | 'sourceDeckParent'>;
  cardNoteType: string | null;
}

/** Whether a step's condition lets it run. No condition: always. */
export function conditionMatches(when: PipelineCondition | null, ctx: PipelineContext): boolean {
  if (!when) return true;
  if (when.mediaTypes.length || when.titles.length) {
    if (!ruleMatches({ mediaTypes: when.mediaTypes, titles: when.titles }, ctx.sentence)) return false;
  }
  if (when.cardNoteTypes.length && (!ctx.cardNoteType || !when.cardNoteTypes.includes(ctx.cardNoteType))) return false;
  return true;
}

/** Whether the pipeline can add to Anki: it has an Anki step. Without one it only saves to the word's list. */
export function hasAnkiStep(pipeline: Pipeline): boolean {
  return pipeline.steps.some((s) => s.kind === 'anki');
}

/** The first matching step of a kind; of several matching Anki or Build steps, only the first runs. */
export function firstStep<K extends PipelineStep['kind']>(pipeline: Pipeline, kind: K, ctx: PipelineContext): Extract<PipelineStep, { kind: K }> | null {
  return (pipeline.steps.find((s) => s.kind === kind && conditionMatches(s.when, ctx)) as Extract<PipelineStep, { kind: K }> | undefined) ?? null;
}

/** What the Build step makes: the parts, English and padding. No matching Build step: text only. */
export interface BuildChoice {
  parts: MiningParts;
  english: PipelineEnglish;
  padStartMs: number;
  padEndMs: number;
}

export const TEXT_ONLY: BuildChoice = { parts: { audio: false, picture: false, english: false, video: false }, english: 'none', padStartMs: 250, padEndMs: 250 };

export function buildChoice(step: BuildStep | null): BuildChoice {
  if (!step) return TEXT_ONLY;
  return {
    parts: { audio: step.audio, picture: step.picture, video: step.video, english: step.english !== 'none' },
    english: step.english,
    padStartMs: step.padStartMs,
    padEndMs: step.padEndMs,
  };
}

/** The target an Anki step's new note goes to: the first whose rule matches the line, else the last. Null without targets. */
export function stepTarget(step: AnkiStep, sentence: PipelineContext['sentence'], tags: string): NewCardTarget | null {
  if (!step.targets.length) return null;
  return pickTarget({ targets: step.targets.map((t) => ({ ...t, tags })), padStartMs: 0, padEndMs: 0 }, sentence);
}

/** An Anki tag from a title or media type: spaces become underscores, as Anki splits tags on spaces. */
function asTag(text: string): string {
  return text.trim().replace(/\s+/g, '_');
}

/** Every matching Tags step's tags, once each, for a new note. */
export function pipelineTags(pipeline: Pipeline, ctx: PipelineContext, mediaTypeName: (type: number) => string): string {
  const tags: string[] = [];
  const deck = ctx.sentence.sourceDeckParent ?? ctx.sentence.sourceDeck;
  for (const step of pipeline.steps) {
    if (step.kind !== 'tags' || !conditionMatches(step.when, ctx)) continue;
    tags.push(...step.tags.split(/\s+/).filter(Boolean));
    if (step.titleTag && deck?.originalTitle) tags.push(asTag(deck.romajiTitle || deck.originalTitle));
    if (step.mediaTypeTag && deck?.mediaType != null) tags.push(asTag(mediaTypeName(deck.mediaType)));
  }
  return [...new Set(tags)].join(' ');
}

/** One line saying what a step does, for the editor's collapsed rows. */
export function stepSummary(step: PipelineStep): string {
  if (step.kind === 'build') {
    const parts = [step.audio && 'Audio', step.picture && 'Screenshot', step.video && 'Video'].filter(Boolean) as string[];
    if (step.english !== 'none') parts.push(`English (${step.english === 'subtitlesThenAi' ? 'subtitles then AI' : 'subtitles only'})`);
    return `${parts.length ? parts.join(', ') : 'Text only'} · padding ${step.padStartMs} / ${step.padEndMs} ms`;
  }
  if (step.kind === 'anki') {
    const targets = step.targets.length === 1 ? `${step.targets[0]!.deckName || 'no deck'}, ${step.targets[0]!.modelName || 'no note type'}` : `${step.targets.length} targets`;
    const noCard = step.noCard === 'targets' ? targets : 'skip';
    return `Has a card: ${HAS_CARD_LABELS[step.hasCard].toLowerCase()} · No card: ${noCard}`;
  }
  const extra = [step.titleTag && 'the title', step.mediaTypeTag && 'the media type'].filter(Boolean).join(' and ');
  return `${step.tags.trim() || 'No fixed tags'}${extra ? ` · plus ${extra}` : ''} · new cards only`;
}

/** The step's condition as chips: "only if …". */
export function conditionChips(when: PipelineCondition | null, mediaTypeName: (type: number) => string): string[] {
  if (!when) return [];
  const chips: string[] = [];
  const lines = [...when.mediaTypes.map(mediaTypeName), ...when.titles.map((t) => t.title)];
  if (lines.length) chips.push(`only for ${lines.join(' or ')}`);
  if (when.cardNoteTypes.length) chips.push(`only if card is on ${when.cardNoteTypes.join(' or ')}`);
  return chips;
}
