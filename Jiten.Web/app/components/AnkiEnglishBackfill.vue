<script setup lang="ts">
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import InputText from 'primevue/inputtext';
  import Message from 'primevue/message';
  import { useToast } from 'primevue/usetoast';
  import { ankiConnect } from '~/composables/useMiningCards';
  import { noteContext, noteSource, skipReason, type BackfillNote } from '~/utils/ankiEnglishBackfill';
  import { escapeFieldText } from '~/utils/miningCardFields';
  import { sanitiseHtml } from '~/utils/sanitiseHtml';

  interface Row {
    noteId: number;
    sentence: string;
    source: string;
    context: string[];
    skip: string | null;
    english: string;
    status: 'pending' | 'translating' | 'translated' | 'failed' | 'written';
    error?: string;
  }

  interface Estimate {
    provider: string | null;
    model: string | null;
    estimatedCostUsd: number | null;
  }

  /** Parallel AI calls while translating. */
  const CONCURRENCY = 3;

  const { $api } = useNuxtApp();
  const toast = useToast();
  // Loads the saved AnkiConnect key that ankiConnect() sends.
  useMiningCards();

  const query = ref('"note:Lapis+" -tag:lapisplus-example "Sentence:_*" "SentenceEnglish:"');
  const sentenceField = ref('Sentence');
  const englishField = ref('SentenceEnglish');
  const rows = ref<Row[]>([]);
  const estimate = ref<Estimate | null>(null);
  const finding = ref(false);
  const translating = ref(false);
  const writing = ref(false);

  const toTranslate = computed(() => rows.value.filter((r) => !r.skip));
  const skipped = computed(() => rows.value.filter((r) => r.skip));
  const ready = computed(() => rows.value.filter((r) => r.status === 'translated' && r.english.trim()));

  async function find() {
    finding.value = true;
    rows.value = [];
    estimate.value = null;
    try {
      const ids = await ankiConnect<number[]>('findNotes', { query: query.value });
      const notes = ids.length ? await ankiConnect<BackfillNote[]>('notesInfo', { notes: ids }) : [];
      rows.value = notes.map((n) => ({
        noteId: n.noteId,
        sentence: n.fields[sentenceField.value]?.value ?? '',
        source: noteSource(n),
        context: noteContext(n),
        skip: n.fields[englishField.value] ? skipReason(n, sentenceField.value) : `no ${englishField.value} field`,
        english: '',
        status: 'pending',
      }));
      estimate.value = await $api<Estimate>('admin/translation/text/estimate', { query: { count: toTranslate.value.length } });
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't read Anki", detail: extractApiError(e, 'Is Anki open with AnkiConnect?'), life: 6000 });
    } finally {
      finding.value = false;
    }
  }

  async function translateRow(row: Row) {
    row.status = 'translating';
    try {
      const { text } = await $api<{ text: string }>('admin/translation/text', {
        method: 'POST',
        body: { sentence: row.sentence, source: row.source, context: row.context },
      });
      row.english = sanitiseHtml(text);
      row.status = 'translated';
    } catch (e) {
      row.status = 'failed';
      row.error = extractApiError(e, 'The provider returned an error');
    }
  }

  async function translate() {
    translating.value = true;
    const queue = toTranslate.value.filter((r) => r.status === 'pending' || r.status === 'failed');
    await Promise.all(
      Array.from({ length: CONCURRENCY }, async () => {
        for (let row = queue.shift(); row; row = queue.shift()) await translateRow(row);
      })
    );
    translating.value = false;
  }

  async function write() {
    writing.value = true;
    let written = 0;
    for (const row of ready.value) {
      try {
        await ankiConnect('updateNoteFields', { note: { id: row.noteId, fields: { [englishField.value]: escapeFieldText(row.english.trim()) } } });
        row.status = 'written';
        written++;
      } catch (e) {
        row.status = 'failed';
        row.error = extractApiError(e, 'AnkiConnect refused the update');
      }
    }
    writing.value = false;
    toast.add({ severity: 'success', summary: `Wrote English to ${written} notes`, life: 3000 });
  }
</script>

<template>
  <Card class="shadow-md">
    <template #title>Backfill English on Anki notes</template>
    <template #content>
      <p class="text-sm text-gray-600 dark:text-gray-300 mb-3">
        Translates the sentence of Anki notes that have none (Yomitan and GSM cards) with the AI provider, through AnkiConnect on this PC. Nothing is written
        until you press "Write to Anki". Lapis sample cards and fragments under 4 characters are skipped.
      </p>
      <div class="flex flex-col gap-3">
        <div>
          <label class="text-sm block mb-1" for="backfill-query">Anki search</label>
          <InputText id="backfill-query" v-model="query" class="w-full font-mono text-xs" />
        </div>
        <div class="flex flex-col sm:flex-row gap-3">
          <div>
            <label class="text-sm block mb-1" for="backfill-sentence">Sentence field</label>
            <InputText id="backfill-sentence" v-model="sentenceField" class="w-48" />
          </div>
          <div>
            <label class="text-sm block mb-1" for="backfill-english">English field</label>
            <InputText id="backfill-english" v-model="englishField" class="w-48" />
          </div>
        </div>
        <div class="flex flex-wrap gap-2">
          <Button label="Find notes" icon="pi pi-search" size="small" :loading="finding" @click="find" />
          <Button
            label="Translate"
            icon="pi pi-language"
            size="small"
            :loading="translating"
            :disabled="!toTranslate.length || !estimate?.provider || writing"
            @click="translate"
          />
          <Button
            :label="`Write ${ready.length} to Anki`"
            icon="pi pi-upload"
            size="small"
            severity="success"
            :loading="writing"
            :disabled="!ready.length || translating"
            @click="write"
          />
        </div>

        <Message v-if="estimate" severity="secondary" size="small">
          {{ toTranslate.length }} to translate, {{ skipped.length }} skipped.
          <template v-if="estimate.provider">
            {{ estimate.provider }} · {{ estimate.model }}
            <template v-if="estimate.estimatedCostUsd != null"> · about ${{ estimate.estimatedCostUsd.toFixed(2) }}</template>
          </template>
          <template v-else> No AI provider is set up.</template>
        </Message>

        <table v-if="rows.length" class="w-full text-sm">
          <thead>
            <tr class="text-left text-xs text-gray-500">
              <th class="py-1 pr-2">Sentence</th>
              <th class="py-1 pr-2">English</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="row in [...toTranslate, ...skipped]" :key="row.noteId" class="border-t border-surface-200 dark:border-surface-700 align-top">
              <td class="py-2 pr-2 w-1/2">
                <!-- eslint-disable-next-line vue/no-v-html -->
                <div lang="ja" v-html="sanitiseHtml(row.sentence)" />
                <div class="text-xs text-gray-500 mt-1">{{ row.source }}</div>
              </td>
              <td class="py-2">
                <span v-if="row.skip" class="text-xs text-gray-500">Skipped: {{ row.skip }}</span>
                <span v-else-if="row.status === 'pending'" class="text-xs text-gray-500">Not translated yet</span>
                <i v-else-if="row.status === 'translating'" class="pi pi-spin pi-spinner text-gray-500" />
                <span v-else-if="row.status === 'failed'" class="text-xs text-red-600">{{ row.error }}</span>
                <template v-else>
                  <InputText v-model="row.english" class="w-full text-sm" :disabled="row.status === 'written'" />
                  <span v-if="row.status === 'written'" class="text-xs text-green-600">Written</span>
                </template>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </template>
  </Card>
</template>
