import type { DeckIPlusOneSentence, ExampleSentence, Word, WordSummary } from '~/types';
import { ankiConnect } from '~/composables/useAnkiConnect';
import { ankiPitchCategories, ankiPitchPositions } from '~/utils/pitchAccent';
import {
  ankiSearchValue,
  autoSentencesOf,
  fieldFor,
  fieldsFor,
  miningOf,
  PART_ROLES,
  partsOffRoles,
  setupQuery,
  type AnkiNoteTypeSetup,
  type FieldRole,
  type MiningParts,
  type MiningSettings,
  type NewCardTarget,
} from '~/utils/ankiSetups';
import type { BuildChoice, PipelineEnglish } from '~/utils/pipelines';
import { mainFieldValues, noteWriteFields, type MainFields, type NoteWrite } from '~/utils/ankiNoteWrite';
import { DEFAULT_EMPTY_FIELDS, emptyFieldWarnings, failureReason, MineDenied, rolesWith, TargetNotReady, type EmptyReasons } from '~/utils/miningWarnings';
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

/** The server's limit on the spelling matches sent to `mined/word-notes`. */
const MAX_WORD_NOTE_CANDIDATES = 200;

/** A note of the word's spelling that the store has on another dictionary entry, or on none (`wordId` null). */
export interface OtherEntryNote {
  ankiNoteId: number;
  modelName: string;
  wordId: number | null;
  readingIndex: number | null;
  expression: string;
  /** The entry's first meaning; null for a note on no entry. */
  meaning: string | null;
  /** How the note was matched (`MinedWordMatch`): "guessed" is worth a second look. */
  wordMatch: string | null;
}

interface WordNotesResponse {
  own: { ankiNoteId: number; modelName: string }[];
  others: OtherEntryNote[];
  unstored: number[];
}

/** A word's notes in Anki: on its own entry, and of its spelling on other entries (GDR-061). */
export interface WordNotes {
  own: number[];
  others: OtherEntryNote[];
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
  const { forCardWith, loadOptions: loadTranslationOptions } = useSentenceTranslation();
  const { importNotes } = useMinedImport();
  const store = useJitenStore();
  const anki = useAnkiSetups();
  const pipelines = usePipelines();
  if (import.meta.client) {
    anki.load().catch(() => {});
    pipelines.load().catch(() => {});
  }

  /** The padding for listening to a line on a page; a card's comes from its pipeline's Build step. */
  const settings = pipelines.previewPadding;

  function rolesOf(modelName: string): Record<string, FieldRole> {
    return (anki.setupFor(modelName)?.fieldRoles ?? {}) as Record<string, FieldRole>;
  }

  function targetReady(t: NewCardTarget): boolean {
    return !!t.deckName && !!t.modelName && Object.keys(rolesOf(t.modelName)).length > 0;
  }

  /** Anki is set up for mining: some note type has field roles. Where a card goes is the pipeline's. */
  const configured = computed(() => anki.miningSetups.value.some((s) => Object.keys(s.fieldRoles).length > 0));

  /** The note type's mining settings, with the defaults; its parts aren't used for mining any more (the pipeline's are). */
  function miningFor(modelName: string): MiningSettings {
    return miningOf(anki.setupFor(modelName));
  }

  type Padding = { padStartMs: number; padEndMs: number };

  function fetchAudio(sentenceId: number, pad: Padding = settings.value): Promise<Blob> {
    return $api<Blob>(`sentence-media/${sentenceId}/audio`, {
      responseType: 'blob',
      query: { padStartMs: pad.padStartMs, padEndMs: pad.padEndMs },
    });
  }

  /** The screenshot; pictureKey (the sentence's, for a fixed frame) skips the day-long browser cache of another frame. */
  function fetchPicture(sentenceId: number, pictureKey = ''): Promise<Blob> {
    return $api<Blob>(`sentence-media/${sentenceId}/picture`, { responseType: 'blob', query: pictureKey ? { v: pictureKey } : undefined });
  }

  /** The clip with sound, padded like the audio (a fixed clip already holds its padding); clipKey as pictureKey above. */
  function fetchVideo(sentenceId: number, pad: Padding = settings.value, clipKey = ''): Promise<Blob> {
    return $api<Blob>(`sentence-media/${sentenceId}/video`, {
      responseType: 'blob',
      query: { padStartMs: pad.padStartMs, padEndMs: pad.padEndMs, ...(clipKey ? { v: clipKey } : {}) },
    });
  }

  /**
   * Cuts again the media of mined sentences whose line's clip or frame was fixed (or reset) since their card got it,
   * stores it in Anki under its new name and records that, so the Sentences field written next points at it. Old files
   * stay until Anki's Check Media. One that fails is left for the next Sync; a lost connection stops the Sync.
   */
  async function updateFixedMedia(): Promise<{ updated: number; failed: number }> {
    const behind = await $api<{ minedSentenceId: number; exampleSentenceId: number; audio: string | null; picture: string | null; video: string | null }[]>(
      'mined/media'
    );
    let updated = 0;
    let failed = 0;
    for (const s of behind) {
      try {
        const keyOf = (name: string | null) => name?.match(/^jiten_\d+(_[0-9a-f]{8})?\./)?.[1] ?? '';
        const parts: [string | null, () => Promise<Blob>][] = [
          [s.audio, () => fetchAudio(s.exampleSentenceId)],
          [s.picture, () => fetchPicture(s.exampleSentenceId, keyOf(s.picture))],
          [s.video, () => fetchVideo(s.exampleSentenceId, settings.value, keyOf(s.video))],
        ];
        for (const [name, cut] of parts) {
          if (name) await ankiConnect('storeMediaFile', { filename: name, data: await blobToBase64(await cut()) });
        }
        await $api(`mined/sentences/${s.minedSentenceId}/media`, { method: 'POST', body: { audio: s.audio, picture: s.picture, video: s.video } });
        updated++;
      } catch (e) {
        if (e instanceof TypeError) throw e;
        failed++;
      }
    }
    return { updated, failed };
  }

  /**
   * Automatic sentences: adds the lines the server picks for mined words' cards (`mined/auto/plan`), each built with its
   * note type's automatic settings (parts and English), its media stored in Anki, and marked as automatic. The cards are
   * written by Sync's next step. A line that fails, or comes without English when English is needed, is skipped and
   * tried again on the next Sync; a lost connection stops the Sync.
   */
  async function addAutoSentences(
    progress?: (done: number, total: number) => void
  ): Promise<{ added: number; words: number; failed: number; skipped: number; capped: boolean; startedAt: string; byModel: Record<string, number> }> {
    const plan = await $api<{
      words: { minedWordId: number; ankiNoteId: number; modelName: string; expression: string; sentences: ExampleSentence[] }[];
      lines: number;
      capped: boolean;
      startedAt: string;
    }>('mined/auto/plan', { method: 'POST' });
    const byModel: Record<string, number> = {};
    const words = new Set<number>();
    let added = 0;
    let failed = 0;
    let skipped = 0;
    let done = 0;
    for (const word of plan.words) {
      const auto = autoSentencesOf(anki.setupFor(word.modelName));
      const parts: MiningParts = { audio: auto.audio, picture: auto.picture, english: auto.english !== 'none', video: auto.video };
      const want = { ...parts, video: wantsVideo(word.modelName, parts) };
      const build = { english: auto.english, padStartMs: settings.value.padStartMs, padEndMs: settings.value.padEndMs };
      for (const sentence of word.sentences) {
        progress?.(done++, plan.lines);
        try {
          // A line without the English it needs is skipped for good, before any media is cut and stored in Anki.
          if (auto.needsEnglish && parts.english) {
            const english = await cardEnglish(sentence.sentenceId, build.english);
            if (!english.text) {
              // Only a line that has no English is skipped; a failed request or English turned off site-wide is tried again.
              if (english.reason?.startsWith('no ')) {
                await $api('mined/auto/skip', { method: 'POST', body: { ankiNoteId: word.ankiNoteId, exampleSentenceId: sentence.sentenceId } });
                skipped++;
              } else failed++;
              continue;
            }
          }
          const built = await buildSentence(sentence, want, build);
          // A line that needs a clip goes on the card only with it: one whose cut failed is tried again next Sync.
          const b = built.body;
          if (auto.needsClip && ((want.audio && !b.audioFilename?.endsWith('.mp3')) || (want.picture && !b.pictureFilename) || (want.video && !b.videoFilename))) {
            failed++;
            continue;
          }
          const result = await $api<AddedSentence>('mined/sentences', { method: 'POST', body: { ankiNoteId: word.ankiNoteId, ...built.body, auto: true } });
          if (!result.added) continue;
          added++;
          words.add(word.minedWordId);
          byModel[word.modelName] = (byModel[word.modelName] ?? 0) + 1;
        } catch (e) {
          if (e instanceof TypeError) throw e;
          failed++;
        }
      }
    }
    return { added, words: words.size, failed, skipped, capped: plan.capped, startedAt: plan.startedAt, byModel };
  }

  /**
   * The sentence's audio and screenshot, cut from its episode. Without a linked video the audio is text-to-speech and
   * there is no screenshot. A part that fails is left out, with the reason.
   */
  async function cardMedia(
    sentence: Pick<ExampleSentence, 'sentenceId' | 'clipKey' | 'pictureKey'>,
    wantAudio: boolean,
    wantPicture: boolean,
    wantVideo: boolean,
    pad: Padding
  ): Promise<{ audio: CardFile | null; picture: CardFile | null; video: CardFile | null; reasons: EmptyReasons }> {
    // A fixed clip or frame is stored under a name of its own, which Sync compares to find cards that are behind
    const { sentenceId, clipKey = '', pictureKey = '' } = sentence;
    const reasons: EmptyReasons = {};
    const audio = async (): Promise<CardFile | null> => {
      try {
        return { blob: await fetchAudio(sentenceId, pad), filename: `jiten_${sentenceId}${clipKey}.mp3` };
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
        return { blob: await fetchPicture(sentenceId, pictureKey), filename: `jiten_${sentenceId}${pictureKey}.jpg` };
      } catch (e) {
        reasons.picture = isNotFound(e) ? 'this title has no linked video' : failureReason('Cutting the screenshot', e);
        return null;
      }
    };
    const video = async (): Promise<CardFile | null> => {
      try {
        return { blob: await fetchVideo(sentenceId, pad, clipKey), filename: `jiten_${sentenceId}${clipKey}.webm` };
      } catch (e) {
        reasons.video = isNotFound(e) ? 'this title has no linked video' : failureReason('Cutting the video clip', e);
        return null;
      }
    };
    const [a, p, v] = await Promise.all([wantAudio ? audio() : null, wantPicture ? picture() : null, wantVideo ? video() : null]);
    return { audio: a, picture: p, video: v, reasons };
  }

  /** The English a card gets with the pipeline's choice, with why there's none: null and no reason when it gets none. */
  async function cardEnglish(sentenceId: number, english: PipelineEnglish): Promise<{ text: string | null; origin: string | null; reason?: string }> {
    try {
      const translation = await forCardWith(sentenceId, english);
      if (translation?.text) return { text: escapeFieldText(translation.text), origin: translation.origin };
      const mode = (await loadTranslationOptions())?.cardMode;
      if (mode === 'off' || !mode || english === 'none') return { text: null, origin: null };
      const allowed = english === 'subtitlesThenAi' && mode === 'subtitlesThenAi';
      return { text: null, origin: null, reason: allowed ? 'no subtitles or AI translation for this line' : 'no subtitles for this line' };
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
    want: { audio: boolean; picture: boolean; english: boolean; video: boolean },
    build: Pick<BuildChoice, 'english' | 'padStartMs' | 'padEndMs'>
  ): Promise<BuiltSentence> {
    const [media, english] = await Promise.all([
      cardMedia(sentence, want.audio, want.picture, want.video, build),
      want.english
        ? cardEnglish(sentence.sentenceId, build.english)
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
   * Whether mining cuts the clip for a note type: the pipeline's video part is on, and something shows it, a field with
   * the Video role or the rotation script's sentence list, from v2 (which plays it in the picture slot; v1 can't show it).
   */
  function wantsVideo(modelName: string, parts: MiningParts): boolean {
    if (!parts.video) return false;
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
  function warnRoles(modelName: string, parts: MiningParts): readonly FieldRole[] {
    const m = miningFor(modelName);
    const off = partsOffRoles(parts);
    return rolesWith(m.emptyFields ?? DEFAULT_EMPTY_FIELDS, 'warn', 'deny').filter((r) => !off.includes(r));
  }

  /** The note type's roles that stop mining when they would be empty. */
  function denyRoles(modelName: string, parts: MiningParts): FieldRole[] {
    const m = miningFor(modelName);
    const off = partsOffRoles(parts);
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
   * The word's notes in Anki by its dictionary entry (GDR-061). Notes of every note type with sentence actions (within its
   * filter) whose Word field holds the word's spelling are found first, as "Hide words already in Anki" finds them; those
   * not in the store yet are imported, so Sync's matching puts each on an entry. `own` are the notes stored on this
   * entry, oldest first, found by spelling or not, that are still in Anki; `others` are spelling matches stored on
   * another entry (or on none), oldest first.
   */
  async function existingNotes(word: WordSummary): Promise<WordNotes> {
    await anki.load();
    const setups = wordSetups();
    if (!setups.length || !word.reading) return { own: [], others: [] };
    const query = setups
      .map((s) => {
        const fields = fieldsFor(s.fieldRoles, 'word')
          .map((f) => `"${ankiSearchValue(f)}:${ankiSearchValue(word.reading)}"`)
          .join(' OR ');
        return `(${setupQuery(s, `(${fields})`)})`;
      })
      .join(' OR ');
    // The newest, when there are too many: a note not yet stored is most likely a recent one.
    const found = (await ankiConnect<number[]>('findNotes', { query })).sort((a, b) => a - b).slice(-MAX_WORD_NOTE_CANDIDATES);
    const classify = () =>
      $api<WordNotesResponse>('mined/word-notes', { method: 'POST', body: { wordId: word.wordId, readingIndex: word.readingIndex, candidates: found } });

    let result = await classify();
    // Only a word with no note of its own needs the rest sorted; a note that can't be imported is left out, as it can't
    // be told apart from another word's.
    if (!result.own.length && result.unstored.length) {
      const imported = await importNotes(result.unstored);
      if (imported.some((r) => r.status !== 'failed')) result = await classify();
    }

    // Stored notes the search didn't find may have been deleted in Anki, or be outside the setups now (INV-006).
    let own = result.own.map((n) => n.ankiNoteId);
    const unseen = own.filter((id) => !found.includes(id));
    if (unseen.length) {
      const inSetups = setups.map((s) => `(${setupQuery(s)})`).join(' OR ');
      const alive = new Set(await ankiConnect<number[]>('findNotes', { query: `nid:${unseen.join(',')} (${inSetups})` }));
      own = own.filter((id) => found.includes(id) || alive.has(id));
    }
    return { own, others: result.others };
  }

  type WordNote = { modelName: string; fields: Record<string, { value: string; order: number }> };

  /** Adds the plain text of the chosen fields of every note the search finds to `words`. */
  async function collectWords(query: string, fieldsOf: (note: WordNote) => string[], words: Set<string>) {
    const noteIds = await ankiConnect<number[]>('findNotes', { query });
    for (let i = 0; i < noteIds.length; i += 1000) {
      const notes = await ankiConnect<WordNote[]>('notesInfo', { notes: noteIds.slice(i, i + 1000) });
      for (const note of notes) {
        for (const f of fieldsOf(note)) {
          const w = plainFieldText(note.fields?.[f]?.value ?? '');
          if (w) words.add(w);
        }
      }
    }
  }

  /** Every word on the notes of note types with sentence actions, as plain text. */
  async function wordsInAnki(): Promise<string[]> {
    await anki.load();
    const words = new Set<string>();
    for (const setup of wordSetups()) {
      const wordFields = fieldsFor(setup.fieldRoles, 'word');
      await collectWords(setupQuery(setup), () => wordFields, words);
    }
    return [...words];
  }

  /** The Anki collection's deck names, sorted. */
  async function deckNames(): Promise<string[]> {
    return (await ankiConnect<string[]>('deckNames')).sort((a, b) => a.localeCompare(b));
  }

  /**
   * Every word on the notes in a deck (and its subdecks), as plain text: the word fields of a note type that has a setup,
   * otherwise the note type's first field (a premade deck usually leads with the word).
   */
  async function wordsInDeck(deck: string): Promise<string[]> {
    await anki.load();
    const words = new Set<string>();
    await collectWords(
      `"deck:${ankiSearchValue(deck)}"`,
      (note) => {
        const setup = anki.setupFor(note.modelName);
        const roleFields = setup ? fieldsFor(setup.fieldRoles, 'word') : [];
        if (roleFields.length) return roleFields;
        const first = Object.entries(note.fields ?? {}).find(([, f]) => f.order === 0)?.[0];
        return first ? [first] : [];
      },
      words
    );
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
   * rotation. The pipeline's Build step decides the parts built; the kept note's note type, the empty-field policies.
   */
  async function addToExistingNote(
    sentence: ExampleSentence,
    noteIds: number[],
    keptInfo: AnkiNoteInfo | null | undefined,
    build: BuildChoice,
    /** The entry the line was mined for, when the note is another entry's: its Jiten-only copy there is removed. */
    parsed?: { wordId: number; readingIndex: number }
  ): Promise<MinedResult> {
    const kept = keptInfo !== undefined ? keptInfo : await keptNote(noteIds);
    const modelName = kept?.modelName ?? '';
    const { parts } = build;
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

    const built = await buildSentence(sentence, { ...parts, video: wantsVideo(modelName, parts) }, build);
    const builtParts = builtPartRoles({ ...parts, video: wantsVideo(modelName, parts) });

    // Denied parts stop the sentence before the store or the note changes.
    const deny = denyRoles(modelName, parts);
    if (deny.length) {
      const lines = emptyFieldWarnings({ fieldRoles: {}, fields: {}, warn: deny, reasons: built.reasons, parts: builtParts });
      if (deny.includes('sentenceList') && kept && listFieldMissing(kept.modelName, kept.fields)) lines.push(missingListWarning(kept.modelName));
      if (lines.length) throw new MineDenied(lines);
    }

    const result = await $api<AddedSentence>('mined/sentences', { method: 'POST', body: { ankiNoteId: noteIds[0], ...built.body, parsedWordId: parsed?.wordId, parsedReadingIndex: parsed?.readingIndex },
    });
    const warnings = result.added ? await writeAndCheck(result.write, built.reasons, builtParts, modelName, parts) : [];
    return { ankiNoteId: result.ankiNoteId, added: result.added, sentences: result.sentences, warnings };
  }

  /**
   * Writes the kept note and lists what was left empty: the list field, and the main fields when they were written. The
   * added sentence's English, audio and picture (`parts`) are stored with it, so their failures are listed by reason.
   */
  async function writeAndCheck(write: NoteWrite | null, reasons: EmptyReasons, parts: FieldRole[], modelName: string, built: MiningParts): Promise<string[]> {
    const warn = warnRoles(write?.modelName ?? modelName, built);
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
   * the main fields Sync would write, from the same sentence the store keeps. The note goes to the pipeline's target for
   * the line with its tags; the Build step decides the parts built, and the note type the empty-field policies.
   * `keptModelName` is the note type of the word's older note when it has one ("Create a new note"): that note gets the
   * sentence list, so its note type decides whether a missing list field is denied.
   */
  async function addCard(
    item: DeckIPlusOneSentence,
    keptModelName: string | null | undefined,
    plan: { target: NewCardTarget; build: BuildChoice }
  ): Promise<MinedResult> {
    const { sentence, word } = item;
    const { target, build } = plan;
    await anki.load();
    if (!targetReady(target))
      throw new TargetNotReady(
        `The target for this line (${target.deckName || 'no deck'}, ${target.modelName || 'no note type'}) isn't set up. Check the pipeline's Anki step and the note type's field roles.`
      );
    const mapping = rolesOf(target.modelName);
    const { parts } = build;
    const off = partsOffRoles(parts);
    const wanted = new Set(Object.values(mapping).filter((r) => !off.includes(r)));
    // The stored sentence also joins an older kept note's list, which may show a video the new note type doesn't.
    const reasons: EmptyReasons = {};

    const [details, built, { targetWordTemplate }] = await Promise.all([
      (['definitions', 'mainDefinition', 'pitchPosition', 'pitchCategories', 'frequency'] as const).some((f) => wanted.has(f))
        ? $api<Word>(`vocabulary/${word.wordId}/${word.readingIndex}`).catch((e) => {
            for (const role of ['definitions', 'mainDefinition', 'pitchPosition', 'pitchCategories', 'frequency'] as const)
              reasons[role] = failureReason('Reading the word', e);
            return null;
          })
        : null,
      buildSentence(
        sentence,
        {
          audio: wanted.has('sentenceAudio') || (!!keptModelName && parts.audio),
          picture: wanted.has('picture') || (!!keptModelName && parts.picture),
          english: wanted.has('sentenceEnglish') || (!!keptModelName && parts.english),
          video: wantsVideo(target.modelName, parts) || (!!keptModelName && wantsVideo(keptModelName, parts)),
        },
        build
      ),
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
    const deny = denyRoles(target.modelName, parts);
    const listModel = keptModelName || target.modelName;
    const denyList = denyRoles(listModel, parts).includes('sentenceList');
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
        if (done.missingList && warnRoles(write.modelName, parts).includes('sentenceList')) warnings.push(missingListWarning(write.modelName));
        listWritten = write.ankiNoteId === ankiNoteId && write.sentencesHtml != null && !done.missingList;
        if (write.ankiNoteId === ankiNoteId) current = done.fields;
      } catch (e) {
        // The card exists in Anki and in the store; the next Sync writes what's missing.
        warnings.push(`${failureReason('Writing the sentence list', e)}; the next Sync will write it.`);
      }
    }
    current ??= await noteFields(ankiNoteId);
    const roles = [...wanted].filter((r) => r !== 'sentenceList' || listWritten);
    warnings.push(...emptyFieldWarnings({ fieldRoles: mapping, fields: current, warn: warnRoles(target.modelName, parts), reasons, roles }));
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
    targetReady,
    wordsInAnki,
    deckNames,
    wordsInDeck,
    writeNote,
    recordMainWritten,
    addToExistingNote,
    rebuildMovedSentences,
    updateFixedMedia,
    addAutoSentences,
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
        const { body } = await buildSentence(s.sentence, { audio: false, picture: false, english: true, video: false }, { english: 'subtitlesThenAi', ...settings.value });
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
