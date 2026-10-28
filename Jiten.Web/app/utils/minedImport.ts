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
  english: 'SentenceEnglish',
  previousLine: 'PreviousLine',
  url: 'URL',
  miscInfo: 'MiscInfo',
  sentenceAudio: 'SentenceAudio',
  picture: 'Picture',
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
