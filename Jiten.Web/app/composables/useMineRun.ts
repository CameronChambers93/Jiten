import type { ExampleSentence, WordSummary } from '~/types';
import { ankiConnect } from '~/composables/useAnkiConnect';
import { MineDenied, TargetNotReady } from '~/utils/miningWarnings';
import { emptyTally, runSteps, type RunItem, type RunTally, type WhenAsked } from '~/utils/mineSearches';
import { hasAnkiStep, type Pipeline } from '~/utils/pipelines';

/**
 * Why an error stops the whole run: AnkiConnect can't be reached (a failed fetch is a TypeError, but so is a bug, so
 * Anki is asked again), or a target isn't set up; every later line would fail the same way.
 */
async function fatalReason(e: unknown): Promise<string | null> {
  if (e instanceof TargetNotReady) return e.message;
  if (e instanceof TypeError) {
    const reachable = await ankiConnect('version').then(
      () => true,
      () => false
    );
    if (!reachable) return "Couldn't reach AnkiConnect at 127.0.0.1:8765. Is Anki open?";
  }
  return null;
}

function reasonOf(e: unknown): string {
  if (e instanceof MineDenied) return e.lines.join(' ');
  const err = e as { data?: { message?: string }; message?: string } | null;
  return err?.data?.message ?? err?.message ?? 'Unknown error';
}

/**
 * A bulk run of a pipeline over a Mine search's plan (GDR-059): in the open page, one sentence at a time, the same save
 * then mine path as the sentence button, no toasts and no dialogs. A word gets one note per run.
 */
export function useMineRun() {
  const list = useSentenceList();
  const { configured } = useMiningCards();
  const { mine } = useMineSentence();

  const running = ref(false);
  const done = ref(0);
  const total = ref(0);
  const current = ref<string | null>(null);
  const tally = ref<RunTally>(emptyTally());
  /** Why the run stopped early: the user, or an error that every later line would hit. */
  const stoppedBy = ref<string | null>(null);
  /** Words that got a card in this run (a new note or a sentence on one). */
  const cardedWords = ref<WordSummary[]>([]);
  let stopAsked = false;

  function stop() {
    stopAsked = true;
  }

  async function run(items: RunItem<WordSummary, ExampleSentence>[], pipeline: Pipeline, whenAsked: WhenAsked) {
    running.value = true;
    stopAsked = false;
    stoppedBy.value = null;
    done.value = 0;
    total.value = items.reduce((n, i) => n + i.sentences.length, 0);
    tally.value = emptyTally();
    cardedWords.value = [];
    const t = tally.value;
    // The note this run gave each word (a new note or a sentence on an existing one): later sentences go to that note.
    const carded = new Map<string, number>();
    const anki = hasAnkiStep(pipeline);

    try {
      if (anki && !configured.value) {
        stoppedBy.value = 'Set up a note type in Card settings first.';
        return;
      }
      for (const { word, sentence } of runSteps(items)) {
        if (stopAsked) {
          stoppedBy.value = 'Stopped.';
          break;
        }
        const key = `${word.wordId}:${word.readingIndex}`;
        current.value = word.reading;
        try {
          const state = await list.save(word.wordId, word.readingIndex, sentence.sentenceId, sentence.wordPosition, sentence.wordLength);
          // Not marked as carded: which card it is on isn't known here, and the step's own choice still applies.
          if (state === 'card') {
            t.alreadyDone++;
            continue;
          }
          if (!anki) {
            t.kept++;
            continue;
          }
          const outcome = await mine(word, sentence, pipeline, { whenAsked, cardNoteId: carded.get(key) ?? null });
          if (!outcome) continue;
          if (outcome.kind === 'kept') {
            if (outcome.skipped) t.skipped++;
            else t.kept++;
            continue;
          }
          if (!carded.has(key)) cardedWords.value.push(word);
          carded.set(key, outcome.noteId);
          if (outcome.warnings.length) t.warned.push({ word: word.reading, wordId: word.wordId, readingIndex: word.readingIndex, reason: outcome.warnings.join(' ') });
          list.setState(word.wordId, word.readingIndex, sentence.sentenceId, 'card');
          if (outcome.kind === 'created') t.created++;
          else if (outcome.added) t.added++;
          else t.alreadyOnCard++;
        } catch (e) {
          const fatal = await fatalReason(e);
          if (fatal) {
            stoppedBy.value = fatal;
            break;
          }
          t.failed.push({ word: word.reading, wordId: word.wordId, readingIndex: word.readingIndex, reason: reasonOf(e) });
        } finally {
          done.value++;
        }
      }
    } finally {
      current.value = null;
      running.value = false;
    }
  }

  return { running: readonly(running), done: readonly(done), total: readonly(total), current: readonly(current), tally, stoppedBy: readonly(stoppedBy), cardedWords, run, stop };
}
