import { extractAudioRef, extractImageRef, extractVideoRef } from '~/utils/ankiMediaExtract';
import { fieldFor, type FieldRole } from '~/utils/ankiSetups';

/** An Anki note as AnkiConnect's notesInfo returns it. */
export interface AnkiNoteInfo {
  noteId: number;
  modelName: string;
  tags: string[];
  fields: Record<string, { value: string }>;
  cards: number[];
}

/** One note for `POST api/mined/import`. */
export interface ImportNote {
  ankiNoteId: number;
  modelName: string;
  deckName: string;
  tags: string[];
  expression: string;
  reading: string;
  sentence: string;
  furigana: string | null;
  english: string | null;
  previousLine: string | null;
  url: string | null;
  miscInfo: string | null;
  audioFilename: string | null;
  audioBase64: string | null;
  pictureFilename: string | null;
  pictureBase64: string | null;
  /** The Video field's clip, by name only: it stays in Anki's collection and Sync writes it back as it is. */
  videoFilename: string | null;
  /** Every field, without markup: decides between dictionary entries that share the word and reading. */
  text: string;
}

/** How long a note's text may be; the API reads no further. */
export const NOTE_TEXT_LIMIT = 20_000;

/**
 * All of a note's fields as plain text, whatever its note type, for telling homographs apart. The sentence list Jiten
 * writes is left out: it holds the word's other sentences, which may be another entry's until the note is matched.
 */
export function noteText(fields: Record<string, { value: string }>): string {
  return Object.values(fields)
    .filter((f) => !f.value.includes('class="jiten-sentences"'))
    .map((f) => f.value.replace(/\[sound:[^\]]*\]/g, ' ').replace(/<[^>]*>/g, ' '))
    .join('\n')
    .replace(/[ \t]+/g, ' ')
    .slice(0, NOTE_TEXT_LIMIT);
}

const JITEN_MEDIA = /^jiten_\d+\./;

/** The first field with the role, read as the note's value for it. */
function roleValue(note: AnkiNoteInfo, roles: Record<string, FieldRole | ''>, role: FieldRole): string {
  const name = fieldFor(roles, role, Object.keys(note.fields));
  return name ? (note.fields[name]?.value ?? '') : '';
}

/**
 * The sentence media a note references, by Anki file name. Jiten's own media (`jiten_<sentenceId>.*`) is named but not
 * fetched: the API cuts it again from the episode.
 */
export function noteMedia(note: AnkiNoteInfo, roles: Record<string, FieldRole | ''>): { audio: string | null; picture: string | null; fetch: string[] } {
  const audio = extractAudioRef(roleValue(note, roles, 'sentenceAudio'))?.filename ?? null;
  const picture = extractImageRef(roleValue(note, roles, 'picture'))?.filename ?? null;
  const jiten = [audio, picture].some((f) => f && JITEN_MEDIA.test(f));
  return { audio, picture, fetch: jiten ? [] : [audio, picture].filter((f): f is string => !!f) };
}

/** Kana from a furigana field: 漢字[かんじ] and <ruby>漢字<rt>かんじ</rt></ruby> both become かんじ. */
export function furiganaToKana(html: string): string {
  return html
    .replace(/<ruby>(?:<rb>)?[^<]*(?:<\/rb>)?<rt>([^<]*)<\/rt><\/ruby>/g, '$1')
    .replace(/<[^>]*>/g, '')
    .replace(/([^[\]\s]+)\[([^[\]]*)\]/g, '$2')
    .replace(/&nbsp;|\s/g, '');
}

/**
 * The import payload for a note through its note type's roles, given its deck and the base64 of each media file
 * fetched. The word's reading comes from the kana field, else from the word with furigana; the sentence from the bold
 * sentence, else the plain one, else the one with furigana.
 */
export function toImportNote(note: AnkiNoteInfo, deckName: string, media: Record<string, string>, roles: Record<string, FieldRole | ''>): ImportNote {
  const { audio, picture } = noteMedia(note, roles);
  const value = (role: FieldRole) => roleValue(note, roles, role);
  const optional = (role: FieldRole) => value(role) || null;
  return {
    ankiNoteId: note.noteId,
    modelName: note.modelName,
    deckName,
    tags: note.tags,
    expression: value('word'),
    reading: value('wordReading') || furiganaToKana(value('wordFurigana')),
    sentence: value('sentence') || value('sentencePlain') || value('sentenceFurigana'),
    furigana: optional('sentenceFurigana'),
    english: optional('sentenceEnglish'),
    previousLine: optional('previousLine'),
    url: optional('url'),
    miscInfo: optional('source'),
    audioFilename: audio,
    audioBase64: (audio && media[audio]) || null,
    pictureFilename: picture,
    pictureBase64: (picture && media[picture]) || null,
    videoFilename: extractVideoRef(value('video'))?.filename ?? null,
    text: noteText(note.fields),
  };
}

/** Why a sentence was flagged, as `api/sentence-flags` stores it. */
export const FLAG_REASONS = [
  { value: 'japanese', label: 'Japanese text' },
  { value: 'english', label: 'English' },
  { value: 'media', label: 'Audio or screenshot' },
  { value: 'parsing', label: 'Parsing' },
] as const;

export type FlagReason = (typeof FLAG_REASONS)[number]['value'];

/** The tag a card's flag button adds: `sentence-fix::<minedSentenceId>`. */
export const FLAG_TAG_PREFIX = 'sentence-fix::';

/** A tag added by hand, for cards without the flag button: flags the sentence in the note's main fields. */
export const PLAIN_FLAG_TAG = 'jiten-flag';

/** The Anki search for flagged notes. */
export const FLAG_QUERY = `("tag:${FLAG_TAG_PREFIX}*" OR "tag:${PLAIN_FLAG_TAG}")`;

/** The sentence flags on notes, read from their tags; the plain tag has no sentence id. */
export interface NoteFlag {
  ankiNoteId: number;
  minedSentenceId: number | null;
  tag: string;
}

export function noteFlags(notes: Pick<AnkiNoteInfo, 'noteId' | 'tags'>[]): NoteFlag[] {
  return notes.flatMap((n) =>
    n.tags.flatMap((tag): NoteFlag[] => {
      const lower = tag.toLowerCase();
      if (lower === PLAIN_FLAG_TAG) return [{ ankiNoteId: n.noteId, minedSentenceId: null, tag }];
      if (!lower.startsWith(FLAG_TAG_PREFIX)) return [];
      const id = Number(tag.slice(FLAG_TAG_PREFIX.length));
      return Number.isSafeInteger(id) && id > 0 ? [{ ankiNoteId: n.noteId, minedSentenceId: id, tag }] : [];
    })
  );
}
