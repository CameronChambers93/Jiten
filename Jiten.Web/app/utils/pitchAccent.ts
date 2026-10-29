export type PitchCategory = 'heiban' | 'atamadaka' | 'nakadaka' | 'odaka';

const SMALL_KANA = /[ぁぃぅぇぉゃゅょゎァィゥェォャュョヮ]/g;
const KANA = /[ぁ-ゖァ-ヺー]/g;

/** The kana of a reading written as ruby markup (食[た]べる) or as plain kana. */
export function readingKana(reading: string): string {
  return (reading.replace(/[一-鿿０-ｚ々ヵヶ]+(?=\[)/g, '').match(KANA) ?? []).join('');
}

/** Small kana share the mora of the kana before them; っ and ー are morae of their own. */
export function moraCount(kana: string): number {
  return kana.replace(SMALL_KANA, '').length;
}

export function pitchCategory(accent: number, morae: number): PitchCategory | null {
  if (accent < 0 || morae <= 0 || accent > morae) return null;
  if (accent === 0) return 'heiban';
  if (accent === 1) return 'atamadaka';
  return accent === morae ? 'odaka' : 'nakadaka';
}

export function wordPitchCategory(reading: string, accents: number[] | null | undefined): PitchCategory | null {
  if (!accents?.length) return null;
  const morae = moraCount(readingKana(reading));
  const categories = new Set(accents.map((accent) => pitchCategory(accent, morae)));
  const [only] = categories;
  return categories.size === 1 && only ? only : null;
}

/** Verbs and i-adjectives, which Yomitan calls kifuku when they drop (suru nouns, `vs`, are nouns). */
const KIFUKU_POS = /^(adj-i|v1|v2|v4|v5|vk|vz|vs-[is]|vr)/;

/**
 * Yomitan's {pitch-accent-categories} for an Anki card: one per accent, unique, comma-separated; a verb or i-adjective
 * that drops is kifuku. Lapis colours the card from it.
 */
export function ankiPitchCategories(reading: string, accents: number[] | null | undefined, partsOfSpeech: string[]): string {
  if (!accents?.length) return '';
  const morae = moraCount(readingKana(reading));
  const kifuku = partsOfSpeech.some((p) => KIFUKU_POS.test(p));
  const categories = accents.map((accent) => (kifuku && accent > 0 ? 'kifuku' : pitchCategory(accent, morae))).filter(Boolean);
  return [...new Set(categories)].join(',');
}

/** Yomitan's {pitch-accent-positions} as Lapis reads it: [2], or [1][0] for several. */
export function ankiPitchPositions(accents: number[] | null | undefined): string {
  return (accents ?? []).map((accent) => `[${accent}]`).join('');
}

export function pitchColourClasses(reading: string, accents: number[] | null | undefined): string {
  const category = wordPitchCategory(reading, accents);
  if (!category) return '';
  return reading.includes('[') ? `pitch-coloured pitch-${category}` : `pitch-coloured pitch-kana pitch-${category}`;
}

export const PITCH_CATEGORY_LABELS: Record<PitchCategory, { name: string; description: string }> = {
  heiban: { name: 'Heiban', description: 'Starts low, rises, and never drops' },
  atamadaka: { name: 'Atamadaka', description: 'High on the first mora, then drops' },
  nakadaka: { name: 'Nakadaka', description: 'Drops inside the word' },
  odaka: { name: 'Odaka', description: 'Drops right after the word' },
};
