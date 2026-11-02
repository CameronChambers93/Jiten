import type { DeckIPlusOneSentence, ExampleSentence, Word } from '~/types';
import { ankiConnect } from '~/composables/useAnkiConnect';
import { ankiPitchCategories, ankiPitchPositions } from '~/utils/pitchAccent';
import { ankiSearchValue, fieldFor, fieldsFor, setupQuery, type AnkiNoteTypeSetup, type FieldRole } from '~/utils/ankiSetups';
import { mainFieldValues, noteWriteFields, type MainFields, type NoteWrite } from '~/utils/ankiNoteWrite';
import { DEFAULT_EMPTY_FIELDS, emptyFieldWarnings, failureReason, MineDenied, rolesWith, type EmptyReasons } from '~/utils/miningWarnings';
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

/** What mining a sentence did, with a line for each field it left empty that the card settings warn about. */
export interface MinedResult {
  ankiNoteId: number;
  /** False when the word already had the sentence. */
  added: boolean;
  sentences: number;
  warnings: string[];
}

/** A sentence as both ways of mining send it to `api/mined`: the word in bold, before the target template. */
interface BuiltSentence {
  body: {
    exampleSentenceId: number;
    sentence: string;
    furigana: string | null;
    english: string | null;
    englishOrigin: string | null;
    sourceTitle: string | null;
    audioFilename: string | null;
    pictureFilename: string | null;
  };
  reasons: EmptyReasons;
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
  const { forCardTranslation, loadOptions: loadTranslationOptions } = useSentenceTranslation();
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
   * there is no screenshot. A part that fails is left out, with the reason.
   */
  async function cardMedia(
    sentenceId: number,
    wantAudio: boolean,
    wantPicture: boolean
  ): Promise<{ audio: CardFile | null; picture: CardFile | null; reasons: EmptyReasons }> {
    const reasons: EmptyReasons = {};
    const audio = async (): Promise<CardFile | null> => {
      try {
        return { blob: await fetchAudio(sentenceId), filename: `jiten_${sentenceId}.mp3` };
      } catch (e) {
        if (!isNotFound(e)) {
          reasons.sentenceAudio = failureReason('Cutting the audio', e);
          return null;
        }
      }
      try {
        const voice = store.ttsVoice === 'random' ? 'female' : store.ttsVoice;
        const blob = await $api<Blob>(`tts/sentence/${sentenceId}`, { responseType: 'blob', query: { voice } });
        return { blob, filename: `jiten_${sentenceId}.ogg` };
      } catch (e) {
        reasons.sentenceAudio = failureReason('Text-to-speech', e);
        return null;
      }
    };
    const picture = async (): Promise<CardFile | null> => {
      try {
        return { blob: await fetchPicture(sentenceId), filename: `jiten_${sentenceId}.jpg` };
      } catch (e) {
        reasons.picture = isNotFound(e) ? 'this title has no linked video' : failureReason('Cutting the screenshot', e);
        return null;
      }
    };
    const [a, p] = await Promise.all([wantAudio ? audio() : null, wantPicture ? picture() : null]);
    return { audio: a, picture: p, reasons };
  }

  /** The English a card gets, with why there's none: null and no reason when card English is off. */
  async function cardEnglish(sentenceId: number): Promise<{ text: string | null; origin: string | null; reason?: string }> {
    try {
      const translation = await forCardTranslation(sentenceId);
      if (translation?.text) return { text: escapeFieldText(translation.text), origin: translation.origin };
      const mode = (await loadTranslationOptions())?.cardMode;
      return mode === 'off' || !mode ? { text: null, origin: null } : { text: null, origin: null, reason: 'no subtitles or AI translation for this line' };
    } catch (e) {
      // A failed translation leaves the sentence without English rather than losing it.
      return { text: null, origin: null, reason: failureReason('Translation', e) };
    }
  }

  /**
   * The sentence as both ways of mining store it, its media already in Anki's collection: one builder, so a new note
   * and an existing one get the same text.
   */
  async function buildSentence(sentence: ExampleSentence, want: { audio: boolean; picture: boolean; english: boolean }): Promise<BuiltSentence> {
    const [media, english] = await Promise.all([
      cardMedia(sentence.sentenceId, want.audio, want.picture),
      want.english
        ? cardEnglish(sentence.sentenceId)
        : Promise.resolve({ text: null, origin: null } as { text: string | null; origin: string | null; reason?: string }),
    ]);
    const reasons: EmptyReasons = { ...media.reasons };
    if (english.reason) reasons.sentenceEnglish = english.reason;
    for (const [role, file] of [
      ['sentenceAudio', media.audio],
      ['picture', media.picture],
    ] as const) {
      if (!file) continue;
      try {
        await ankiConnect('storeMediaFile', { filename: file.filename, data: await blobToBase64(file.blob) });
      } catch (e) {
        if (e instanceof TypeError) throw e;
        reasons[role] = failureReason('Storing the file in Anki', e);
        if (role === 'sentenceAudio') media.audio = null;
        else media.picture = null;
      }
    }
    return {
      body: {
        exampleSentenceId: sentence.sentenceId,
        sentence: boldTarget(sentence),
        furigana: sentence.furigana?.length ? ankiSentenceFurigana(sentence.text, sentence.furigana, sentence) : null,
        english: english.text,
        englishOrigin: english.origin,
        sourceTitle: sourceLabel(sentence) || null,
        audioFilename: media.audio?.filename ?? null,
        pictureFilename: media.picture?.filename ?? null,
      },
      reasons,
    };
  }

  /** The roles reported after the writes: those that warn, and those that deny but still ended up empty. */
  function warnRoles(): readonly FieldRole[] {
    return rolesWith(anki.cardTarget.value.emptyFields ?? DEFAULT_EMPTY_FIELDS, 'warn', 'deny');
  }

  /** The roles that stop mining when they would be empty. */
  function denyRoles(): FieldRole[] {
    return rolesWith(anki.cardTarget.value.emptyFields ?? DEFAULT_EMPTY_FIELDS, 'deny');
  }

  /** Whether the note type's setup writes a sentence list but has no field for it, or the note lacks that field. */
  function listFieldMissing(modelName: string, fields?: Record<string, unknown>): boolean {
    const setup = anki.setupFor(modelName);
    if (!setup || setup.sentenceList === 'none') return false;
    const field = fieldFor(setup.fieldRoles, 'sentenceList');
    return !field || (fields != null && !(field in fields));
  }

  /** The note's fields as Anki has them now; empty when it can't be read. */
  async function noteFields(ankiNoteId: number): Promise<Record<string, { value: string }> | null> {
    try {
      const [note] = await ankiConnect<(AnkiNoteInfo | Record<string, never>)[]>('notesInfo', { notes: [ankiNoteId] });
      return note && 'noteId' in note ? note.fields : null;
    } catch {
      return null;
    }
  }

  /** A missing sentence list field, as a warning: the note type's name and the field Sync writes to. */
  function missingListWarning(modelName: string): string {
    const field = fieldFor(anki.setupFor(modelName)?.fieldRoles ?? {}, 'sentenceList');
    return field
      ? `${field}: ${modelName} has no such field, so the sentence list wasn't written. Sync on Mined words can add it.`
      : `Sentence list: ${modelName} has no field for it in Anki settings, so the sentence list wasn't written.`;
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
   * hold the value. Records main fields written, so they aren't written again. `fields` is the note's fields after the
   * write; null when the note couldn't be read.
   */
  async function writeNote(
    write: NoteWrite,
    info?: AnkiNoteInfo,
    record = true
  ): Promise<{ updated: boolean; missingList: boolean; mainWritten: MainWritten | null; fields: Record<string, { value: string }> | null }> {
    const none = { updated: false, missingList: false, mainWritten: null, fields: null };
    const setup = anki.setupFor(write.modelName);
    if (!setup) return none;
    const note = info ?? (await ankiConnect<(AnkiNoteInfo | Record<string, never>)[]>('notesInfo', { notes: [write.ankiNoteId] }))[0];
    if (!note || !('noteId' in note)) return none;
    const { fields, missingList } = noteWriteFields(write, setup.fieldRoles, note.fields);
    if (Object.keys(fields).length) await ankiConnect('updateNoteFields', { note: { id: write.ankiNoteId, fields } });
    const mainWritten = write.main ? { ankiNoteId: write.ankiNoteId, minedSentenceId: write.main.minedSentenceId, hash: write.main.hash } : null;
    if (mainWritten && record) await recordMainWritten([mainWritten]);
    const after = { ...note.fields };
    for (const [name, value] of Object.entries(fields)) after[name] = { ...after[name], value };
    return { updated: Object.keys(fields).length > 0, missingList, mainWritten, fields: after };
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
  async function addToExistingNote(sentence: ExampleSentence, noteIds: number[]): Promise<MinedResult> {
    const { ankiNoteIds } = await $api<{ ankiNoteIds: number[] }>('mined/notes');
    const known = new Set(ankiNoteIds);
    const missing = noteIds.filter((id) => !known.has(id));
    if (missing.length) {
      const failed = (await importNotes(missing)).find((r) => r.status === 'failed');
      if (failed) throw new Error(failed.error ?? "Couldn't read the existing note");
    }

    const built = await buildSentence(sentence, { audio: true, picture: true, english: true });

    // Denied parts stop the sentence before the store or the note changes.
    const deny = denyRoles();
    if (deny.length) {
      const lines = emptyFieldWarnings({
        fieldRoles: {},
        fields: {},
        warn: deny,
        reasons: built.reasons,
        parts: ['sentenceEnglish', 'sentenceAudio', 'picture'],
      });
      if (deny.includes('sentenceList')) {
        const [kept] = await ankiConnect<(AnkiNoteInfo | Record<string, never>)[]>('notesInfo', { notes: [noteIds[0]] });
        if (kept && 'noteId' in kept && listFieldMissing(kept.modelName, kept.fields)) lines.push(missingListWarning(kept.modelName));
      }
      if (lines.length) throw new MineDenied(lines);
    }

    const result = await $api<AddedSentence>('mined/sentences', { method: 'POST', body: { ankiNoteId: noteIds[0], ...built.body } });
    const warnings = result.added ? await writeAndCheck(result.write, built.reasons, ['sentenceEnglish', 'sentenceAudio', 'picture']) : [];
    return { ankiNoteId: result.ankiNoteId, added: result.added, sentences: result.sentences, warnings };
  }

  /**
   * Writes the kept note and lists what was left empty: the list field, and the main fields when they were written. The
   * added sentence's English, audio and picture (`parts`) are stored with it, so their failures are listed by reason.
   */
  async function writeAndCheck(write: NoteWrite | null, reasons: EmptyReasons, parts: FieldRole[]): Promise<string[]> {
    const warn = warnRoles();
    const partsOnly = () => emptyFieldWarnings({ fieldRoles: {}, fields: {}, warn, reasons, parts });
    if (!write) return partsOnly();
    let done: Awaited<ReturnType<typeof writeNote>>;
    try {
      done = await writeNote(write);
    } catch (e) {
      // The sentence is in the store; the next Sync writes the card.
      return [`${failureReason('Writing the card', e)}; the next Sync will write it.`, ...partsOnly()];
    }
    const warnings: string[] = [];
    if (done.missingList && warn.includes('sentenceList')) warnings.push(missingListWarning(write.modelName));
    const roles: FieldRole[] = [];
    if (write.sentencesHtml != null && !done.missingList) roles.push('sentenceList');
    if (write.main) roles.push('sentence', 'sentencePlain', 'sentenceFurigana', 'sentenceEnglish', 'sentenceAudio', 'picture', 'source');
    const fieldRoles = anki.setupFor(write.modelName)?.fieldRoles ?? {};
    warnings.push(...emptyFieldWarnings({ fieldRoles, fields: done.fields, warn, reasons, roles, parts }));
    return warnings;
  }

  /**
   * Creates the note in Anki through AnkiConnect, then records it in the sentence store at once. Its sentence fields are
   * the main fields Sync would write, from the same sentence the store keeps.
   */
  async function addCard(item: DeckIPlusOneSentence): Promise<MinedResult> {
    const { sentence, word } = item;
    await anki.load();
    const mapping = settings.value.fieldRoles;
    const wanted = new Set(Object.values(mapping));
    const reasons: EmptyReasons = {};

    const [details, built, { targetWordTemplate }] = await Promise.all([
      (['definitions', 'mainDefinition', 'pitchPosition', 'pitchCategories', 'frequency'] as const).some((f) => wanted.has(f))
        ? $api<Word>(`vocabulary/${word.wordId}/${word.readingIndex}`).catch((e) => {
            for (const role of ['definitions', 'mainDefinition', 'pitchPosition', 'pitchCategories', 'frequency'] as const)
              reasons[role] = failureReason('Reading the word', e);
            return null;
          })
        : null,
      buildSentence(sentence, { audio: wanted.has('sentenceAudio'), picture: wanted.has('picture'), english: wanted.has('sentenceEnglish') }),
      $api<{ targetWordTemplate: string }>('mined/settings'),
    ]);
    Object.assign(reasons, built.reasons);

    const b = built.body;
    const main: MainFields = {
      minedSentenceId: 0,
      hash: '',
      sentence: applyTargetTemplate(b.sentence, targetWordTemplate),
      sentencePlain: sentence.text,
      furigana: b.furigana == null ? null : applyTargetTemplate(b.furigana, targetWordTemplate),
      english: b.english,
      audioFilename: b.audioFilename,
      pictureFilename: b.pictureFilename,
      source: b.sourceTitle,
    };
    const values: Partial<Record<FieldRole, string>> = {
      word: escapeFieldText(word.reading),
      wordReading: escapeFieldText(kanaReading(word.readingFurigana || word.reading)),
      wordFurigana: escapeFieldText(ankiWordFurigana(word.readingFurigana || word.reading)),
      pitchPosition: ankiPitchPositions(details?.pitchAccents),
      pitchCategories: ankiPitchCategories(word.readingFurigana || word.reading, details?.pitchAccents, details?.partsOfSpeech ?? []),
      definitions: details ? definitionsHtml(details.definitions, word.readingIndex) : '',
      mainDefinition: escapeFieldText((details && firstSense(details.definitions, word.readingIndex)) || word.mainDefinition || ''),
      frequency: wordFrequency(details, word.readingIndex),
    };
    const fields: Record<string, string> = {};
    for (const [name, role] of Object.entries(mapping)) {
      const value = values[role];
      if (value !== undefined) fields[name] = value;
    }
    Object.assign(fields, mainFieldValues(main, mapping));

    // Every field is known now, so a denied role that would be empty stops the card before Anki has it.
    const deny = denyRoles();
    if (deny.length) {
      const planned = Object.fromEntries(Object.keys(mapping).map((name) => [name, { value: fields[name] ?? '' }]));
      const lines = emptyFieldWarnings({
        fieldRoles: mapping,
        fields: planned,
        warn: deny,
        reasons,
        roles: [...new Set(Object.values(mapping))].filter((r) => r !== 'sentenceList'),
      });
      if (deny.includes('sentenceList') && listFieldMissing(settings.value.modelName)) lines.push(missingListWarning(settings.value.modelName));
      if (lines.length) throw new MineDenied(lines);
    }

    const ankiNoteId = await ankiConnect<number>('addNote', {
      note: {
        deckName: settings.value.deckName,
        modelName: settings.value.modelName,
        fields,
        tags: settings.value.tags.split(/\s+/).filter(Boolean),
        options: { allowDuplicate: false, duplicateScope: 'deck' },
      },
    });

    let recorded: AddedSentence | null = null;
    let recordWarning: string | null = null;
    try {
      recorded = await $api<AddedSentence>('mined/notes/created', {
        method: 'POST',
        body: {
          ankiNoteId,
          modelName: settings.value.modelName,
          deckName: settings.value.deckName,
          wordId: word.wordId,
          readingIndex: word.readingIndex,
          expression: plainFieldText(fields[fieldFor(mapping, 'word') ?? ''] ?? '') || word.reading,
          reading: kanaReading(word.readingFurigana || word.reading),
          ...b,
        },
      });
    } catch (e) {
      recordWarning = `${failureReason('Recording the card in Jiten', e)}; the next Sync will pick it up.`;
    }

    const warnings: string[] = [];
    if (recordWarning) warnings.push(recordWarning);
    // The list field is only this note's to fill when it's the word's kept note; an older note gets the list instead.
    let listWritten = false;
    let current: Record<string, { value: string }> | null = null;
    if (recorded?.write) {
      const write = recorded.write;
      try {
        // The server has recorded the main fields already.
        const done = await writeNote(write, undefined, false);
        if (done.missingList && warnRoles().includes('sentenceList')) warnings.push(missingListWarning(write.modelName));
        listWritten = write.ankiNoteId === ankiNoteId && write.sentencesHtml != null && !done.missingList;
        if (write.ankiNoteId === ankiNoteId) current = done.fields;
      } catch (e) {
        // The card exists in Anki and in the store; the next Sync writes what's missing.
        warnings.push(`${failureReason('Writing the sentence list', e)}; the next Sync will write it.`);
      }
    }
    current ??= await noteFields(ankiNoteId);
    const roles = [...new Set(Object.values(mapping))].filter((r) => r !== 'sentenceList' || listWritten);
    warnings.push(...emptyFieldWarnings({ fieldRoles: mapping, fields: current, warn: warnRoles(), reasons, roles }));
    return { ankiNoteId, added: true, sentences: recorded?.sentences ?? 1, warnings };
  }

  return { settings, configured, fetchAudio, fetchPicture, addCard, existingNotes, wordsInAnki, writeNote, recordMainWritten, addToExistingNote };
}
