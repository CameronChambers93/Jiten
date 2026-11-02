import type { ExampleSentence } from '~/types';
import type { MiningFieldSource } from '~/utils/miningCardFields';
import { guessFieldSource } from '~/utils/miningCardFields';

/**
 * What a note type's field holds. Card creation writes each role into its fields; import and Sync read the first field
 * with the role. The card-creation roles are `MiningFieldSource`; the rest are only read (url, previousLine) or written
 * by Sync (sentenceList).
 */
export type FieldRole = Exclude<MiningFieldSource, ''> | 'frequency' | 'url' | 'previousLine' | 'sentenceList';

export type KnownWordsState = 'seen' | 'review' | 'mature';
export type SentenceListFormat = 'script' | 'plain' | 'none';

/** How Sync treats one note type (`api/anki-sync/setups`). */
export interface AnkiNoteTypeSetup {
  modelName: string;
  /** Anki search ANDed with the note type; null for every note. */
  filter: string | null;
  /** Field name → role. */
  fieldRoles: Record<string, FieldRole>;
  preset: string | null;
  /** The card state that makes a word known; null when off. */
  knownWords: KnownWordsState | null;
  importSentences: boolean;
  takeFlags: boolean;
  foldDuplicates: boolean;
  updateMainFields: boolean;
  sentenceList: SentenceListFormat;
  scriptVersion: number | null;
  /** How mining treats the note type; null or missing for the defaults (`miningOf`). */
  mining?: MiningSettings | null;
}

/** When mining would leave a field empty: add the card and say so, or don't add it. */
export type EmptyFieldPolicy = 'warn' | 'deny';
export type EmptyFieldPolicies = Partial<Record<FieldRole, EmptyFieldPolicy>>;

/** When a word already has a card of the note type: add the sentence to it without asking, or ask. */
export type MiningExisting = 'auto' | 'ask';

/** The parts of a sentence mining builds for a note type. One that's off isn't cut or stored, and never counts as empty. */
export interface MiningParts {
  audio: boolean;
  picture: boolean;
  english: boolean;
}

/** How mining treats one note type, stored on its setup. */
export interface MiningSettings {
  existing: MiningExisting;
  parts: MiningParts;
  /** What mining does when it would leave a role empty; null for `DEFAULT_EMPTY_FIELDS`. */
  emptyFields: EmptyFieldPolicies | null;
}

export const DEFAULT_PARTS: MiningParts = { audio: true, picture: true, english: true };

/** The role each part fills. */
export const PART_ROLES: Record<keyof MiningParts, FieldRole> = { audio: 'sentenceAudio', picture: 'picture', english: 'sentenceEnglish' };

/** A setup's mining settings, with the defaults filled in. */
export function miningOf(setup: Pick<AnkiNoteTypeSetup, 'mining'> | null | undefined): MiningSettings {
  const m = setup?.mining;
  return { existing: m?.existing ?? 'ask', parts: { ...DEFAULT_PARTS, ...m?.parts }, emptyFields: m?.emptyFields ?? null };
}

/** The roles of parts that are off, which mining leaves empty on purpose. */
export function partsOffRoles(parts: MiningParts): FieldRole[] {
  return (Object.keys(PART_ROLES) as (keyof MiningParts)[]).filter((p) => !parts[p]).map((p) => PART_ROLES[p]);
}

/** A title a target's rule matches, with its name for showing the rule. */
export interface RuleTitle {
  deckId: number;
  title: string;
}

/** Which lines a target takes: any of the media types, or any of the titles. */
export interface CardTargetRule {
  mediaTypes: number[];
  titles: RuleTitle[];
}

/** One place new mined cards go. Its note type's setup has the field roles and mining settings. */
export interface NewCardTarget {
  deckName: string;
  modelName: string;
  tags: string;
  /** Null on the last target, which takes every line the others don't. */
  when: CardTargetRule | null;
}

/** Where new mined cards go (`api/anki-sync/card-target`): the first target whose rule matches; the audio padding is shared. */
export interface CardTargets {
  targets: NewCardTarget[];
  padStartMs: number;
  padEndMs: number;
}

export const DEFAULT_CARD_TARGETS: CardTargets = {
  targets: [{ deckName: '', modelName: '', tags: 'jiten', when: null }],
  padStartMs: 250,
  padEndMs: 250,
};

/** Whether the rule takes the line: its media type, or its title or the title's parent (a series for an episode). */
export function ruleMatches(rule: CardTargetRule | null, sentence: Pick<ExampleSentence, 'sourceDeck' | 'sourceDeckParent'>): boolean {
  if (!rule) return false;
  const mediaType = sentence.sourceDeckParent?.mediaType ?? sentence.sourceDeck?.mediaType;
  if (mediaType != null && rule.mediaTypes.includes(mediaType)) return true;
  const ids = [sentence.sourceDeck?.deckId, sentence.sourceDeckParent?.deckId].filter((id): id is number => id != null);
  return rule.titles.some((t) => ids.includes(t.deckId));
}

/** The target a new card for the line goes to: the first whose rule matches, else the last. */
export function pickTarget(targets: CardTargets, sentence: Pick<ExampleSentence, 'sourceDeck' | 'sourceDeckParent'>): NewCardTarget {
  const list = targets.targets.length ? targets.targets : DEFAULT_CARD_TARGETS.targets;
  return list.slice(0, -1).find((t) => ruleMatches(t.when, sentence)) ?? list[list.length - 1]!;
}

export const ROLE_LABELS: Record<FieldRole, string> = {
  word: 'Word',
  wordReading: 'Word reading (kana)',
  wordFurigana: 'Word with furigana',
  pitchPosition: 'Pitch accent positions',
  pitchCategories: 'Pitch accent categories',
  definitions: 'Definitions (all senses)',
  mainDefinition: 'Main definition',
  frequency: 'Frequency rank',
  sentence: 'Sentence, word in bold',
  sentencePlain: 'Sentence',
  sentenceFurigana: 'Sentence with furigana',
  sentenceEnglish: 'Sentence (English)',
  sentenceAudio: 'Sentence audio',
  picture: 'Screenshot',
  source: 'Source (title and episode)',
  url: 'Source URL (read on import)',
  previousLine: 'Previous line (read on import)',
  sentenceList: 'Sentence list (written by Sync)',
};

export const ROLE_OPTIONS: { value: FieldRole | ''; label: string }[] = [
  { value: '', label: '(none)' },
  ...(Object.keys(ROLE_LABELS) as FieldRole[]).map((value) => ({ value, label: ROLE_LABELS[value] })),
];

export const KNOWN_WORDS_OPTIONS: { value: KnownWordsState | null; label: string }[] = [
  { value: null, label: 'Off' },
  { value: 'seen', label: 'Reviewed at least once' },
  { value: 'review', label: 'Out of learning' },
  { value: 'mature', label: 'Mature (21+ days)' },
];

export const SENTENCE_LIST_OPTIONS: { value: SentenceListFormat; label: string; detail: string }[] = [
  { value: 'script', label: 'Rotation script', detail: 'Every sentence, for the card script that shows a different one each review' },
  { value: 'plain', label: 'Plain list', detail: 'Every sentence with its English, as a list in a field' },
  { value: 'none', label: 'Main fields only', detail: 'No list; the main sentence fields when updating them is on' },
];

interface Preset {
  name: string;
  roles: Record<string, FieldRole>;
}

const LAPIS_ROLES: Record<string, FieldRole> = {
  Expression: 'word',
  ExpressionFurigana: 'wordFurigana',
  ExpressionReading: 'wordReading',
  MainDefinition: 'mainDefinition',
  Glossary: 'definitions',
  Sentence: 'sentence',
  SentenceFurigana: 'sentenceFurigana',
  SentenceAudio: 'sentenceAudio',
  Picture: 'picture',
  PitchPosition: 'pitchPosition',
  PitchCategories: 'pitchCategories',
  FreqSort: 'frequency',
  MiscInfo: 'source',
};

/** Presets for the common mining note types, most specific first. */
export const PRESETS: Preset[] = [
  {
    name: 'Lapis+',
    roles: { ...LAPIS_ROLES, SentenceEnglish: 'sentenceEnglish', URL: 'url', PreviousLine: 'previousLine', Sentences: 'sentenceList' },
  },
  { name: 'Lapis', roles: { ...LAPIS_ROLES, SentenceEnglish: 'sentenceEnglish' } },
  { name: 'Kiku', roles: LAPIS_ROLES },
  {
    name: 'Core 2000',
    roles: {
      Expression: 'word',
      'Vocabulary-Furigana': 'wordFurigana',
      vocabularyKana: 'wordReading',
      vocabularyEnglish: 'mainDefinition',
      SentenceKanji: 'sentence',
      SentenceFurigana: 'sentenceFurigana',
      SentenceEnglish: 'sentenceEnglish',
      'Sentence-Audio': 'sentenceAudio',
      Sentences: 'sentenceList',
    },
  },
  {
    name: 'JP Mining Note',
    roles: {
      Word: 'word',
      WordReading: 'wordFurigana',
      WordReadingHiragana: 'wordReading',
      PrimaryDefinition: 'definitions',
      Sentence: 'sentence',
      SentenceReading: 'sentenceFurigana',
      SentenceAudio: 'sentenceAudio',
      Picture: 'picture',
      PAPositions: 'pitchPosition',
      FrequencySort: 'frequency',
    },
  },
];

/**
 * The preset whose fields all exist on the note type: the one covering the most fields, and on a tie the one named like
 * the note type. Null when none fits.
 */
export function matchPreset(modelName: string, fieldNames: string[]): Preset | null {
  const have = new Set(fieldNames);
  const fits = PRESETS.filter((p) => Object.keys(p.roles).every((f) => have.has(f)));
  if (!fits.length) return null;
  const size = (p: Preset) => Object.keys(p.roles).length;
  const best = Math.max(...fits.map(size));
  const top = fits.filter((p) => size(p) === best);
  return top.find((p) => modelName.toLowerCase().includes(p.name.toLowerCase())) ?? top[0]!;
}

/** Roles for a note type's fields: a preset's when one fits, else guesses from the field names. */
export function suggestRoles(modelName: string, fieldNames: string[]): { preset: string | null; roles: Record<string, FieldRole> } {
  const preset = matchPreset(modelName, fieldNames);
  if (preset) return { preset: preset.name, roles: { ...preset.roles } };
  const roles: Record<string, FieldRole> = {};
  const taken = new Set<string>();
  for (const name of fieldNames) {
    const guess = guessFieldSource(name);
    // A guess only fills a role once, so "Expression" and "Word" don't both become the word.
    if (guess && !taken.has(guess)) {
      roles[name] = guess;
      taken.add(guess);
    } else if (/^sentences$/i.test(name.trim()) && !taken.has('sentenceList')) {
      roles[name] = 'sentenceList';
      taken.add('sentenceList');
    }
  }
  return { preset: null, roles };
}

/** The first field holding the role, in the note type's field order when it's known. */
export function fieldFor(roles: Record<string, FieldRole | ''>, role: FieldRole, fieldOrder?: string[]): string | null {
  const names = fieldOrder ?? Object.keys(roles);
  return names.find((n) => roles[n] === role) ?? null;
}

/** Every field holding the role. */
export function fieldsFor(roles: Record<string, FieldRole | ''>, role: FieldRole): string[] {
  return Object.keys(roles).filter((n) => roles[n] === role);
}

export function hasRole(roles: Record<string, FieldRole | ''>, ...wanted: FieldRole[]): boolean {
  return Object.values(roles).some((r) => r && wanted.includes(r));
}

/** Which actions the roles allow; mirrors `AnkiSyncSetupService.Validate`. */
export function allowedActions(roles: Record<string, FieldRole | ''>) {
  const word = hasRole(roles, 'word');
  const sentence = hasRole(roles, 'sentence', 'sentencePlain', 'sentenceFurigana');
  return {
    knownWords: word,
    importSentences: word && sentence,
    takeFlags: true,
    foldDuplicates: true,
    updateMainFields: sentence,
    sentenceList: hasRole(roles, 'sentenceList'),
  };
}

/** Whether the setup does anything with sentences, so its notes are mining notes. */
export function hasSentenceActions(s: AnkiNoteTypeSetup): boolean {
  return s.importSentences || s.takeFlags || s.foldDuplicates || s.updateMainFields || s.sentenceList !== 'none';
}

/** A value for an Anki search term inside double quotes: quotes and backslashes escaped, wildcards taken literally. */
export function ankiSearchValue(value: string): string {
  return value.replace(/[\\"*_]/g, (c) => `\\${c}`);
}

/** The Anki search for a setup's notes: the note type, ANDed with its filter. */
export function setupQuery(setup: Pick<AnkiNoteTypeSetup, 'modelName' | 'filter'>, extra?: string): string {
  const parts = [`"note:${ankiSearchValue(setup.modelName)}"`];
  if (setup.filter?.trim()) parts.push(`(${setup.filter.trim()})`);
  if (extra) parts.push(extra);
  return parts.join(' ');
}

/** A new setup for a note type, with suggested roles and the actions they allow. */
export function newSetup(modelName: string, fieldNames: string[]): AnkiNoteTypeSetup {
  const { preset, roles } = suggestRoles(modelName, fieldNames);
  const allowed = allowedActions(roles);
  return {
    modelName,
    filter: null,
    fieldRoles: roles,
    preset,
    knownWords: null,
    importSentences: allowed.importSentences,
    takeFlags: allowed.importSentences,
    foldDuplicates: allowed.importSentences,
    updateMainFields: false,
    sentenceList: allowed.sentenceList && allowed.importSentences ? 'script' : 'none',
    scriptVersion: null,
  };
}

/** The setup with these roles, and the actions the roles no longer allow turned off, so the server accepts it. */
export function withRoles(setup: AnkiNoteTypeSetup, fieldRoles: Record<string, FieldRole>): AnkiNoteTypeSetup {
  const allowed = allowedActions(fieldRoles);
  return {
    ...setup,
    fieldRoles,
    knownWords: allowed.knownWords ? setup.knownWords : null,
    importSentences: allowed.importSentences && setup.importSentences,
    updateMainFields: allowed.updateMainFields && setup.updateMainFields,
    sentenceList: allowed.sentenceList ? setup.sentenceList : 'none',
  };
}

/** Turns the old per-browser field mapping (field → source) into roles. */
export function rolesFromMapping(mapping: Record<string, MiningFieldSource>): Record<string, FieldRole> {
  return Object.fromEntries(Object.entries(mapping).filter(([, v]) => !!v)) as Record<string, FieldRole>;
}
