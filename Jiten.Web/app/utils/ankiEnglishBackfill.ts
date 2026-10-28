/** The parts of an Anki note that the English backfill reads (from AnkiConnect's notesInfo). */
export interface BackfillNote {
  noteId: number;
  tags: string[];
  fields: Record<string, { value: string }>;
}

/** Shortest sentence worth translating, in characters of text (a lookup of just 読め isn't a sentence). */
export const MIN_SENTENCE_LENGTH = 4;

const entities: Record<string, string> = { '&nbsp;': ' ', '&amp;': '&', '&lt;': '<', '&gt;': '>', '&quot;': '"', '&#39;': "'" };

/** A field's text without tags, line breaks as spaces. */
export function plainText(html: string): string {
  return html
    .replace(/<br\s*\/?>|<\/div>/gi, ' ')
    .replace(/<[^>]*>/g, '')
    .replace(/&nbsp;|&amp;|&lt;|&gt;|&quot;|&#39;/g, (e) => entities[e]!)
    .replace(/\s+/g, ' ')
    .trim();
}

function field(note: BackfillNote, name: string): string {
  return note.fields[name]?.value ?? '';
}

/** The page a note was mined from: the URL field's link, or its text when it holds a bare URL. */
export function noteUrl(note: BackfillNote): URL | null {
  const value = field(note, 'URL');
  const raw = value.match(/href="([^"]+)"/i)?.[1] ?? plainText(value);
  try {
    return raw ? new URL(raw) : null;
  } catch {
    return null;
  }
}

/** Where a note's sentence came from, worded for the translation prompt ("Japanese text from …"). */
export function noteSource(note: BackfillNote): string {
  const misc = plainText(field(note, 'MiscInfo'));
  const url = noteUrl(note);

  if (note.tags.includes('jiten') && misc) return `the show "${misc}"`;
  if (misc.toLowerCase() === 'texthooker' || url?.pathname.toLowerCase().includes('texthooker')) return 'a video game';
  if (!url) return 'an unknown source';
  if (url.protocol === 'file:') return 'a document';

  const host = url.hostname.replace(/^www\d*\./, '');
  if (host === 'news.web.nhk' || (host === 'nhk.or.jp' && url.pathname.startsWith('/news/easy'))) return 'an NHK News Web Easy article';
  if (host.endsWith('nhk.or.jp') || host.endsWith('.nhk')) return 'an NHK news article';
  if (host === 'jiten.local' || host === 'jiten.moe' || host === 'localhost') return 'a TV show';
  return `the website ${host}`;
}

/** Why a note is left out of the backfill, or null to translate it. */
export function skipReason(note: BackfillNote, sentenceField: string): string | null {
  if (plainText(field(note, 'MiscInfo')).startsWith('Sample Card')) return 'Lapis sample card';
  const text = plainText(field(note, sentenceField));
  if (!text) return 'no sentence';
  if ([...text].length < MIN_SENTENCE_LENGTH) return 'fragment';
  return null;
}

/** Lines before the sentence, for the prompt's context: the note's PreviousLine, when it has one. */
export function noteContext(note: BackfillNote): string[] {
  const previous = plainText(field(note, 'PreviousLine'));
  return previous ? [previous] : [];
}
