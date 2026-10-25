/** A sentence's English, from the episode's English subtitles or an AI translation. */
export interface SentenceTranslation {
  text: string;
  origin: 'subtitles' | 'ai';
  provider: string | null;
  model: string | null;
  /** For subtitles, the release they came from. */
  source: string | null;
}

export interface SentenceTranslationOptions {
  cardMode: 'subtitlesThenAi' | 'subtitlesOnly' | 'off';
  pageMode: 'off' | 'button' | 'auto';
  pageAutoAi: boolean;
  /** An AI provider is set up and this user may call it (admins only). */
  aiAvailable: boolean;
}

// Shared by every page that shows or mines sentences; options rarely change, so they're loaded once per page load.
const options = ref<SentenceTranslationOptions | null>(null);
let optionsRequest: Promise<SentenceTranslationOptions | null> | null = null;
const translations = ref<Record<number, SentenceTranslation | null>>({});

export function useSentenceTranslation() {
  const { $api } = useNuxtApp();

  function loadOptions(): Promise<SentenceTranslationOptions | null> {
    optionsRequest ??= $api<SentenceTranslationOptions>('sentence-translation/options')
      .then((o) => (options.value = o))
      .catch(() => {
        optionsRequest = null;
        return null;
      });
    return optionsRequest;
  }

  /** One sentence's English; with ai, translates it with the AI provider when there's none yet. Null when there's none. */
  async function translate(sentenceId: number, ai: boolean): Promise<SentenceTranslation | null> {
    const known = translations.value[sentenceId];
    if (known || (known === null && !ai)) return known;
    const { translation } = await $api<{ translation: SentenceTranslation | null }>(`sentence-translation/${sentenceId}`, { query: { ai } });
    translations.value = { ...translations.value, [sentenceId]: translation };
    return translation;
  }

  /** Subtitle and cached English for many sentences at once, without AI calls. */
  async function translateMany(sentenceIds: number[]): Promise<void> {
    const missing = sentenceIds.filter((id) => !(id in translations.value));
    for (let i = 0; i < missing.length; i += 100) {
      const ids = missing.slice(i, i + 100);
      const { translations: found } = await $api<{ translations: Record<string, SentenceTranslation> }>('sentence-translation/batch', {
        method: 'POST',
        body: { sentenceIds: ids },
      });
      translations.value = { ...translations.value, ...Object.fromEntries(ids.map((id) => [id, found[id] ?? null])) };
    }
  }

  /** The English to put on a card, following the card mode; empty when there's none. */
  async function forCard(sentenceId: number): Promise<string> {
    const o = await loadOptions();
    if (!o || o.cardMode === 'off') return '';
    const translation = await translate(sentenceId, o.cardMode === 'subtitlesThenAi' && o.aiAvailable);
    return translation?.text ?? '';
  }

  return { options: readonly(options), translations: readonly(translations), loadOptions, translate, translateMany, forCard };
}
