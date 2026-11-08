import { useToast } from 'primevue/usetoast';
import type { ExampleSentence, WordSummary } from '~/types';
import { MineDenied } from '~/utils/miningWarnings';
import type { NewCardTarget } from '~/utils/ankiSetups';
import { buildChoice, firstStep, hasAnkiStep, pipelineTags, stepTarget, type Pipeline, type PipelineContext } from '~/utils/pipelines';
import { getMediaTypeText } from '~/utils/mediaTypeMapper';

/**
 * What running a pipeline did in Anki: a new note, the sentence joined the word's existing card, or nothing (no Anki step
 * matched, or it skipped), so the sentence stays kept for Jiten. `warnings` lists fields left empty.
 */
export type MineOutcome =
  | { kind: 'created'; warnings: string[] }
  | { kind: 'existing'; added: boolean; sentences: number; warnings: string[] }
  | { kind: 'kept' };

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
 * Running a pipeline's Anki, Build and Tags steps on a sentence, the same on every page (n1miner); saving to the word's
 * list is the sentence button's. The first matching Anki step decides: add to the word's card, ask, make a new note
 * from its targets, or skip. Render `MineChoiceDialog` with `choice` and `answer`.
 */
export function useMineSentence() {
  const toast = useToast();
  const { addCard, existingNotes, keptNote, addToExistingNote } = useMiningCards();

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
   * Runs the pipeline's Anki steps for the word (the sentence's wordPosition/wordLength mark the word). Null when the user
   * cancelled or it failed; a failure has been shown as a toast.
   */
  async function mine(word: WordSummary, sentence: ExampleSentence, pipeline: Pipeline): Promise<MineOutcome | null> {
    if (!hasAnkiStep(pipeline)) return { kind: 'kept' };
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
    const ctx: PipelineContext = { sentence, cardNoteType: modelName || null };
    const step = firstStep(pipeline, 'anki', ctx);
    if (!step) return { kind: 'kept' };
    const buildStep = firstStep(pipeline, 'build', ctx);
    const build = buildChoice(buildStep);
    const target = stepTarget(step, sentence, pipelineTags(pipeline, ctx, (t) => getMediaTypeText(t)));
    const noBuild = buildStep ? [] : [`No Build step in ${pipeline.name} matched this line, so the card has text only.`];

    let pick: 'existing' | 'new' | null;
    if (notes.length) {
      if (step.hasCard === 'skip') return { kind: 'kept' };
      // A step with no targets can't make a new note, so asking would offer nothing.
      pick = step.hasCard === 'add' || (step.hasCard === 'ask' && !target) ? 'existing' : step.hasCard === 'new' ? 'new' : null;
      if (pick == null) pick = await ask({ word, notes, modelName, target: target! });
    } else {
      if (step.noCard === 'skip') return { kind: 'kept' };
      pick = 'new';
    }
    if (pick === 'new') {
      if (!target) {
        failed(new Error(`${pipeline.name}'s Anki step has no target for new cards.`), "Couldn't add the card");
        return null;
      }
      try {
        const result = await addCard({ word, sentence, wordSentences: 0 }, modelName || null, { target, build });
        result.warnings.push(...noBuild);
        done(`Added ${word.reading} to Anki`, result.warnings);
        return { kind: 'created', warnings: result.warnings };
      } catch (e) {
        failed(e, e instanceof MineDenied ? `Didn't add ${word.reading} to Anki` : "Couldn't add the card");
        return null;
      }
    }
    if (pick === 'existing') {
      try {
        const result = await addToExistingNote(sentence, notes, kept, build);
        result.warnings.push(...noBuild);
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
