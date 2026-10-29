import type { Definition, ExampleSentence, SentenceFurigana } from '~/types';

const FIELD_TEXT_ENTITIES: Record<string, string> = { '&': '&amp;', '<': '&lt;', '>': '&gt;' };

/**
 * Text for an Anki field, which holds HTML: only & < > are escaped. Quotes are fine outside attributes, and escaping
 * them leaves You&#39;re in the field, which Anki's search and other tools then see.
 */
export function escapeFieldText(text: string): string {
  return text.replace(/[&<>]/g, (c) => FIELD_TEXT_ENTITIES[c]!);
}

/** What a field of the user's Anki note type is filled with when a sentence is mined. */
export type MiningFieldSource =
  | ''
  | 'word'
  | 'wordReading'
  | 'wordFurigana'
  | 'pitchPosition'
  | 'pitchCategories'
  | 'definitions'
  | 'mainDefinition'
  | 'sentence'
  | 'sentencePlain'
  | 'sentenceFurigana'
  | 'sentenceEnglish'
  | 'sentenceAudio'
  | 'picture'
  | 'source';

export const MINING_FIELD_SOURCES: { value: MiningFieldSource; label: string }[] = [
  { value: '', label: '(leave empty)' },
  { value: 'word', label: 'Word' },
  { value: 'wordReading', label: 'Word reading (kana)' },
  { value: 'wordFurigana', label: 'Word with furigana' },
  { value: 'pitchPosition', label: 'Pitch accent positions ([2])' },
  { value: 'pitchCategories', label: 'Pitch accent categories (heiban, …)' },
  { value: 'definitions', label: 'Definitions (all senses)' },
  { value: 'mainDefinition', label: 'Main definition' },
  { value: 'sentence', label: 'Sentence, word in bold' },
  { value: 'sentencePlain', label: 'Sentence' },
  { value: 'sentenceFurigana', label: 'Sentence with furigana' },
  { value: 'sentenceEnglish', label: 'Sentence (English)' },
  { value: 'sentenceAudio', label: 'Sentence audio' },
  { value: 'picture', label: 'Screenshot' },
  { value: 'source', label: 'Source (title and episode)' },
];

/** Field-name guesses for common mining note types (Lapis, GSM/Yomitan defaults, Kaishi). */
const FIELD_GUESSES: [RegExp, MiningFieldSource][] = [
  [/^(expression|word|term|vocab(ulary)?|target ?word|key)$/i, 'word'],
  [/^(expression|word|vocab)?furigana$/i, 'wordFurigana'],
  [/^(expression|word|vocab)?reading$/i, 'wordReading'],
  [/^pitch ?(accent ?)?positions?$/i, 'pitchPosition'],
  [/^pitch ?(accent ?)?categor(y|ies)$/i, 'pitchCategories'],
  [/^(main ?definition|meaning)$/i, 'mainDefinition'],
  [/^(glossary|definitions?|vocab ?def(inition)?)$/i, 'definitions'],
  [/^sentence$/i, 'sentence'],
  [/^sentence ?furigana$/i, 'sentenceFurigana'],
  [/^(sentence ?(english|translation|meaning)|translation|english)$/i, 'sentenceEnglish'],
  [/^sentence ?audio$/i, 'sentenceAudio'],
  [/^(picture|image|screenshot)$/i, 'picture'],
  [/^(source|misc ?info|game|show)$/i, 'source'],
];

export function guessFieldSource(fieldName: string): MiningFieldSource {
  return FIELD_GUESSES.find(([pattern]) => pattern.test(fieldName.trim()))?.[1] ?? '';
}

const RUBY_MARKUP = /([一-鿿０-ｚ々ヵヶ]+)\[([぀-ゟ゠-ヿ]+)]/g;

/** Jiten's ruby markup (漢[かん]字[じ]) as kana only. */
export function kanaReading(readingFurigana: string): string {
  return readingFurigana.replace(RUBY_MARKUP, '$2');
}

/** Jiten's ruby markup in Anki's furigana syntax, where a space ends the text a reading covers: お 茶[ちゃ]. */
export function ankiWordFurigana(readingFurigana: string): string {
  return readingFurigana.replace(RUBY_MARKUP, ' $1[$2]').trim();
}

/** A sentence in Anki's furigana syntax from its ruby spans; spans whose reading is their own text are left bare. */
export function ankiSentenceFurigana(text: string, furigana: Pick<SentenceFurigana, 'position' | 'length' | 'reading'>[]): string {
  let result = '';
  let at = 0;
  for (const span of [...furigana].sort((a, b) => a.position - b.position)) {
    if (span.position < at) continue;
    const surface = text.slice(span.position, span.position + span.length);
    result += escapeFieldText(text.slice(at, span.position));
    result += surface === span.reading || !span.reading ? escapeFieldText(surface) : ` ${escapeFieldText(surface)}[${escapeFieldText(span.reading)}]`;
    at = span.position + span.length;
  }
  return (result + escapeFieldText(text.slice(at))).trim();
}

export function boldTarget(sentence: Pick<ExampleSentence, 'text' | 'wordPosition' | 'wordLength'>): string {
  const { text, wordPosition: start, wordLength: length } = sentence;
  if (length <= 0) return escapeFieldText(text);
  return `${escapeFieldText(text.slice(0, start))}<b>${escapeFieldText(text.slice(start, start + length))}</b>${escapeFieldText(text.slice(start + length))}`;
}

function sensesFor(definitions: Definition[], readingIndex: number): Definition[] {
  return definitions.filter((d) => !d.restrictedToReadingIndices?.length || d.restrictedToReadingIndices.includes(readingIndex));
}

/** The meanings of the first sense that applies to the reading. */
export function firstSense(definitions: Definition[], readingIndex: number): string {
  return sensesFor(definitions, readingIndex)[0]?.meanings.join('; ') ?? '';
}

/** The senses that apply to the reading, as a numbered list with parts of speech. */
export function definitionsHtml(definitions: Definition[], readingIndex: number): string {
  const senses = sensesFor(definitions, readingIndex);
  if (senses.length === 0) return '';
  const items = senses.map((d) => {
    const pos = d.partsOfSpeech.length ? `<i>(${escapeFieldText(d.partsOfSpeech.join(', '))})</i> ` : '';
    return `<li>${pos}${escapeFieldText(d.meanings.join('; '))}</li>`;
  });
  return `<ol>${items.join('')}</ol>`;
}

export function sourceLabel(sentence: Pick<ExampleSentence, 'sourceDeck' | 'sourceDeckParent'>): string {
  return [sentence.sourceDeckParent?.originalTitle, sentence.sourceDeck?.originalTitle].filter(Boolean).join(' ');
}
