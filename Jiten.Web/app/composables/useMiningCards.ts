import type { DeckIPlusOneSentence, ExampleSentence, Word } from '~/types';
import type { MiningFieldSource } from '~/utils/miningCardFields';
import { escapeHtml } from '~/utils/sanitiseHtml';
import { LAPIS_FIELDS } from '~/utils/minedImport';
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

/** A value for an Anki search term inside double quotes: quotes and backslashes escaped, wildcards taken literally. */
function ankiSearchValue(value: string): string {
  return value.replace(/[\\"*_]/g, (c) => `\\${c}`);
}

function isNotFound(e: unknown): boolean {
  const err = e as { statusCode?: number; response?: { status?: number } } | null;
  return err?.statusCode === 404 || err?.response?.status === 404;
}

/** A media file for a card, named after the example sentence so the sentence store recognises Jiten's media. */
interface CardFile {
  blob: Blob;
  filename: string;
}

/** What adding a sentence to a word's existing note did; see `POST api/mined/sentences`. */
export interface AddedSentence {
  ankiNoteId: number;
  minedSentenceId: number;
  /** False when the word already had the sentence. */
  added: boolean;
  /** The word's sentences, counting this one. */
  sentences: number;
  sentencesHtml: string;
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
  const { forCard: englishForCard, forCardTranslation } = useSentenceTranslation();
  const { importNotes } = useMinedImport();
  const store = useJitenStore();

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

  /**
   * The sentence's audio and screenshot, cut from its episode. Without a linked video the audio is text-to-speech and
   * there is no screenshot.
   */
  async function cardMedia(sentenceId: number, wantAudio: boolean, wantPicture: boolean): Promise<{ audio: CardFile | null; picture: CardFile | null }> {
    const audio = async (): Promise<CardFile | null> => {
      try {
        return { blob: await fetchAudio(sentenceId), filename: `jiten_${sentenceId}.mp3` };
      } catch (e) {
        if (!isNotFound(e)) throw e;
        const voice = store.ttsVoice === 'random' ? 'female' : store.ttsVoice;
        const blob = await $api<Blob>(`tts/sentence/${sentenceId}`, { responseType: 'blob', query: { voice } });
        return { blob, filename: `jiten_${sentenceId}.ogg` };
      }
    };
    const picture = async (): Promise<CardFile | null> => {
      try {
        return { blob: await fetchPicture(sentenceId), filename: `jiten_${sentenceId}.jpg` };
      } catch (e) {
        if (isNotFound(e)) return null;
        throw e;
      }
    };
    const [a, p] = await Promise.all([wantAudio ? audio() : null, wantPicture ? picture() : null]);
    return { audio: a, picture: p };
  }

  /**
   * The word's notes in Anki, oldest first: notes of the card settings' note type whose word field holds it (the check
   * "Hide words already in Anki" makes). Empty when no field is mapped to the word.
   */
  async function existingNotes(word: string): Promise<number[]> {
    const { modelName } = settings.value;
    const mapping = settings.value.fieldsByModel[modelName] ?? {};
    const wordFields = Object.keys(mapping).filter((name) => mapping[name] === 'word');
    if (!modelName || wordFields.length === 0 || !word) return [];
    const fields = wordFields.map((f) => `"${ankiSearchValue(f)}:${ankiSearchValue(word)}"`).join(' OR ');
    const ids = await ankiConnect<number[]>('findNotes', { query: `"note:${ankiSearchValue(modelName)}" (${fields})` });
    return ids.sort((a, b) => a - b);
  }

  /**
   * Adds the sentence to the word's existing note instead of creating another: it joins the word's sentences in the
   * store, its media goes into Anki's collection, and the kept note's Sentences field is rewritten. Notes not in the
   * store yet (a Yomitan card never synced) are imported first, so their own sentence stays on the card.
   */
  async function addToExistingNote(sentence: ExampleSentence, noteIds: number[]): Promise<AddedSentence> {
    const { ankiNoteIds } = await $api<{ ankiNoteIds: number[] }>('mined/notes');
    const known = new Set(ankiNoteIds);
    const missing = noteIds.filter((id) => !known.has(id));
    if (missing.length) {
      const failed = (await importNotes(missing)).find((r) => r.status === 'failed');
      if (failed) throw new Error(failed.error ?? "Couldn't read the existing note");
    }

    const [media, translation] = await Promise.all([
      cardMedia(sentence.sentenceId, true, true),
      // A failed translation leaves the sentence without English rather than losing it.
      forCardTranslation(sentence.sentenceId).catch(() => null),
    ]);
    for (const file of [media.audio, media.picture]) {
      if (file) await ankiConnect('storeMediaFile', { filename: file.filename, data: await blobToBase64(file.blob) });
    }

    const result = await $api<AddedSentence>('mined/sentences', {
      method: 'POST',
      body: {
        ankiNoteId: noteIds[0],
        exampleSentenceId: sentence.sentenceId,
        sentence: boldTarget(sentence),
        furigana: sentence.furigana?.length ? ankiSentenceFurigana(sentence.text, sentence.furigana) : null,
        english: translation?.text ? escapeHtml(translation.text) : null,
        englishOrigin: translation?.origin ?? null,
        sourceTitle: sourceLabel(sentence) || null,
        audioFilename: media.audio?.filename ?? null,
        pictureFilename: media.picture?.filename ?? null,
      },
    });
    if (result.added) {
      await ankiConnect('updateNoteFields', { note: { id: result.ankiNoteId, fields: { [LAPIS_FIELDS.sentences]: result.sentencesHtml } } });
    }
    return result;
  }

  /** Creates the note in Anki through AnkiConnect; resolves to the new note id. */
  async function addCard(item: DeckIPlusOneSentence): Promise<number> {
    const { sentence, word } = item;
    const mapping = settings.value.fieldsByModel[settings.value.modelName] ?? {};
    const wanted = new Set(Object.values(mapping));
    const fieldsFor = (source: MiningFieldSource) => Object.keys(mapping).filter((name) => mapping[name] === source);

    const [details, media, english] = await Promise.all([
      wanted.has('definitions') || wanted.has('mainDefinition') ? $api<Word>(`vocabulary/${word.wordId}/${word.readingIndex}`) : null,
      cardMedia(sentence.sentenceId, wanted.has('sentenceAudio'), wanted.has('picture')),
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
    if (media.audio) note.audio = [{ data: await blobToBase64(media.audio.blob), filename: media.audio.filename, fields: fieldsFor('sentenceAudio') }];
    if (media.picture) note.picture = [{ data: await blobToBase64(media.picture.blob), filename: media.picture.filename, fields: fieldsFor('picture') }];

    return ankiConnect<number>('addNote', { note });
  }

  return { settings: readonly(settings), configured, saveSettings, mappingFor, fetchAudio, fetchPicture, addCard, existingNotes, addToExistingNote };
}
