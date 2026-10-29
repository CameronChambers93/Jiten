import { ankiConnect } from '~/composables/useMiningCards';
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

/** Imports Anki notes into the sentence store, reading them (and any media Jiten can't cut again) through AnkiConnect. */
export function useMinedImport() {
  const { $api } = useNuxtApp();

  async function readNotes(ids: number[]): Promise<ImportNote[]> {
    const notes = await ankiConnect<AnkiNoteInfo[]>('notesInfo', { notes: ids });
    const cards = await ankiConnect<{ cardId: number; deckName: string }[]>('cardsInfo', { cards: notes.map((n) => n.cards[0]).filter(Boolean) });
    const deckOf = new Map(cards.map((c) => [c.cardId, c.deckName]));

    return Promise.all(
      notes.map(async (note) => {
        const media: Record<string, string> = {};
        for (const filename of noteMedia(note).fetch) {
          const data = await ankiConnect<string | false>('retrieveMediaFile', { filename });
          if (data) media[filename] = data;
        }
        return toImportNote(note, deckOf.get(note.cards[0]!) ?? '', media);
      })
    );
  }

  /** Imports the notes in batches, reporting each batch's results as it lands. */
  async function importNotes(ids: number[], onBatch?: (results: MinedImportResult[], done: number) => void): Promise<MinedImportResult[]> {
    const all: MinedImportResult[] = [];
    for (let i = 0; i < ids.length; i += BATCH) {
      const notes = await readNotes(ids.slice(i, i + BATCH));
      const { results } = await $api<{ results: MinedImportResult[] }>('mined/import', { method: 'POST', body: { notes } });
      all.push(...results);
      onBatch?.(results, Math.min(i + BATCH, ids.length));
    }
    return all;
  }

  return { importNotes };
}
