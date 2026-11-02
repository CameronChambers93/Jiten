import { ankiConnect } from '~/composables/useAnkiConnect';
import { noteMedia, toImportNote, type AnkiNoteInfo, type ImportNote } from '~/utils/minedImport';

export interface MinedImportResult {
  ankiNoteId: number;
  status: 'imported' | 'already' | 'failed';
  minedWordId: number | null;
  matched: boolean;
  origin: string | null;
  error: string | null;
}

/** Notes per import request; media rides along as base64, so batches stay small. */
const BATCH = 5;

/** Imports Anki notes into the sentence store, reading them (and any media Jiten can't cut again) through AnkiConnect and their note types' field roles. */
export function useMinedImport() {
  const { $api } = useNuxtApp();

  const { load, setupFor } = useAnkiSetups();

  /** Reads the notes through their note types' roles; notes whose note type has no setup can't be read. */
  async function readNotes(ids: number[]): Promise<{ notes: ImportNote[]; unreadable: MinedImportResult[] }> {
    await load();
    const infos = (await ankiConnect<(AnkiNoteInfo | Record<string, never>)[]>('notesInfo', { notes: ids })).filter((n): n is AnkiNoteInfo => 'noteId' in n);
    const unreadable: MinedImportResult[] = [];
    const readable = infos.filter((n) => {
      if (setupFor(n.modelName)) return true;
      unreadable.push({
        ankiNoteId: n.noteId,
        status: 'failed',
        minedWordId: null,
        matched: false,
        origin: null,
        error: `${n.modelName} isn't set up in Anki settings`,
      });
      return false;
    });
    const cards = await ankiConnect<{ cardId: number; deckName: string }[]>('cardsInfo', { cards: readable.map((n) => n.cards[0]).filter(Boolean) });
    const deckOf = new Map(cards.map((c) => [c.cardId, c.deckName]));

    const notes = await Promise.all(
      readable.map(async (note) => {
        const roles = setupFor(note.modelName)!.fieldRoles;
        const media: Record<string, string> = {};
        for (const filename of noteMedia(note, roles).fetch) {
          const data = await ankiConnect<string | false>('retrieveMediaFile', { filename });
          if (data) media[filename] = data;
        }
        return toImportNote(note, deckOf.get(note.cards[0]!) ?? '', media, roles);
      })
    );
    return { notes, unreadable };
  }

  /** Imports the notes in batches, reporting each batch's results as it lands. */
  async function importNotes(ids: number[], onBatch?: (results: MinedImportResult[], done: number) => void): Promise<MinedImportResult[]> {
    const all: MinedImportResult[] = [];
    for (let i = 0; i < ids.length; i += BATCH) {
      const { notes, unreadable } = await readNotes(ids.slice(i, i + BATCH));
      const results = [...unreadable];
      if (notes.length) results.push(...(await $api<{ results: MinedImportResult[] }>('mined/import', { method: 'POST', body: { notes } })).results);
      all.push(...results);
      onBatch?.(results, Math.min(i + BATCH, ids.length));
    }
    return all;
  }

  return { importNotes };
}
