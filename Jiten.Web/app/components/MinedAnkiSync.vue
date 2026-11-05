<script setup lang="ts">
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import Message from 'primevue/message';
  import RadioButton from 'primevue/radiobutton';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import { ankiConnect } from '~/composables/useAnkiConnect';
  import type { MinedImportResult } from '~/composables/useMinedImport';
  import type { MainWritten } from '~/composables/useMiningCards';
  import { FLAG_QUERY, noteFlags, type AnkiNoteInfo } from '~/utils/minedImport';
  import { fieldFor, setupQuery } from '~/utils/ankiSetups';
  import type { NoteWrite } from '~/utils/ankiNoteWrite';
  import { sanitiseHtml } from '~/utils/sanitiseHtml';

  interface FoldNote {
    ankiNoteId: number;
    deckName: string;
    createdAt: string;
    ready: boolean;
    sentence: string | null;
    /** How the note's word was chosen; `guessed` notes warn before a fold. */
    wordMatch: string | null;
  }

  interface FoldCandidate {
    minedWordId: number;
    expression: string;
    reading: string;
    keep: number;
    notes: FoldNote[];
  }

  interface PendingFold {
    minedWordId: number;
    keep: number;
    folded: number[];
  }

  /** What Sync did to one note type's notes. */
  interface ModelResult {
    imported: number;
    importFailed: number;
    /** Notes already stored without a sentence of their own that got it. */
    sentencesAdded: number;
    /** Notes whose word was guessed among entries sharing its spelling, imported or checked now. */
    guessed: number;
    /** Stored notes found to be another word on their first check, and moved to it. */
    moved: number;
    known: number;
    flags: number;
    written: number;
    unchanged: number;
    /** The sentence-list field the note type is missing, when it is. */
    missingList: string | null;
  }

  const emit = defineEmits<{ changed: []; check: [expression: string] }>();

  /** Notes per notesInfo read while writing. */
  const READ_BATCH = 100;

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();
  const anki = useAnkiSetups();
  const { importNotes } = useMinedImport();
  const { writeNote, recordMainWritten, rebuildMovedSentences } = useMiningCards();
  const knownWords = useAnkiKnownWords();

  const running = ref(false);
  const step = ref('');
  const results = ref<Record<string, ModelResult> | null>(null);
  const known = ref<{ imported: number; updated: number; unmatched: string[]; withHistory: boolean } | null>(null);
  const foldsCompleted = ref(0);
  const folds = ref<FoldCandidate[] | null>(null);
  const keep = ref<Record<number, number>>({});
  const folding = ref<number | null>(null);

  onMounted(() => anki.load().catch(() => {}));

  function resultFor(model: string): ModelResult {
    results.value ??= {};
    return (results.value[model] ??= {
      imported: 0,
      importFailed: 0,
      sentencesAdded: 0,
      guessed: 0,
      moved: 0,
      known: 0,
      flags: 0,
      written: 0,
      unchanged: 0,
      missingList: null,
    });
  }

  async function syncKnownWords() {
    const r = await knownWords.sync((text) => (step.value = text));
    if (!r) return;
    for (const [model, n] of Object.entries(r.byModel)) resultFor(model).known += n;
    known.value = { imported: r.imported, updated: r.updated, unmatched: r.unmatched, withHistory: r.withHistory };
  }

  async function importNew() {
    const setups = anki.setups.value.filter((s) => s.importSentences);
    if (!setups.length) return;
    const { ankiNoteIds, withoutSentence, unchecked } = await $api<{ ankiNoteIds: number[]; withoutSentence: number[]; unchecked: number[] }>('mined/notes');
    const stored = new Set(ankiNoteIds);
    // Stored notes are sent again when they have no sentence of their own (synced for their list before their note type
    // mapped one), and the server adds it; or when their word was never checked against other entries with its spelling.
    const again = new Set([...withoutSentence, ...unchecked]);
    for (const setup of setups) {
      const ids = (await ankiConnect<number[]>('findNotes', { query: setupQuery(setup) })).filter((id) => !stored.has(id) || again.has(id));
      if (!ids.length) continue;
      step.value = `Importing ${ids.length} ${setup.modelName} notes…`;
      const res: MinedImportResult[] = await importNotes(ids, (_, done) => (step.value = `Importing ${setup.modelName} notes… ${done}/${ids.length}`));
      const r = resultFor(setup.modelName);
      r.imported += res.filter((x) => x.status === 'imported').length;
      r.sentencesAdded += res.filter((x) => x.status === 'sentence').length;
      r.guessed += res.filter((x) => x.wordMatch === 'guessed').length;
      r.moved += res.filter((x) => x.moved).length;
      r.importFailed += res.filter((x) => x.status === 'failed').length;
      ids.forEach((id) => stored.add(id));
    }
  }

  async function readFlags() {
    for (const setup of anki.setups.value.filter((s) => s.takeFlags)) {
      const ids = await ankiConnect<number[]>('findNotes', { query: setupQuery(setup, FLAG_QUERY) });
      if (!ids.length) continue;
      const flags = noteFlags(await ankiConnect<AnkiNoteInfo[]>('notesInfo', { notes: ids }));
      const { applied } = await $api<{ applied: { ankiNoteId: number; minedSentenceId: number | null }[] }>('mined/sync/flags', {
        method: 'POST',
        body: { flags: flags.map(({ ankiNoteId, minedSentenceId }) => ({ ankiNoteId, minedSentenceId })) },
      });
      // Jiten holds the flag from here on, so the tag comes off the note.
      for (const a of applied) {
        const tag = flags.find((f) => f.ankiNoteId === a.ankiNoteId && f.minedSentenceId === a.minedSentenceId)!.tag;
        await ankiConnect('removeTags', { notes: [a.ankiNoteId], tags: tag });
      }
      resultFor(setup.modelName).flags += applied.length;
    }
  }

  /**
   * Folds recorded but never confirmed: confirmed for the notes gone from Anki (all, or the ones a failed delete got to),
   * dropped when none are.
   */
  async function finishPendingFolds(pending: PendingFold[]) {
    for (const p of pending) {
      const infos = await ankiConnect<(AnkiNoteInfo | Record<string, never>)[]>('notesInfo', { notes: p.folded });
      const gone = p.folded.filter((_, i) => !('noteId' in (infos[i] ?? {})));
      if (gone.length) {
        const { write } = await $api<{ write: NoteWrite | null }>(`mined/sync/fold/${p.minedWordId}/confirm`, {
          method: 'POST',
          body: { goneAnkiNoteIds: gone },
        });
        if (write) await writeNote(write);
        foldsCompleted.value++;
      } else {
        await $api(`mined/sync/fold/${p.minedWordId}`, { method: 'DELETE' });
      }
    }
  }

  async function writeNotes() {
    const { notes } = await $api<{ notes: NoteWrite[] }>('mined/sync/notes');
    for (let i = 0; i < notes.length; i += READ_BATCH) {
      const batch = notes.slice(i, i + READ_BATCH);
      const infos = await ankiConnect<(AnkiNoteInfo | Record<string, never>)[]>('notesInfo', { notes: batch.map((n) => n.ankiNoteId) });
      const mainWritten: MainWritten[] = [];
      for (const [j, info] of infos.entries()) {
        if (!('noteId' in info)) continue;
        const write = batch[j]!;
        const r = resultFor(write.modelName);
        const done = await writeNote(write, info as AnkiNoteInfo, false);
        if (done.mainWritten) mainWritten.push(done.mainWritten);
        if (done.missingList) r.missingList = fieldFor(anki.setupFor(write.modelName)?.fieldRoles ?? {}, 'sentenceList');
        if (done.updated) r.written++;
        else r.unchanged++;
      }
      await recordMainWritten(mainWritten);
    }
  }

  async function loadFolds(): Promise<PendingFold[]> {
    const { candidates, pending } = await $api<{ candidates: FoldCandidate[]; pending: PendingFold[] }>('mined/sync/folds');
    folds.value = candidates;
    keep.value = Object.fromEntries(candidates.map((c) => [c.minedWordId, c.keep]));
    return pending;
  }

  async function sync() {
    running.value = true;
    results.value = null;
    known.value = null;
    foldsCompleted.value = 0;
    try {
      await anki.load(true);
      if (!anki.setups.value.length) {
        toast.add({ severity: 'info', summary: 'No note types are synced yet', detail: 'Set them up in Anki settings.', life: 6000 });
        return;
      }
      await syncKnownWords();
      step.value = 'Importing new notes…';
      await importNew();
      step.value = 'Reading flags…';
      await readFlags();
      step.value = 'Finishing folds…';
      await finishPendingFolds(await loadFolds());
      step.value = 'Updating sentences whose line changed…';
      const moved = await rebuildMovedSentences();
      if (moved.failed)
        toast.add({
          severity: 'warn',
          summary: `${moved.failed} sentence${moved.failed === 1 ? '' : 's'} not updated`,
          detail: 'They keep their current text and are tried again on the next Sync.',
          life: 6000,
        });
      step.value = 'Writing sentences to notes…';
      await writeNotes();
      step.value = 'Finding notes to fold…';
      await loadFolds();
      results.value ??= {};
      emit('changed');
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Sync stopped', detail: extractApiError(e, 'Is Anki open with AnkiConnect?'), life: 8000 });
    } finally {
      running.value = false;
      step.value = '';
    }
  }

  function addListField(modelName: string, fieldName: string) {
    confirm.require({
      header: `Add ${fieldName} to ${modelName}`,
      message: `Adding a field changes the note type, so Anki's next sync to AnkiWeb has to be a one-way full upload. Add the ${fieldName} field?`,
      acceptProps: { label: 'Add the field' },
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      accept: async () => {
        try {
          const fields = await ankiConnect<string[]>('modelFieldNames', { modelName });
          await ankiConnect('modelFieldAdd', { modelName, fieldName, index: fields.length });
          toast.add({ severity: 'success', summary: `Added ${fieldName} to ${modelName}`, detail: 'Sync again to fill it.', life: 4000 });
          if (results.value?.[modelName]) results.value[modelName]!.missingList = null;
        } catch (e) {
          toast.add({ severity: 'error', summary: "Couldn't add the field", detail: extractApiError(e, 'AnkiConnect refused'), life: 6000 });
        }
      },
    });
  }

  function fold(candidate: FoldCandidate) {
    const keepId = keep.value[candidate.minedWordId]!;
    const folded = candidate.notes.filter((n) => n.ankiNoteId !== keepId && n.ready).map((n) => n.ankiNoteId);
    if (!folded.length) return;
    const involved = candidate.notes.filter((n) => n.ankiNoteId === keepId || folded.includes(n.ankiNoteId));
    const guessed = involved.filter((n) => n.wordMatch === 'guessed').length;
    const guessWarning = guessed
      ? `${guessed} of these notes ${guessed === 1 ? 'was' : 'were'} matched to this word by guess, and may be a different word with the same spelling. `
      : '';
    confirm.require({
      header: guessed ? `Fold ${candidate.expression} anyway?` : `Fold ${candidate.expression}`,
      message: `${guessWarning}Delete ${folded.length} Anki note${folded.length === 1 ? '' : 's'} (${folded.join(', ')}) and keep note ${keepId}? The sentences stay with the word (exact copies are kept once) and are written to the kept note now. Review history on the deleted notes is lost.`,
      icon: guessed ? 'pi pi-exclamation-triangle' : undefined,
      acceptProps: { label: guessed ? 'Fold anyway' : 'Fold', severity: 'danger' },
      rejectProps: { label: guessed ? 'Check word' : 'Cancel', severity: 'secondary', outlined: true },
      reject: () => {
        if (guessed) emit('check', candidate.expression);
      },
      accept: async () => {
        folding.value = candidate.minedWordId;
        try {
          // Recorded first, so a failure after the delete leaves a pending fold the next Sync completes.
          await $api('mined/sync/fold', { method: 'POST', body: { minedWordId: candidate.minedWordId, keepAnkiNoteId: keepId, foldedAnkiNoteIds: folded } });
          try {
            await ankiConnect('deleteNotes', { notes: folded });
          } catch (e) {
            await $api(`mined/sync/fold/${candidate.minedWordId}`, { method: 'DELETE' }).catch(() => {});
            throw e;
          }
          const { write } = await $api<{ write: NoteWrite | null }>(`mined/sync/fold/${candidate.minedWordId}/confirm`, { method: 'POST' });
          if (write) await writeNote(write);
          await loadFolds();
          emit('changed');
        } catch (e) {
          toast.add({ severity: 'error', summary: 'Fold failed', detail: extractApiError(e, 'The fold failed'), life: 6000 });
        } finally {
          folding.value = null;
        }
      },
    });
  }

  const modelRows = computed(() => Object.entries(results.value ?? {}).sort(([a], [b]) => a.localeCompare(b)));

  function describe(r: ModelResult): string {
    const parts: string[] = [];
    if (r.known) parts.push(`${r.known} known words sent`);
    if (r.imported) parts.push(`${r.imported} notes imported`);
    if (r.sentencesAdded) parts.push(`${r.sentencesAdded} notes' sentences added`);
    if (r.moved) parts.push(`${r.moved} notes moved to another word`);
    if (r.guessed) parts.push(`${r.guessed} notes matched by guess`);
    if (r.importFailed) parts.push(`${r.importFailed} couldn't be imported`);
    if (r.flags) parts.push(`${r.flags} flags taken in`);
    if (r.written || r.unchanged) parts.push(`${r.written} notes written, ${r.unchanged} unchanged`);
    return parts.join(' · ') || 'Nothing to do';
  }
</script>

<template>
  <Card class="shadow-md mb-6">
    <template #title>Sync with Anki</template>
    <template #content>
      <p class="text-sm text-gray-600 dark:text-gray-300 mb-3">
        For each note type in <NuxtLink to="/settings/anki" class="underline">Anki settings</NuxtLink>: sends known words, imports new notes, takes in sentences
        flagged from cards, writes each word's sentences to its note, and lists words on more than one note to fold into one. Nothing is deleted without your
        confirmation.
      </p>
      <Message v-if="anki.loaded.value && !anki.setups.value.length" severity="info" size="small" class="mb-3">
        No note types are synced yet. <NuxtLink to="/settings/anki" class="underline">Set them up</NuxtLink>.
      </Message>
      <Button label="Sync" icon="pi pi-sync" size="small" :loading="running" @click="sync" />
      <Message v-if="running" severity="secondary" size="small" class="mt-3">{{ step }}</Message>

      <div v-else-if="results" class="mt-3 flex flex-col gap-2" data-testid="sync-results">
        <Message v-if="!modelRows.length && !foldsCompleted" severity="success" size="small">Nothing to do.</Message>
        <div v-for="[model, r] in modelRows" :key="model" class="rounded border border-surface-200 dark:border-surface-700 px-3 py-2 text-sm">
          <div class="font-semibold">{{ model }}</div>
          <div class="text-gray-600 dark:text-gray-300">{{ describe(r) }}</div>
          <div v-if="r.missingList" class="mt-1 flex flex-wrap items-center gap-2 text-amber-700 dark:text-amber-400">
            <span>The note type has no {{ r.missingList }} field, so its sentence lists weren't written.</span>
            <Button :label="`Add ${r.missingList}`" size="small" outlined @click="addListField(model, r.missingList)" />
          </div>
        </div>
        <Message v-if="known" severity="secondary" size="small">
          Known words: {{ known.imported }} new, {{ known.updated }} updated<template v-if="known.unmatched.length">
            · {{ known.unmatched.length }} with no dictionary match ({{ known.unmatched.slice(0, 10).join('、')
            }}<template v-if="known.unmatched.length > 10">…</template>)</template
          ><template v-if="!known.withHistory"> · this AnkiConnect can't send review history, so only card states were sent</template>
        </Message>
        <Message v-if="foldsCompleted" severity="secondary" size="small">{{ foldsCompleted }} unfinished folds completed.</Message>
      </div>

      <div v-if="folds" class="mt-4">
        <h3 class="font-semibold mb-2">Words on more than one note ({{ folds.length }})</h3>
        <p v-if="!folds.length" class="text-sm text-gray-500">None.</p>
        <div v-for="c in folds" :key="c.minedWordId" class="border border-surface-200 dark:border-surface-700 rounded p-3 mb-2">
          <div class="flex items-center justify-between gap-2 mb-2">
            <span
              ><span class="text-lg font-semibold" lang="ja">{{ c.expression }}</span> <span class="text-gray-500" lang="ja">{{ c.reading }}</span></span
            >
            <Button
              label="Fold into kept note"
              size="small"
              severity="danger"
              outlined
              :loading="folding === c.minedWordId"
              :disabled="!c.notes.some((n) => n.ankiNoteId !== keep[c.minedWordId] && n.ready)"
              @click="fold(c)"
            />
          </div>
          <label v-for="n in c.notes" :key="n.ankiNoteId" class="flex items-start gap-2 text-sm py-1">
            <RadioButton v-model="keep[c.minedWordId]" :value="n.ankiNoteId" />
            <span class="flex-1 min-w-0">
              <!-- eslint-disable-next-line vue/no-v-html -->
              <span v-if="n.sentence" lang="ja" v-html="sanitiseHtml(n.sentence)" />
              <span v-else class="text-gray-500">No sentence</span>
              <span class="block text-xs text-gray-500">
                {{ n.deckName }} · {{ new Date(n.createdAt).toLocaleString() }}
                <template v-if="n.wordMatch === 'guessed'"> · <span class="text-orange-600">guessed word</span></template>
                <template v-if="n.ankiNoteId === keep[c.minedWordId]"> · kept</template>
                <template v-else-if="!n.ready"> · too new to fold (GSM may still be adding media)</template>
              </span>
            </span>
          </label>
        </div>
      </div>
    </template>
  </Card>
</template>
