import { extractAudioRef, extractImageRef } from '~/utils/ankiMediaExtract';

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
}

/** Lapis+ field names the import reads. */
export const LAPIS_FIELDS = {
  expression: 'Expression',
  reading: 'ExpressionReading',
  sentence: 'Sentence',
  sentenceFurigana: 'SentenceFurigana',
  english: 'SentenceEnglish',
  previousLine: 'PreviousLine',
  url: 'URL',
  miscInfo: 'MiscInfo',
  sentenceAudio: 'SentenceAudio',
  picture: 'Picture',
  /** The full sentence list Sync writes for the card's script. */
  sentences: 'Sentences',
} as const;

const JITEN_MEDIA = /^jiten_\d+\./;

function field(note: AnkiNoteInfo, name: string): string {
  return note.fields[name]?.value ?? '';
}

/**
 * The sentence media a note references, by Anki file name. Jiten's own media (`jiten_<sentenceId>.*`) is named but not
 * fetched: the API cuts it again from the episode.
 */
export function noteMedia(note: AnkiNoteInfo): { audio: string | null; picture: string | null; fetch: string[] } {
  const audio = extractAudioRef(field(note, LAPIS_FIELDS.sentenceAudio))?.filename ?? null;
  const picture = extractImageRef(field(note, LAPIS_FIELDS.picture))?.filename ?? null;
  const jiten = [audio, picture].some((f) => f && JITEN_MEDIA.test(f));
  return { audio, picture, fetch: jiten ? [] : [audio, picture].filter((f): f is string => !!f) };
}

/** The import payload for a note, given its deck and the base64 of each media file fetched. */
export function toImportNote(note: AnkiNoteInfo, deckName: string, media: Record<string, string>): ImportNote {
  const { audio, picture } = noteMedia(note);
  const optional = (name: string) => field(note, name) || null;
  return {
    ankiNoteId: note.noteId,
    modelName: note.modelName,
    deckName,
    tags: note.tags,
    expression: field(note, LAPIS_FIELDS.expression),
    reading: field(note, LAPIS_FIELDS.reading),
    sentence: field(note, LAPIS_FIELDS.sentence),
    furigana: optional(LAPIS_FIELDS.sentenceFurigana),
    english: optional(LAPIS_FIELDS.english),
    previousLine: optional(LAPIS_FIELDS.previousLine),
    url: optional(LAPIS_FIELDS.url),
    miscInfo: optional(LAPIS_FIELDS.miscInfo),
    audioFilename: audio,
    audioBase64: (audio && media[audio]) || null,
    pictureFilename: picture,
    pictureBase64: (picture && media[picture]) || null,
  };
}

/** The tag a card's flag button adds: `sentence-fix::<minedSentenceId>`. */
export const FLAG_TAG_PREFIX = 'sentence-fix::';

/** The sentence flags on notes, read from their tags. */
export function noteFlags(notes: Pick<AnkiNoteInfo, 'noteId' | 'tags'>[]): { ankiNoteId: number; minedSentenceId: number; tag: string }[] {
  return notes.flatMap((n) =>
    n.tags
      .filter((t) => t.toLowerCase().startsWith(FLAG_TAG_PREFIX))
      .map((tag) => ({ ankiNoteId: n.noteId, minedSentenceId: Number(tag.slice(FLAG_TAG_PREFIX.length)), tag }))
      .filter((f) => Number.isSafeInteger(f.minedSentenceId) && f.minedSentenceId > 0)
  );
}
