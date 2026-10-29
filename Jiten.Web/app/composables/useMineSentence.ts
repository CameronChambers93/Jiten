import { useToast } from 'primevue/usetoast';
import type { ExampleSentence, WordSummary } from '~/types';

/** What adding a sentence did: a new note, or the sentence joined the word's existing card. */
export type MineOutcome = { kind: 'created' } | { kind: 'existing'; added: boolean; sentences: number };

/** A word already in Anki, waiting for the user to choose between its existing card and a new note. */
export interface MineChoice {
  word: WordSummary;
  notes: number[];
}

/**
 * Adding a sentence to Anki, the same on every page (n1miner). When the word already has a note the user chooses: add
 * the sentence to that card (the default) or create another note. Render `MineChoiceDialog` with `choice` and `answer`.
 */
export function useMineSentence() {
  const toast = useToast();
  const { addCard, existingNotes, addToExistingNote } = useMiningCards();

  const choice = ref<MineChoice | null>(null);
  let resolveChoice: ((answer: 'existing' | 'new' | null) => void) | null = null;

  function ask(word: WordSummary, notes: number[]): Promise<'existing' | 'new' | null> {
    resolveChoice?.(null);
    choice.value = { word, notes };
    return new Promise((resolve) => (resolveChoice = resolve));
  }

  function answer(value: 'existing' | 'new' | null) {
    choice.value = null;
    resolveChoice?.(value);
    resolveChoice = null;
  }

  function failed(e: unknown, summary: string) {
    const err = e as { data?: { message?: string }; message?: string } | null;
    const detail = e instanceof TypeError ? "Couldn't reach AnkiConnect at 127.0.0.1:8765. Is Anki open?" : (err?.data?.message ?? err?.message);
    toast.add({ severity: 'error', summary, detail, life: 6000, group: 'bottom' });
  }

  /**
   * Adds the sentence for the word (the sentence's wordPosition/wordLength mark the word). Null when the user cancelled
   * or it failed; a failure has been shown as a toast.
   */
  async function mine(word: WordSummary, sentence: ExampleSentence): Promise<MineOutcome | null> {
    let notes: number[];
    try {
      notes = await existingNotes(word.reading);
    } catch (e) {
      failed(e, "Couldn't check Anki for the word");
      return null;
    }

    const pick = notes.length > 0 ? await ask(word, notes) : 'new';
    if (pick === 'new') {
      try {
        await addCard({ word, sentence, wordSentences: 0 });
        toast.add({ severity: 'success', summary: `Added ${word.reading} to Anki`, life: 2500, group: 'bottom' });
        return { kind: 'created' };
      } catch (e) {
        failed(e, "Couldn't add the card");
        return null;
      }
    }
    if (pick === 'existing') {
      try {
        const result = await addToExistingNote(sentence, notes);
        toast.add({
          severity: result.added ? 'success' : 'info',
          summary: result.added
            ? `Added to ${word.reading}, now ${result.sentences} sentences`
            : `${word.reading} already has this sentence (${result.sentences} sentences)`,
          life: 3000,
          group: 'bottom',
        });
        return { kind: 'existing', added: result.added, sentences: result.sentences };
      } catch (e) {
        failed(e, "Couldn't add the sentence to the card");
        return null;
      }
    }
    return null;
  }

  return { mine, choice: readonly(choice), answer };
}
