import type { DeckIPlusOneSentence, Word } from '~/types';
import type { MiningFieldSource } from '~/utils/miningCardFields';
import { escapeHtml } from '~/utils/sanitiseHtml';
import {
  ankiSentenceFurigana,
  ankiWordFurigana,
  boldTarget,
  definitionsHtml,
  firstSense,
  guessFieldSource,
  kanaReading,
  sourceLabel,
} from '~/utils/miningCardFields';

/** Which Anki deck and note type mined sentences go to, and what fills each field. Kept per browser. */
export interface MiningCardSettings {
  version: 1;
  deckName: string;
  modelName: string;
  /** Field mapping per note type, so switching note types and back keeps each one's mapping. */
  fieldsByModel: Record<string, Record<string, MiningFieldSource>>;
  tags: string;
  padStartMs: number;
  padEndMs: number;
  /** AnkiConnect's optional apiKey. */
  ankiKey: string;
}

const STORAGE_KEY = 'jiten-mining-cards';
const ANKI_CONNECT_URL = 'http://127.0.0.1:8765';

const defaults = (): MiningCardSettings => ({
  version: 1,
  deckName: '',
  modelName: '',
  fieldsByModel: {},
  tags: 'jiten',
  padStartMs: 250,
  padEndMs: 250,
  ankiKey: '',
});

// Shared by the settings dialog and every page that mines.
const settings = ref<MiningCardSettings>(defaults());
let loaded = false;

function loadSettings() {
  if (loaded || !import.meta.client) return;
  loaded = true;
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    const blob = raw ? (JSON.parse(raw) as MiningCardSettings) : null;
    if (blob?.version === 1) settings.value = { ...defaults(), ...blob };
  } catch {}
}

export async function ankiConnect<T = unknown>(action: string, params: Record<string, unknown> = {}, key = settings.value.ankiKey): Promise<T> {
  const body: Record<string, unknown> = { action, version: 6, params };
  if (key) body.key = key;
  const res = await fetch(ANKI_CONNECT_URL, { method: 'POST', body: JSON.stringify(body) });
  const json = await res.json();
  if (json.error) throw new Error(json.error);
  return json.result as T;
}

async function blobToBase64(blob: Blob): Promise<string> {
  const bytes = new Uint8Array(await blob.arrayBuffer());
  let binary = '';
  for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(binary);
}

export function useMiningCards() {
  loadSettings();
  const { $api } = useNuxtApp();
  const { forCard: englishForCard } = useSentenceTranslation();

  const configured = computed(() => !!settings.value.deckName && !!settings.value.modelName && !!settings.value.fieldsByModel[settings.value.modelName]);

  function saveSettings(next: MiningCardSettings) {
    settings.value = next;
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
    } catch {}
  }

  /** The note type's fields with their saved mapping, or a guess from the field names. */
  function mappingFor(modelName: string, fieldNames: string[]): Record<string, MiningFieldSource> {
    const saved = settings.value.fieldsByModel[modelName] ?? {};
    return Object.fromEntries(fieldNames.map((name) => [name, name in saved ? saved[name]! : guessFieldSource(name)]));
  }

  function fetchAudio(sentenceId: number): Promise<Blob> {
    return $api<Blob>(`sentence-media/${sentenceId}/audio`, {
      responseType: 'blob',
      query: { padStartMs: settings.value.padStartMs, padEndMs: settings.value.padEndMs },
    });
  }

  function fetchPicture(sentenceId: number): Promise<Blob> {
    return $api<Blob>(`sentence-media/${sentenceId}/picture`, { responseType: 'blob' });
  }

  /** Creates the note in Anki through AnkiConnect; resolves to the new note id. */
  async function addCard(item: DeckIPlusOneSentence): Promise<number> {
    const { sentence, word } = item;
    const mapping = settings.value.fieldsByModel[settings.value.modelName] ?? {};
    const wanted = new Set(Object.values(mapping));
    const fieldsFor = (source: MiningFieldSource) => Object.keys(mapping).filter((name) => mapping[name] === source);

    const [details, audio, picture, english] = await Promise.all([
      wanted.has('definitions') || wanted.has('mainDefinition') ? $api<Word>(`vocabulary/${word.wordId}/${word.readingIndex}`) : null,
      wanted.has('sentenceAudio') ? fetchAudio(sentence.sentenceId) : null,
      wanted.has('picture') ? fetchPicture(sentence.sentenceId) : null,
      // A failed translation leaves the field empty rather than losing the card.
      wanted.has('sentenceEnglish') ? englishForCard(sentence.sentenceId).catch(() => '') : '',
    ]);

    const values: Record<Exclude<MiningFieldSource, '' | 'sentenceAudio' | 'picture'>, string> = {
      word: escapeHtml(word.reading),
      wordReading: escapeHtml(kanaReading(word.readingFurigana || word.reading)),
      wordFurigana: escapeHtml(ankiWordFurigana(word.readingFurigana || word.reading)),
      definitions: details ? definitionsHtml(details.definitions, word.readingIndex) : '',
      mainDefinition: escapeHtml((details && firstSense(details.definitions, word.readingIndex)) || word.mainDefinition || ''),
      sentence: boldTarget(sentence),
      sentencePlain: escapeHtml(sentence.text),
      sentenceFurigana: ankiSentenceFurigana(sentence.text, sentence.furigana ?? []),
      sentenceEnglish: escapeHtml(english),
      source: escapeHtml(sourceLabel(sentence)),
    };

    const fields: Record<string, string> = {};
    for (const [name, source] of Object.entries(mapping)) {
      if (source && source in values) fields[name] = values[source as keyof typeof values];
    }

    const note: Record<string, unknown> = {
      deckName: settings.value.deckName,
      modelName: settings.value.modelName,
      fields,
      tags: settings.value.tags.split(/\s+/).filter(Boolean),
      options: { allowDuplicate: false, duplicateScope: 'deck' },
    };
    if (audio) note.audio = [{ data: await blobToBase64(audio), filename: `jiten_${sentence.sentenceId}.mp3`, fields: fieldsFor('sentenceAudio') }];
    if (picture) note.picture = [{ data: await blobToBase64(picture), filename: `jiten_${sentence.sentenceId}.jpg`, fields: fieldsFor('picture') }];

    return ankiConnect<number>('addNote', { note });
  }

  return { settings: readonly(settings), configured, saveSettings, mappingFor, fetchAudio, addCard };
}
