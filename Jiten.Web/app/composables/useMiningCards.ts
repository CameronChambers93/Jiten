import type { DeckIPlusOneSentence, ExampleSentence, Word } from '~/types';
import { ankiConnect } from '~/composables/useAnkiConnect';
import { ankiPitchCategories, ankiPitchPositions } from '~/utils/pitchAccent';
import {
  ankiSearchValue,
  fieldFor,
  fieldsFor,
  miningOf,
  PART_ROLES,
  partsOffRoles,
  pickTarget,
  setupQuery,
  type AnkiNoteTypeSetup,
  type CardTargets,
  type FieldRole,
  type MiningParts,
  type MiningSettings,
  type NewCardTarget,
} from '~/utils/ankiSetups';
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
  /** For a new card, the target it went to. */
  target?: NewCardTarget;
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
    videoFilename: string | null;
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

  const targets = computed(() => anki.cardTargets.value as CardTargets);

  /** The audio padding every target shares. */
  const settings = computed(() => ({ padStartMs: targets.value.padStartMs, padEndMs: targets.value.padEndMs }));

  function rolesOf(modelName: string): Record<string, FieldRole> {
    return (anki.setupFor(modelName)?.fieldRoles ?? {}) as Record<string, FieldRole>;
  }

  function targetReady(t: NewCardTarget): boolean {
    return !!t.deckName && !!t.modelName && Object.keys(rolesOf(t.modelName)).length > 0;
  }

  /** The last target, which takes every line the others don't, has a deck, a note type and field roles. */
  const configured = computed(() => {
    const list = targets.value.targets;
    return list.length > 0 && targetReady(list[list.length - 1]!);
  });

  /** The target a new card for the line goes to. */
  function targetFor(sentence: ExampleSentence): NewCardTarget {
    return pickTarget(targets.value, sentence);
  }

  /** The note type's mining settings, with the defaults. */
  function miningFor(modelName: string): MiningSettings {
    return miningOf(anki.setupFor(modelName));
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

  /** The clip with sound, padded like the audio. */
  function fetchVideo(sentenceId: number): Promise<Blob> {
    return $api<Blob>(`sentence-media/${sentenceId}/video`, {
      responseType: 'blob',
      query: { padStartMs: settings.value.padStartMs, padEndMs: settings.value.padEndMs },
    });
  }

  /**
   * The sentence's audio and screenshot, cut from its episode. Without a linked video the audio is text-to-speech and
   * there is no screenshot. A part that fails is left out, with the reason.
   */
  async function cardMedia(
    sentenceId: number,
    wantAudio: boolean,
    wantPicture: boolean,
    wantVideo: boolean
  ): Promise<{ audio: CardFile | null; picture: CardFile | null; video: CardFile | null; reasons: EmptyReasons }> {
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
    const video = async (): Promise<CardFile | null> => {
      try {
        return { blob: await fetchVideo(sentenceId), filename: `jiten_${sentenceId}.webm` };
      } catch (e) {
        reasons.video = isNotFound(e) ? 'this title has no linked video' : failureReason('Cutting the video clip', e);
        return null;
      }
    };
    const [a, p, v] = await Promise.all([wantAudio ? audio() : null, wantPicture ? picture() : null, wantVideo ? video() : null]);
    return { audio: a, picture: p, video: v, reasons };
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
  async function buildSentence(
    sentence: ExampleSentence,
    want: { audio: boolean; picture: boolean; english: boolean; video: boolean }
  ): Promise<BuiltSentence> {
    const [media, english] = await Promise.all([
      cardMedia(sentence.sentenceId, want.audio, want.picture, want.video),
      want.english
        ? cardEnglish(sentence.sentenceId)
        : Promise.resolve({ text: null, origin: null } as { text: string | null; origin: string | null; reason?: string }),
    ]);
    const reasons: EmptyReasons = { ...media.reasons };
    if (english.reason) reasons.sentenceEnglish = english.reason;
    for (const [role, file] of [
      ['sentenceAudio', media.audio],
      ['picture', media.picture],
      ['video', media.video],
    ] as const) {
      if (!file) continue;
      try {
        await ankiConnect('storeMediaFile', { filename: file.filename, data: await blobToBase64(file.blob) });
      } catch (e) {
        if (e instanceof TypeError) throw e;
        reasons[role] = failureReason('Storing the file in Anki', e);
        if (role === 'sentenceAudio') media.audio = null;
        else if (role === 'picture') media.picture = null;
        else media.video = null;
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
        videoFilename: media.video?.filename ?? null,
      },
      reasons,
    };
  }

  /**
   * Whether mining cuts the clip for a note type: its video part is on, and something shows it, a field with the Video
   * role or the rotation script's sentence list, from v2 (which plays it in the picture slot; v1 can't show it).
   */
  function wantsVideo(modelName: string): boolean {
    if (!miningFor(modelName).parts.video) return false;
    const setup = anki.setupFor(modelName);
    return !!setup && (!!fieldFor(setup.fieldRoles, 'video') || (setup.sentenceList === 'script' && (setup.scriptVersion ?? 0) >= 2));
  }

  /** The roles of the parts built for a stored sentence, whose failures are listed by reason. */
  function builtPartRoles(parts: MiningParts): FieldRole[] {
    return (['english', 'audio', 'picture', 'video'] as const).filter((p) => parts[p]).map((p) => PART_ROLES[p]);
  }

  /**
   * The note type's roles reported after the writes: those that warn, and those that deny but still ended up empty.
   * Parts the note type turns off aren't reported.
   */
  function warnRoles(modelName: string): readonly FieldRole[] {
    const m = miningFor(modelName);
    const off = partsOffRoles(m.parts);
    return rolesWith(m.emptyFields ?? DEFAULT_EMPTY_FIELDS, 'warn', 'deny').filter((r) => !off.includes(r));
  }

  /** The note type's roles that stop mining when they would be empty. */
  function denyRoles(modelName: string): FieldRole[] {
    const m = miningFor(modelName);
    const off = partsOffRoles(m.parts);
    return rolesWith(m.emptyFields ?? DEFAULT_EMPTY_FIELDS, 'deny').filter((r) => !off.includes(r));
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

  /** The word's kept note (its oldest, `noteIds[0]`), with its note type and fields; null when it can't be read. */
  async function keptNote(noteIds: number[]): Promise<AnkiNoteInfo | null> {
    if (!noteIds.length) return null;
    const [kept] = await ankiConnect<(AnkiNoteInfo | Record<string, never>)[]>('notesInfo', { notes: [noteIds[0]] });
    return kept && 'noteId' in kept ? (kept as AnkiNoteInfo) : null;
  }

  /**
   * Adds the sentence to the word's existing note instead of creating another: it joins the word's sentences in the
   * store, its media goes into Anki's collection, and the kept note's Sentences field is rewritten. Notes not in the
   * store yet (a Yomitan card never synced) are imported first, so their own sentence stays on the card, first in the
   * rotation. The kept note's note type decides the parts built and the empty-field policies.
   */
  async function addToExistingNote(sentence: ExampleSentence, noteIds: number[], keptInfo?: AnkiNoteInfo | null): Promise<MinedResult> {
    const kept = keptInfo !== undefined ? keptInfo : await keptNote(noteIds);
    const modelName = kept?.modelName ?? '';
    const { parts } = miningFor(modelName);
    const { ankiNoteIds, withoutSentence } = await $api<{ ankiNoteIds: number[]; withoutSentence?: number[] }>('mined/notes');
    const known = new Set(ankiNoteIds);
    const missing = noteIds.filter((id) => !known.has(id));
    // A kept note stored without its own sentence (synced for its list before its note type mapped one) gets it now, so
    // it stays first in the rotation.
    const keptSetup = anki.setupFor(modelName);
    if (
      known.has(noteIds[0]!) &&
      withoutSentence?.includes(noteIds[0]!) &&
      keptSetup &&
      Object.values(keptSetup.fieldRoles).some((r) => r === 'sentence' || r === 'sentencePlain' || r === 'sentenceFurigana')
    )
      missing.unshift(noteIds[0]!);
    if (missing.length) {
      const failed = (await importNotes(missing)).find((r) => r.status === 'failed');
      if (failed) throw new Error(failed.error ?? "Couldn't read the existing note");
    }

    const built = await buildSentence(sentence, { ...parts, video: wantsVideo(modelName) });
    const builtParts = builtPartRoles({ ...parts, video: wantsVideo(modelName) });

    // Denied parts stop the sentence before the store or the note changes.
    const deny = denyRoles(modelName);
    if (deny.length) {
      const lines = emptyFieldWarnings({ fieldRoles: {}, fields: {}, warn: deny, reasons: built.reasons, parts: builtParts });
      if (deny.includes('sentenceList') && kept && listFieldMissing(kept.modelName, kept.fields)) lines.push(missingListWarning(kept.modelName));
      if (lines.length) throw new MineDenied(lines);
    }

    const result = await $api<AddedSentence>('mined/sentences', { method: 'POST', body: { ankiNoteId: noteIds[0], ...built.body } });
    const warnings = result.added ? await writeAndCheck(result.write, built.reasons, builtParts, modelName) : [];
    return { ankiNoteId: result.ankiNoteId, added: result.added, sentences: result.sentences, warnings };
  }

  /**
   * Writes the kept note and lists what was left empty: the list field, and the main fields when they were written. The
   * added sentence's English, audio and picture (`parts`) are stored with it, so their failures are listed by reason.
   */
  async function writeAndCheck(write: NoteWrite | null, reasons: EmptyReasons, parts: FieldRole[], modelName: string): Promise<string[]> {
    const warn = warnRoles(write?.modelName ?? modelName);
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
    if (write.main) roles.push('sentence', 'sentencePlain', 'sentenceFurigana', 'sentenceEnglish', 'sentenceAudio', 'picture', 'video', 'source');
    const fieldRoles = anki.setupFor(write.modelName)?.fieldRoles ?? {};
    warnings.push(...emptyFieldWarnings({ fieldRoles, fields: done.fields, warn, reasons, roles, parts }));
    return warnings;
  }

  /**
   * Creates the note in Anki through AnkiConnect, then records it in the sentence store at once. Its sentence fields are
   * the main fields Sync would write, from the same sentence the store keeps. The note goes to the target picked for the
   * line, and its note type decides the parts built and the empty-field policies. `keptModelName` is the note type of the
   * word's older note when it has one ("Create a new note"): that note gets the sentence list, so its note type decides
   * whether a missing list field is denied.
   */
  async function addCard(item: DeckIPlusOneSentence, keptModelName?: string | null): Promise<MinedResult> {
    const { sentence, word } = item;
    await anki.load();
    const target = targetFor(sentence);
    if (!targetReady(target))
      throw new Error(`The card target for this line (${target.deckName || 'no deck'}, ${target.modelName || 'no note type'}) isn't set up. Check Card settings.`);
    const mapping = rolesOf(target.modelName);
    const { parts } = miningFor(target.modelName);
    const off = partsOffRoles(parts);
    const wanted = new Set(Object.values(mapping).filter((r) => !off.includes(r)));
    // The stored sentence also joins an older kept note's list, so it gets the parts that note type builds too.
    const keptParts = keptModelName ? miningFor(keptModelName).parts : null;
    const reasons: EmptyReasons = {};

    const [details, built, { targetWordTemplate }] = await Promise.all([
      (['definitions', 'mainDefinition', 'pitchPosition', 'pitchCategories', 'frequency'] as const).some((f) => wanted.has(f))
        ? $api<Word>(`vocabulary/${word.wordId}/${word.readingIndex}`).catch((e) => {
            for (const role of ['definitions', 'mainDefinition', 'pitchPosition', 'pitchCategories', 'frequency'] as const)
              reasons[role] = failureReason('Reading the word', e);
            return null;
          })
        : null,
      buildSentence(sentence, {
        audio: wanted.has('sentenceAudio') || !!keptParts?.audio,
        picture: wanted.has('picture') || !!keptParts?.picture,
        english: wanted.has('sentenceEnglish') || !!keptParts?.english,
        video: wantsVideo(target.modelName) || (!!keptModelName && wantsVideo(keptModelName)),
      }),
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
      // A part this note type turns off stays off its own fields, though it was built for the kept note's list.
      english: parts.english ? b.english : null,
      audioFilename: parts.audio ? b.audioFilename : null,
      pictureFilename: parts.picture ? b.pictureFilename : null,
      videoFilename: parts.video ? b.videoFilename : null,
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
    const deny = denyRoles(target.modelName);
    const listModel = keptModelName || target.modelName;
    const denyList = denyRoles(listModel).includes('sentenceList');
    if (deny.length || denyList) {
      const planned = Object.fromEntries(Object.keys(mapping).map((name) => [name, { value: fields[name] ?? '' }]));
      const lines = emptyFieldWarnings({ fieldRoles: mapping, fields: planned, warn: deny, reasons, roles: [...wanted].filter((r) => r !== 'sentenceList') });
      if (denyList && listFieldMissing(listModel)) lines.push(missingListWarning(listModel));
      if (lines.length) throw new MineDenied(lines);
    }

    const ankiNoteId = await ankiConnect<number>('addNote', {
      note: {
        deckName: target.deckName,
        modelName: target.modelName,
        fields,
        tags: target.tags.split(/\s+/).filter(Boolean),
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
          modelName: target.modelName,
          deckName: target.deckName,
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
        if (done.missingList && warnRoles(write.modelName).includes('sentenceList')) warnings.push(missingListWarning(write.modelName));
        listWritten = write.ankiNoteId === ankiNoteId && write.sentencesHtml != null && !done.missingList;
        if (write.ankiNoteId === ankiNoteId) current = done.fields;
      } catch (e) {
        // The card exists in Anki and in the store; the next Sync writes what's missing.
        warnings.push(`${failureReason('Writing the sentence list', e)}; the next Sync will write it.`);
      }
    }
    current ??= await noteFields(ankiNoteId);
    const roles = [...wanted].filter((r) => r !== 'sentenceList' || listWritten);
    warnings.push(...emptyFieldWarnings({ fieldRoles: mapping, fields: current, warn: warnRoles(target.modelName), reasons, roles }));
    return { ankiNoteId, added: true, sentences: recorded?.sentences ?? 1, warnings, target };
  }

  return {
    settings,
    configured,
    fetchAudio,
    fetchPicture,
    fetchVideo,
    addCard,
    existingNotes,
    keptNote,
    miningFor,
    targetFor,
    wordsInAnki,
    writeNote,
    recordMainWritten,
    addToExistingNote,
    rebuildMovedSentences,
  };

  /**
   * Rebuilds the text, furigana and English of mined sentences whose line changed (a re-ingest as whole-cue lines), with
   * the same code as a new card; their audio in Anki is already the whole cue. One that fails is left for the next Sync;
   * a lost connection stops the Sync, as everywhere else.
   */
  async function rebuildMovedSentences(): Promise<{ rebuilt: number; failed: number }> {
    const pending = await $api<{ minedSentenceId: number; sentence: ExampleSentence }[]>('mined/rebuild');
    let rebuilt = 0;
    let failed = 0;
    for (const s of pending) {
      try {
        const { body } = await buildSentence(s.sentence, { audio: false, picture: false, english: true });
        await $api(`mined/sentences/${s.minedSentenceId}/rebuild`, {
          method: 'POST',
          body: { sentence: body.sentence, furigana: body.furigana, english: body.english, englishOrigin: body.englishOrigin },
        });
        rebuilt++;
      } catch (e) {
        if (e instanceof TypeError) throw e;
        failed++;
      }
    }
    return { rebuilt, failed };
  }
}
