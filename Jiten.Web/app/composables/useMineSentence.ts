import { useToast } from 'primevue/usetoast';
import type { ExampleSentence, WordSummary } from '~/types';
import { MineDenied } from '~/utils/miningWarnings';
import type { NewCardTarget } from '~/utils/ankiSetups';

/** What adding a sentence did: a new note, or the sentence joined the word's existing card. `warnings` lists fields left empty. */
export type MineOutcome = { kind: 'created'; warnings: string[] } | { kind: 'existing'; added: boolean; sentences: number; warnings: string[] };

/** A word already in Anki, waiting for the user to choose between its existing card and a new note. */
export interface MineChoice {
  word: WordSummary;
  notes: number[];
  /** The note type of the card that would get the sentence (the word's oldest note); empty when it couldn't be read. */
  modelName: string;
  /** Where a new note would go. */
  target: NewCardTarget;
}

/**
 * Adding a sentence to Anki, the same on every page (n1miner). When the word already has a note, its note type's
 * mining settings decide: add the sentence to that card without asking, or ask the user, defaulting to the card. Render
 * `MineChoiceDialog` with `choice` and `answer`.
 */
export function useMineSentence() {
  const toast = useToast();
  const { addCard, existingNotes, keptNote, miningFor, targetFor, addToExistingNote } = useMiningCards();

  const choice = ref<MineChoice | null>(null);
  let resolveChoice: ((answer: 'existing' | 'new' | null) => void) | null = null;

  function ask(next: MineChoice): Promise<'existing' | 'new' | null> {
    resolveChoice?.(null);
    choice.value = next;
    return new Promise((resolve) => (resolveChoice = resolve));
  }

  function answer(value: 'existing' | 'new' | null) {
    choice.value = null;
    resolveChoice?.(value);
    resolveChoice = null;
  }

  /** The success line, or a warning that stays until closed when fields were left empty. */
  function done(summary: string, warnings: string[], severity: 'success' | 'info' = 'success') {
    if (warnings.length) toast.add({ severity: 'warn', summary, detail: warnings.join('\n'), group: 'bottom', styleClass: 'whitespace-pre-line' });
    else toast.add({ severity, summary, life: severity === 'success' ? 2500 : 3000, group: 'bottom' });
  }

  function failed(e: unknown, summary: string) {
    if (e instanceof MineDenied) {
      // Stays until closed, like a warning: it says what to fix.
      toast.add({
        severity: 'error',
        summary,
        detail: [...e.lines, 'Card settings decide which empty fields stop a card.'].join('\n'),
        group: 'bottom',
        styleClass: 'whitespace-pre-line',
      });
      return;
    }
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
    let kept: Awaited<ReturnType<typeof keptNote>> = null;
    try {
      notes = await existingNotes(word.reading);
      if (notes.length) kept = await keptNote(notes);
    } catch (e) {
      failed(e, "Couldn't check Anki for the word");
      return null;
    }

    const modelName = kept?.modelName ?? '';
    let pick: 'existing' | 'new' | null = 'new';
    if (notes.length) pick = modelName && miningFor(modelName).existing === 'auto' ? 'existing' : await ask({ word, notes, modelName, target: targetFor(sentence) });
    if (pick === 'new') {
      try {
        const result = await addCard({ word, sentence, wordSentences: 0 }, modelName || null);
        done(`Added ${word.reading} to Anki`, result.warnings);
        return { kind: 'created', warnings: result.warnings };
      } catch (e) {
        failed(e, e instanceof MineDenied ? `Didn't add ${word.reading} to Anki` : "Couldn't add the card");
        return null;
      }
    }
    if (pick === 'existing') {
      try {
        const result = await addToExistingNote(sentence, notes, kept);
        done(
          result.added
            ? `Added to ${word.reading}${modelName ? ` on ${modelName}` : ''}, now ${result.sentences} sentences`
            : `${word.reading} already has this sentence (${result.sentences} sentences)`,
          result.warnings,
          result.added ? 'success' : 'info'
        );
        return { kind: 'existing', added: result.added, sentences: result.sentences, warnings: result.warnings };
      } catch (e) {
        failed(e, e instanceof MineDenied ? `Didn't add the sentence to ${word.reading}` : "Couldn't add the sentence to the card");
        return null;
      }
    }
    return null;
  }

  return { mine, choice: readonly(choice), answer };
}
