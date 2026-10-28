<script setup lang="ts">
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import InputText from 'primevue/inputtext';
  import Message from 'primevue/message';
  import Paginator from 'primevue/paginator';
  import Tag from 'primevue/tag';
  import { useToast } from 'primevue/usetoast';
  import { ankiConnect } from '~/composables/useMiningCards';
  import { noteMedia, toImportNote, type AnkiNoteInfo, type ImportNote } from '~/utils/minedImport';
  import { sanitiseHtml } from '~/utils/sanitiseHtml';

  useHead({ title: 'Mined words - Jiten' });

  definePageMeta({
    middleware: ['auth'],
  });

  interface MinedWordSummary {
    minedWordId: number;
    wordId: number | null;
    readingIndex: number | null;
    expression: string;
    reading: string;
    sentences: number;
    notes: number;
    flagged: number;
    mainSentence: string | null;
    updatedAt: string;
  }

  interface MinedSentence {
    minedSentenceId: number;
    text: string;
    english: string | null;
    englishOrigin: string | null;
    origin: 'jiten' | 'game' | 'web' | 'other';
    exampleSentenceId: number | null;
    sourceTitle: string | null;
    sourceUrl: string | null;
    previousLine: string | null;
    minedAt: string | null;
    audioUrl: string | null;
    pictureUrl: string | null;
    isMain: boolean;
    sortOrder: number;
    flaggedAt: string | null;
  }

  interface MinedWord {
    minedWordId: number;
    wordId: number | null;
    readingIndex: number | null;
    expression: string;
    reading: string;
    notes: { ankiNoteId: number; modelName: string; deckName: string; importedAt: string }[];
    sentences: MinedSentence[];
  }

  interface ImportResult {
    ankiNoteId: number;
    status: 'imported' | 'already' | 'failed';
    minedWordId: number | null;
    matched: boolean;
    origin: string | null;
    error: string | null;
  }

  /** Notes per import request; media rides along as base64, so batches stay small. */
  const BATCH = 5;
  const PAGE_SIZE = 50;
  const ORIGIN_LABELS: Record<MinedSentence['origin'], string> = { jiten: 'Jiten', game: 'Game', web: 'Web', other: 'Other' };

  const { $api } = useNuxtApp();
  const toast = useToast();
  const { fetchAudio, fetchPicture } = useMiningCards();

  // Import
  const query = ref('"note:Lapis+"');
  const found = ref<number[] | null>(null);
  const finding = ref(false);
  const importing = ref(false);
  const progress = ref(0);
  const results = ref<ImportResult[]>([]);
  const summary = computed(() => ({
    imported: results.value.filter((r) => r.status === 'imported').length,
    already: results.value.filter((r) => r.status === 'already').length,
    unmatched: results.value.filter((r) => r.status === 'imported' && !r.matched).length,
    failed: results.value.filter((r) => r.status === 'failed'),
  }));

  async function find() {
    finding.value = true;
    found.value = null;
    results.value = [];
    try {
      const [ids, { ankiNoteIds }] = await Promise.all([
        ankiConnect<number[]>('findNotes', { query: query.value }),
        $api<{ ankiNoteIds: number[] }>('mined/notes'),
      ]);
      // Notes imported before aren't read again (their media would be fetched for nothing).
      const known = new Set(ankiNoteIds);
      found.value = ids.filter((id) => !known.has(id));
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't read Anki", detail: extractApiError(e, 'Is Anki open with AnkiConnect?'), life: 6000 });
    } finally {
      finding.value = false;
    }
  }

  async function buildBatch(ids: number[]): Promise<ImportNote[]> {
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

  async function runImport() {
    if (!found.value?.length) return;
    importing.value = true;
    progress.value = 0;
    results.value = [];
    try {
      for (let i = 0; i < found.value.length; i += BATCH) {
        const notes = await buildBatch(found.value.slice(i, i + BATCH));
        const response = await $api<{ results: ImportResult[] }>('mined/import', { method: 'POST', body: { notes } });
        results.value.push(...response.results);
        progress.value = Math.min(i + BATCH, found.value.length);
      }
      found.value = [];
      await loadWords();
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Import stopped', detail: extractApiError(e, 'The import failed'), life: 8000 });
    } finally {
      importing.value = false;
    }
  }

  /** Sync's first step: import whatever the search finds that isn't in the store yet. */
  async function importNew() {
    await find();
    await runImport();
  }

  // Words
  const search = ref('');
  const page = ref(0);
  const words = ref<MinedWordSummary[]>([]);
  const total = ref(0);
  const loading = ref(false);
  const open = ref<Record<number, MinedWord | 'loading' | undefined>>({});

  async function loadWords() {
    loading.value = true;
    try {
      const data = await $api<{ words: MinedWordSummary[]; total: number }>('mined/words', { query: { search: search.value || undefined, page: page.value } });
      words.value = data.words;
      total.value = data.total;
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, "Couldn't load the words"), life: 5000 });
    } finally {
      loading.value = false;
    }
  }

  onMounted(loadWords);

  let searchTimer: ReturnType<typeof setTimeout> | undefined;
  watch(search, () => {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => {
      page.value = 0;
      loadWords();
    }, 300);
  });

  async function toggle(id: number) {
    if (open.value[id]) {
      open.value[id] = undefined;
      return;
    }
    open.value[id] = 'loading';
    try {
      open.value[id] = await $api<MinedWord>(`mined/words/${id}`);
    } catch (e) {
      open.value[id] = undefined;
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, "Couldn't load the word"), life: 5000 });
    }
  }

  // Jiten sentences have no stored media: it's cut from the episode when asked for.
  const cutMedia = ref<Record<number, { audio?: string; picture?: string }>>({});

  async function playAudio(s: MinedSentence) {
    let url = s.audioUrl ?? cutMedia.value[s.minedSentenceId]?.audio;
    if (!url && s.exampleSentenceId) {
      try {
        url = URL.createObjectURL(await fetchAudio(s.exampleSentenceId));
        cutMedia.value[s.minedSentenceId] = { ...cutMedia.value[s.minedSentenceId], audio: url };
      } catch (e) {
        toast.add({ severity: 'warn', summary: 'No audio', detail: extractApiError(e, "Couldn't cut the audio"), life: 4000 });
        return;
      }
    }
    if (url) new Audio(url).play();
  }

  async function showPicture(s: MinedSentence) {
    if (!s.exampleSentenceId) return;
    try {
      const url = URL.createObjectURL(await fetchPicture(s.exampleSentenceId));
      cutMedia.value[s.minedSentenceId] = { ...cutMedia.value[s.minedSentenceId], picture: url };
    } catch (e) {
      toast.add({ severity: 'warn', summary: 'No picture', detail: extractApiError(e, "Couldn't cut the picture"), life: 4000 });
    }
  }

  function pictureOf(s: MinedSentence) {
    return s.pictureUrl ?? cutMedia.value[s.minedSentenceId]?.picture ?? null;
  }

  function hasAudio(s: MinedSentence) {
    return !!s.audioUrl || !!s.exampleSentenceId;
  }
</script>

<template>
  <div class="container mx-auto p-4 max-w-4xl">
    <h1 class="text-3xl font-bold mb-4">Mined words</h1>

    <Card class="shadow-md mb-6">
      <template #title>Import from Anki</template>
      <template #content>
        <p class="text-sm text-gray-600 dark:text-gray-300 mb-3">
          Reads your mining notes through AnkiConnect (on this PC) and stores each note's sentence under its word. GSM's audio and screenshots are copied into
          Jiten; Jiten cards keep cutting theirs from the episode. Notes imported before are skipped.
        </p>
        <div class="flex flex-col sm:flex-row gap-2">
          <InputText v-model="query" class="flex-1 font-mono text-xs" aria-label="Anki search" />
          <Button label="Find notes" icon="pi pi-search" size="small" :loading="finding" :disabled="importing" @click="find" />
          <Button
            :label="found ? `Import ${found.length} new notes` : 'Import'"
            icon="pi pi-download"
            size="small"
            :loading="importing"
            :disabled="!found?.length"
            @click="runImport"
          />
        </div>
        <Message v-if="importing" severity="secondary" size="small" class="mt-3">Importing {{ progress }} / {{ found?.length }}…</Message>
        <Message v-else-if="results.length" severity="success" size="small" class="mt-3">
          {{ summary.imported }} imported ({{ summary.unmatched }} without a dictionary match), {{ summary.already }} already imported<template
            v-if="summary.failed.length"
            >, {{ summary.failed.length }} failed</template
          >.
          <ul v-if="summary.failed.length" class="text-xs mt-1">
            <li v-for="f in summary.failed" :key="f.ankiNoteId">Note {{ f.ankiNoteId }}: {{ f.error }}</li>
          </ul>
        </Message>
      </template>
    </Card>

    <MinedAnkiSync :import-new="importNew" @changed="loadWords" />

    <div class="flex items-center gap-3 mb-3">
      <InputText v-model="search" placeholder="Search words" class="w-full sm:w-72" lang="ja" />
      <span class="text-sm text-gray-500">{{ total }} words</span>
    </div>

    <div v-if="loading && !words.length" class="h-40 rounded bg-surface-100 dark:bg-surface-800 animate-pulse" />
    <p v-else-if="!words.length" class="text-gray-500">No mined words yet. Import your Anki notes above.</p>

    <div class="flex flex-col gap-2">
      <div v-for="w in words" :key="w.minedWordId" class="border border-surface-200 dark:border-surface-700 rounded">
        <button class="w-full text-left p-3 flex items-start gap-3" @click="toggle(w.minedWordId)">
          <i class="pi mt-1 text-gray-400" :class="open[w.minedWordId] ? 'pi-chevron-down' : 'pi-chevron-right'" />
          <div class="flex-1 min-w-0">
            <div class="flex items-baseline gap-2 flex-wrap">
              <span class="text-xl font-semibold" lang="ja">{{ w.expression }}</span>
              <span class="text-gray-500" lang="ja">{{ w.reading }}</span>
              <Tag v-if="w.wordId == null" value="No dictionary match" severity="warn" class="text-xs" />
            </div>
            <!-- eslint-disable-next-line vue/no-v-html -->
            <div v-if="w.mainSentence" class="text-sm text-gray-600 dark:text-gray-300 truncate" lang="ja" v-html="sanitiseHtml(w.mainSentence)" />
          </div>
          <div class="text-xs text-gray-500 text-right shrink-0">
            <div>{{ w.sentences }} sentence{{ w.sentences === 1 ? '' : 's' }}</div>
            <div v-if="w.notes > 1" class="text-orange-600">{{ w.notes }} Anki notes</div>
            <div v-if="w.flagged" class="text-red-600">{{ w.flagged }} flagged</div>
          </div>
        </button>

        <div v-if="open[w.minedWordId]" class="border-t border-surface-200 dark:border-surface-700 p-3">
          <i v-if="open[w.minedWordId] === 'loading'" class="pi pi-spin pi-spinner text-gray-500" />
          <template v-else>
            <div class="flex flex-col gap-3">
              <div v-for="s in (open[w.minedWordId] as MinedWord).sentences" :key="s.minedSentenceId" class="flex gap-3">
                <img v-if="pictureOf(s)" :src="pictureOf(s)!" alt="" class="w-32 h-auto rounded shrink-0 object-cover" />
                <button
                  v-else-if="s.exampleSentenceId"
                  class="w-32 h-18 rounded shrink-0 bg-surface-100 dark:bg-surface-800 text-xs text-gray-500"
                  @click="showPicture(s)"
                >
                  Show picture
                </button>
                <div class="flex-1 min-w-0">
                  <div class="flex items-start gap-2">
                    <Button v-if="hasAudio(s)" icon="pi pi-volume-up" size="small" text rounded aria-label="Play" @click="playAudio(s)" />
                    <!-- eslint-disable-next-line vue/no-v-html -->
                    <div class="text-lg" lang="ja" v-html="sanitiseHtml(s.text)" />
                  </div>
                  <!-- eslint-disable-next-line vue/no-v-html -->
                  <div v-if="s.english" class="text-sm text-gray-600 dark:text-gray-300" v-html="sanitiseHtml(s.english)" />
                  <div class="flex flex-wrap items-center gap-2 mt-1 text-xs text-gray-500">
                    <Tag :value="ORIGIN_LABELS[s.origin]" severity="secondary" />
                    <Tag v-if="s.isMain" value="Main" severity="info" />
                    <Tag v-if="s.flaggedAt" value="Flagged" severity="danger" />
                    <a v-if="s.sourceUrl && s.origin === 'web'" :href="s.sourceUrl" target="_blank" rel="noopener" class="underline">{{ s.sourceTitle }}</a>
                    <span v-else-if="s.sourceTitle">{{ s.sourceTitle }}</span>
                    <span v-if="s.minedAt">{{ new Date(s.minedAt).toLocaleDateString() }}</span>
                  </div>
                </div>
              </div>
              <p v-if="!(open[w.minedWordId] as MinedWord).sentences.length" class="text-sm text-gray-500">No sentences.</p>
              <div class="text-xs text-gray-500">
                Anki notes:
                <span v-for="(n, i) in (open[w.minedWordId] as MinedWord).notes" :key="n.ankiNoteId"
                  >{{ i ? ', ' : '' }}{{ n.deckName }} ({{ n.ankiNoteId }})</span
                >
              </div>
            </div>
          </template>
        </div>
      </div>
    </div>

    <Paginator
      v-if="total > PAGE_SIZE"
      class="mt-4"
      :rows="PAGE_SIZE"
      :total-records="total"
      :first="page * PAGE_SIZE"
      @page="
        (e) => {
          page = e.page;
          loadWords();
        }
      "
    />
  </div>
</template>
