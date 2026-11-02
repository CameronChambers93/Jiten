import type { DeckIPlusOneSentence, ExampleSentence, Word } from '~/types';
import { ankiConnect } from '~/composables/useAnkiConnect';
import { ankiPitchCategories, ankiPitchPositions } from '~/utils/pitchAccent';
import { ankiSearchValue, fieldsFor, setupQuery, type AnkiNoteTypeSetup, type FieldRole } from '~/utils/ankiSetups';
import { noteWriteFields, type NoteWrite } from '~/utils/ankiNoteWrite';
import type { AnkiNoteInfo } from '~/utils/minedImport';
import {
  ankiSentenceFurigana,
  ankiWordFurigana,
  boldTarget,
  definitionsHtml,
  escapeFieldText,
  firstSense,
  kanaReading,
  sourceLabel,
  applyTargetTemplate,
} from '~/utils/miningCardFields';

function isNotFound(e: unknown): boolean {
  const err = e as { statusCode?: number; response?: { status?: number } } | null;
  return err?.statusCode === 404 || err?.response?.status === 404;
}

/** Plain text of a word field: tags, furigana brackets and spaces gone. */
export function plainFieldText(html: string): string {
  return html
    .replace(/<rt>.*?<\/rt>/g, '')
    .replace(/<[^>]*>/g, '')
    .replace(/\[[^\]]*\]/g, '')
    .replace(/&nbsp;/g, ' ')
    .replace(/\s+/g, '')
    .trim();
}

/** Main fields a note was given, as `api/mined/sync/main-written` records them. */
export interface MainWritten {
  ankiNoteId: number;
  minedSentenceId: number;
  hash: string;
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
  /** What to write to the kept note now; null when its note type has nothing to write. */
  write: NoteWrite | null;
}

/** The word's frequency rank for a sort field, or empty when Jiten has none. */
function wordFrequency(details: Word | null, readingIndex: number): string {
  const rank = details && [details.mainReading, ...details.alternativeReadings].find((r) => r.readingIndex === readingIndex)?.frequencyRank;
  return rank && rank > 0 ? String(rank) : '';
}

async function blobToBase64(blob: Blob): Promise<string> {
  const bytes = new Uint8Array(await blob.arrayBuffer());
  let binary = '';
  for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(binary);
}

export function useMiningCards() {
  const { $api } = useNuxtApp();
  const { forCard: englishForCard, forCardTranslation } = useSentenceTranslation();
  const { importNotes } = useMinedImport();
  const store = useJitenStore();
  const anki = useAnkiSetups();
  if (import.meta.client) anki.load().catch(() => {});

  /** The new-card target, with the target note type's roles. */
  const settings = computed(() => {
    const target = anki.cardTarget.value;
    return { ...target, fieldRoles: (anki.setupFor(target.modelName)?.fieldRoles ?? {}) as Record<string, FieldRole> };
  });

  const configured = computed(() => !!settings.value.deckName && !!settings.value.modelName && Object.keys(settings.value.fieldRoles).length > 0);

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

  /** The setups whose notes count as the word being in Anki: those with sentence actions and a Word field. */
  function wordSetups(): AnkiNoteTypeSetup[] {
    return anki.miningSetups.value.filter((s) => fieldsFor(s.fieldRoles, 'word').length > 0);
  }

  /**
   * The word's notes in Anki, oldest first: notes of every note type with sentence actions (within its filter) whose
   * Word field holds it, the check "Hide words already in Anki" makes.
   */
  async function existingNotes(word: string): Promise<number[]> {
    await anki.load();
    const setups = wordSetups();
    if (!setups.length || !word) return [];
    const query = setups
      .map((s) => {
        const fields = fieldsFor(s.fieldRoles, 'word')
          .map((f) => `"${ankiSearchValue(f)}:${ankiSearchValue(word)}"`)
          .join(' OR ');
        return `(${setupQuery(s, `(${fields})`)})`;
      })
      .join(' OR ');
    const ids = await ankiConnect<number[]>('findNotes', { query });
    return ids.sort((a, b) => a - b);
  }

  /** Every word on the notes of note types with sentence actions, as plain text. */
  async function wordsInAnki(): Promise<string[]> {
    await anki.load();
    const words = new Set<string>();
    for (const setup of wordSetups()) {
      const wordFields = fieldsFor(setup.fieldRoles, 'word');
      const noteIds = await ankiConnect<number[]>('findNotes', { query: setupQuery(setup) });
      for (let i = 0; i < noteIds.length; i += 1000) {
        const notes = await ankiConnect<{ fields: Record<string, { value: string }> }[]>('notesInfo', { notes: noteIds.slice(i, i + 1000) });
        for (const note of notes) {
          for (const f of wordFields) {
            const w = plainFieldText(note.fields[f]?.value ?? '');
            if (w) words.add(w);
          }
        }
      }
    }
    return [...words];
  }

  /**
   * Writes Sync's sentence list and main fields to a note through its note type's roles, skipping fields that already
   * hold the value. Records main fields written, so they aren't written again.
   */
  async function writeNote(
    write: NoteWrite,
    info?: AnkiNoteInfo,
    record = true
  ): Promise<{ updated: boolean; missingList: boolean; mainWritten: MainWritten | null }> {
    const none = { updated: false, missingList: false, mainWritten: null };
    const setup = anki.setupFor(write.modelName);
    if (!setup) return none;
    const note = info ?? (await ankiConnect<(AnkiNoteInfo | Record<string, never>)[]>('notesInfo', { notes: [write.ankiNoteId] }))[0];
    if (!note || !('noteId' in note)) return none;
    const { fields, missingList } = noteWriteFields(write, setup.fieldRoles, note.fields);
    if (Object.keys(fields).length) await ankiConnect('updateNoteFields', { note: { id: write.ankiNoteId, fields } });
    const mainWritten = write.main ? { ankiNoteId: write.ankiNoteId, minedSentenceId: write.main.minedSentenceId, hash: write.main.hash } : null;
    if (mainWritten && record) await recordMainWritten([mainWritten]);
    return { updated: Object.keys(fields).length > 0, missingList, mainWritten };
  }

  /** Records main fields written, so they aren't written again. */
  async function recordMainWritten(notes: MainWritten[]) {
    if (notes.length) await $api('mined/sync/main-written', { method: 'POST', body: { notes } });
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
        furigana: sentence.furigana?.length ? ankiSentenceFurigana(sentence.text, sentence.furigana, sentence) : null,
        english: translation?.text ? escapeFieldText(translation.text) : null,
        englishOrigin: translation?.origin ?? null,
        sourceTitle: sourceLabel(sentence) || null,
        audioFilename: media.audio?.filename ?? null,
        pictureFilename: media.picture?.filename ?? null,
      },
    });
    if (result.write) await writeNote(result.write);
    return result;
  }

  /** Creates the note in Anki through AnkiConnect; resolves to the new note id. */
  async function addCard(item: DeckIPlusOneSentence): Promise<number> {
    const { sentence, word } = item;
    await anki.load();
    const mapping = settings.value.fieldRoles;
    const wanted = new Set(Object.values(mapping));
    const fieldsWith = (role: FieldRole) => fieldsFor(mapping, role);

    const [details, media, english, { targetWordTemplate }] = await Promise.all([
      (['definitions', 'mainDefinition', 'pitchPosition', 'pitchCategories', 'frequency'] as const).some((f) => wanted.has(f))
        ? $api<Word>(`vocabulary/${word.wordId}/${word.readingIndex}`)
        : null,
      cardMedia(sentence.sentenceId, wanted.has('sentenceAudio'), wanted.has('picture')),
      // A failed translation leaves the field empty rather than losing the card.
      wanted.has('sentenceEnglish') ? englishForCard(sentence.sentenceId).catch(() => '') : '',
      $api<{ targetWordTemplate: string }>('mined/settings'),
    ]);

    const values: Partial<Record<FieldRole, string>> = {
      word: escapeFieldText(word.reading),
      wordReading: escapeFieldText(kanaReading(word.readingFurigana || word.reading)),
      wordFurigana: escapeFieldText(ankiWordFurigana(word.readingFurigana || word.reading)),
      pitchPosition: ankiPitchPositions(details?.pitchAccents),
      pitchCategories: ankiPitchCategories(word.readingFurigana || word.reading, details?.pitchAccents, details?.partsOfSpeech ?? []),
      definitions: details ? definitionsHtml(details.definitions, word.readingIndex) : '',
      mainDefinition: escapeFieldText((details && firstSense(details.definitions, word.readingIndex)) || word.mainDefinition || ''),
      sentence: applyTargetTemplate(boldTarget(sentence), targetWordTemplate),
      sentencePlain: escapeFieldText(sentence.text),
      sentenceFurigana: applyTargetTemplate(ankiSentenceFurigana(sentence.text, sentence.furigana ?? [], sentence), targetWordTemplate),
      sentenceEnglish: escapeFieldText(english),
      source: escapeFieldText(sourceLabel(sentence)),
      frequency: wordFrequency(details, word.readingIndex),
    };

    const fields: Record<string, string> = {};
    for (const [name, source] of Object.entries(mapping)) {
      const value = values[source];
      if (value !== undefined) fields[name] = value;
    }

    const note: Record<string, unknown> = {
      deckName: settings.value.deckName,
      modelName: settings.value.modelName,
      fields,
      tags: settings.value.tags.split(/\s+/).filter(Boolean),
      options: { allowDuplicate: false, duplicateScope: 'deck' },
    };
    if (media.audio) note.audio = [{ data: await blobToBase64(media.audio.blob), filename: media.audio.filename, fields: fieldsWith('sentenceAudio') }];
    if (media.picture) note.picture = [{ data: await blobToBase64(media.picture.blob), filename: media.picture.filename, fields: fieldsWith('picture') }];

    return ankiConnect<number>('addNote', { note });
  }

  return { settings, configured, fetchAudio, fetchPicture, addCard, existingNotes, wordsInAnki, writeNote, recordMainWritten, addToExistingNote };
}
