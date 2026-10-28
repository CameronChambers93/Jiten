<script setup lang="ts">
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import Message from 'primevue/message';
  import RadioButton from 'primevue/radiobutton';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import { ankiConnect } from '~/composables/useMiningCards';
  import { LAPIS_FIELDS, noteFlags, type AnkiNoteInfo } from '~/utils/minedImport';
  import { sanitiseHtml } from '~/utils/sanitiseHtml';

  interface FoldNote {
    ankiNoteId: number;
    deckName: string;
    createdAt: string;
    ready: boolean;
    sentence: string | null;
  }

  interface FoldCandidate {
    minedWordId: number;
    expression: string;
    reading: string;
    keep: number;
    notes: FoldNote[];
  }

  const props = defineProps<{
    /** Imports notes added since the last import; Sync runs it first. */
    importNew: () => Promise<void>;
  }>();
  const emit = defineEmits<{ changed: [] }>();

  /** Notes per updateNoteFields batch read. */
  const READ_BATCH = 100;

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();

  const running = ref(false);
  const step = ref('');
  const flagged = ref<number | null>(null);
  const written = ref<{ updated: number; unchanged: number; missingField: string[] } | null>(null);
  const folds = ref<FoldCandidate[] | null>(null);
  const keep = ref<Record<number, number>>({});
  const folding = ref<number | null>(null);

  async function readFlags() {
    const ids = await ankiConnect<number[]>('findNotes', { query: '"tag:sentence-fix::*"' });
    if (!ids.length) return 0;
    const flags = noteFlags(await ankiConnect<AnkiNoteInfo[]>('notesInfo', { notes: ids }));
    const { applied } = await $api<{ applied: { ankiNoteId: number; minedSentenceId: number }[] }>('mined/sync/flags', {
      method: 'POST',
      body: { flags: flags.map(({ ankiNoteId, minedSentenceId }) => ({ ankiNoteId, minedSentenceId })) },
    });
    // Jiten holds the flag from here on, so the tag comes off the note.
    for (const a of applied) {
      const tag = flags.find((f) => f.ankiNoteId === a.ankiNoteId && f.minedSentenceId === a.minedSentenceId)!.tag;
      await ankiConnect('removeTags', { notes: [a.ankiNoteId], tags: tag });
    }
    return applied.length;
  }

  async function writeSentences() {
    const { notes } = await $api<{ notes: { ankiNoteId: number; sentencesHtml: string }[] }>('mined/sync/sentences');
    const result = { updated: 0, unchanged: 0, missingField: [] as string[] };
    for (let i = 0; i < notes.length; i += READ_BATCH) {
      const batch = notes.slice(i, i + READ_BATCH);
      const infos = await ankiConnect<(AnkiNoteInfo | Record<string, never>)[]>('notesInfo', { notes: batch.map((n) => n.ankiNoteId) });
      for (const [j, info] of infos.entries()) {
        if (!('noteId' in info)) continue;
        const current = info.fields[LAPIS_FIELDS.sentences];
        if (!current) {
          if (!result.missingField.includes(info.modelName)) result.missingField.push(info.modelName);
          continue;
        }
        if (current.value === batch[j]!.sentencesHtml) {
          result.unchanged++;
          continue;
        }
        await ankiConnect('updateNoteFields', { note: { id: info.noteId, fields: { [LAPIS_FIELDS.sentences]: batch[j]!.sentencesHtml } } });
        result.updated++;
      }
    }
    return result;
  }

  async function loadFolds() {
    const { candidates } = await $api<{ candidates: FoldCandidate[] }>('mined/sync/folds');
    folds.value = candidates;
    keep.value = Object.fromEntries(candidates.map((c) => [c.minedWordId, c.keep]));
  }

  async function sync() {
    running.value = true;
    written.value = null;
    try {
      step.value = 'Importing new notes…';
      await props.importNew();
      step.value = 'Reading flags…';
      flagged.value = await readFlags();
      step.value = 'Writing sentences to notes…';
      written.value = await writeSentences();
      step.value = 'Finding notes to fold…';
      await loadFolds();
      emit('changed');
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Sync stopped', detail: extractApiError(e, 'Is Anki open with AnkiConnect?'), life: 8000 });
    } finally {
      running.value = false;
      step.value = '';
    }
  }

  async function addSentencesField(modelName: string) {
    try {
      const fields = await ankiConnect<string[]>('modelFieldNames', { modelName });
      await ankiConnect('modelFieldAdd', { modelName, fieldName: LAPIS_FIELDS.sentences, index: fields.length });
      toast.add({ severity: 'success', summary: `Added the ${LAPIS_FIELDS.sentences} field to ${modelName}`, detail: 'Sync again to fill it.', life: 4000 });
      if (written.value) written.value.missingField = written.value.missingField.filter((m) => m !== modelName);
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't add the field", detail: extractApiError(e, 'AnkiConnect refused'), life: 6000 });
    }
  }

  function fold(candidate: FoldCandidate) {
    const keepId = keep.value[candidate.minedWordId]!;
    const folded = candidate.notes.filter((n) => n.ankiNoteId !== keepId && n.ready).map((n) => n.ankiNoteId);
    if (!folded.length) return;
    confirm.require({
      header: `Fold ${candidate.expression}`,
      message: `Delete ${folded.length} Anki note${folded.length === 1 ? '' : 's'} (${folded.join(', ')}) and keep note ${keepId}? The sentences stay with the word (exact copies are kept once) and are written to the kept note on the next Sync. Review history on the deleted notes is lost.`,
      acceptProps: { label: 'Fold', severity: 'danger' },
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      accept: async () => {
        folding.value = candidate.minedWordId;
        try {
          await ankiConnect('deleteNotes', { notes: folded });
          await $api('mined/sync/fold', { method: 'POST', body: { minedWordId: candidate.minedWordId, keepAnkiNoteId: keepId, foldedAnkiNoteIds: folded } });
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
</script>

<template>
  <Card class="shadow-md mb-6">
    <template #title>Sync with Anki</template>
    <template #content>
      <p class="text-sm text-gray-600 dark:text-gray-300 mb-3">
        Imports new notes, takes in sentences flagged from cards, writes each word's sentences to its note's {{ LAPIS_FIELDS.sentences }} field, and lists words
        on more than one note to fold into one. Nothing is deleted without your confirmation.
      </p>
      <Button label="Sync" icon="pi pi-sync" size="small" :loading="running" @click="sync" />
      <Message v-if="running" severity="secondary" size="small" class="mt-3">{{ step }}</Message>
      <Message v-else-if="written" severity="success" size="small" class="mt-3">
        {{ flagged }} flags taken in · {{ written.updated }} notes updated, {{ written.unchanged }} unchanged
      </Message>
      <Message v-for="model in written?.missingField ?? []" :key="model" severity="warn" size="small" class="mt-2">
        <div class="flex flex-wrap items-center gap-2">
          <span>The note type {{ model }} has no {{ LAPIS_FIELDS.sentences }} field, so its notes weren't written.</span>
          <Button :label="`Add it to ${model}`" size="small" outlined @click="addSentencesField(model)" />
        </div>
      </Message>

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
