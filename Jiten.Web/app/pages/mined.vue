<script setup lang="ts">
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import Checkbox from 'primevue/checkbox';
  import Dialog from 'primevue/dialog';
  import InputText from 'primevue/inputtext';
  import Message from 'primevue/message';
  import Paginator from 'primevue/paginator';
  import Tag from 'primevue/tag';
  import { useToast } from 'primevue/usetoast';
  import { ankiConnect } from '~/composables/useAnkiConnect';
  import type { MinedImportResult } from '~/composables/useMinedImport';
  import { sanitiseHtml } from '~/utils/sanitiseHtml';
  import { setupQuery } from '~/utils/ankiSetups';

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
    /** Notes whose word was guessed among entries sharing its spelling. */
    guessed: number;
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
    flagged: boolean;
  }

  interface MinedNote {
    ankiNoteId: number;
    modelName: string;
    deckName: string;
    importedAt: string;
    /** How the note's word was chosen: `guessed` needs a look; null until Sync checks it. */
    wordMatch: string | null;
  }

  interface WordCandidate {
    wordId: number;
    readingIndex: number;
    glosses: string[];
    /** How the entry is written, kanji forms first. */
    forms: string[];
  }

  interface MinedWord {
    minedWordId: number;
    wordId: number | null;
    readingIndex: number | null;
    expression: string;
    reading: string;
    notes: MinedNote[];
    sentences: MinedSentence[];
  }

  const PAGE_SIZE = 50;
  const ORIGIN_LABELS: Record<MinedSentence['origin'], string> = { jiten: 'Jiten', game: 'Game', web: 'Web', other: 'Other' };

  const { $api } = useNuxtApp();
  const toast = useToast();
  const { fetchAudio, fetchPicture, fetchVideo } = useMiningCards();
  const { importNotes } = useMinedImport();
  const flagList = ref<{ load: () => Promise<void> } | null>(null);

  // Import
  const query = ref('');
  const { setups: ankiSetups, load: loadSetups } = useAnkiSetups();
  // The notes Sync imports, as a starting search.
  onMounted(async () => {
    await loadSetups().catch(() => {});
    if (!query.value)
      query.value = ankiSetups.value
        .filter((s) => s.importSentences)
        .map((s) => `(${setupQuery(s)})`)
        .join(' OR ');
  });
  const found = ref<number[] | null>(null);
  const finding = ref(false);
  const importing = ref(false);
  const progress = ref(0);
  const results = ref<MinedImportResult[]>([]);
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

  async function runImport() {
    if (!found.value?.length) return;
    importing.value = true;
    progress.value = 0;
    results.value = [];
    try {
      await importNotes(found.value, (batch, done) => {
        results.value.push(...batch);
        progress.value = done;
      });
      found.value = [];
      await loadWords();
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Import stopped', detail: extractApiError(e, 'The import failed'), life: 8000 });
    } finally {
      importing.value = false;
    }
  }

  /** Flags change the word list's counts, and any word that's open. */
  async function refresh() {
    open.value = {};
    await Promise.all([loadWords(), flagList.value?.load()]);
  }

  const flagging = ref<MinedSentence | null>(null);

  // Words
  const search = ref('');
  const guessedOnly = ref(false);
  const page = ref(0);
  const words = ref<MinedWordSummary[]>([]);
  const total = ref(0);
  const loading = ref(false);
  const open = ref<Record<number, MinedWord | 'loading' | undefined>>({});

  async function loadWords() {
    loading.value = true;
    try {
      const data = await $api<{ words: MinedWordSummary[]; total: number }>('mined/words', {
        query: { search: search.value || undefined, page: page.value, guessed: guessedOnly.value || undefined },
      });
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

  watch(guessedOnly, () => {
    page.value = 0;
    loadWords();
  });

  /** Sync's fold dialog asks to check a word first: list it, open. */
  async function checkWord(expression: string) {
    guessedOnly.value = false;
    search.value = expression;
    clearTimeout(searchTimer);
    page.value = 0;
    await loadWords();
    const first = words.value.find((w) => w.expression === expression);
    if (first && !open.value[first.minedWordId]) await toggle(first.minedWordId);
  }

  // Which dictionary entry a note's word is
  const changing = ref<{ note: MinedNote; expression: string; current: number | null; candidates: WordCandidate[] | null } | null>(null);
  const settingWord = ref(false);

  async function changeWord(note: MinedNote, word: MinedWord) {
    changing.value = { note, expression: word.expression, current: word.wordId, candidates: null };
    try {
      const { candidates } = await $api<{ candidates: WordCandidate[] }>(`mined/notes/${note.ankiNoteId}/candidates`);
      if (changing.value?.note === note) changing.value.candidates = candidates;
    } catch (e) {
      changing.value = null;
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(e, "Couldn't load the dictionary entries"), life: 5000 });
    }
  }

  /** Puts the note on the entry (or confirms it), then shows the lists as they now are. */
  async function setWord(note: MinedNote, wordId: number, readingIndex: number) {
    settingWord.value = true;
    try {
      const { moved } = await $api<{ moved: boolean }>(`mined/notes/${note.ankiNoteId}/word`, { method: 'POST', body: { wordId, readingIndex } });
      changing.value = null;
      if (moved)
        toast.add({
          severity: 'success',
          summary: 'Moved',
          detail: 'The note and its sentence are on the other word now. Sync writes both notes.',
          life: 5000,
        });
      await refresh();
    } catch (e) {
      toast.add({ severity: 'error', summary: "Couldn't change the word", detail: extractApiError(e, 'The change failed'), life: 6000 });
    } finally {
      settingWord.value = false;
    }
  }

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
  const cutMedia = ref<Record<number, { audio?: string; picture?: string; video?: string }>>({});

  async function playAudio(s: MinedSentence) {
    let url = s.audioUrl ?? cutMedia.value[s.minedSentenceId]?.audio;
    if (!url && canCut(s)) {
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

  /** Cuts the clip with sound and shows it in the picture's place; the screenshot instead when the clip can't be cut. */
  async function showClip(s: MinedSentence) {
    if (!canCut(s)) return;
    try {
      const url = URL.createObjectURL(await fetchVideo(s.exampleSentenceId));
      cutMedia.value[s.minedSentenceId] = { ...cutMedia.value[s.minedSentenceId], video: url };
      return;
    } catch {
      // The screenshot below, or why there's neither
    }
    try {
      const url = URL.createObjectURL(await fetchPicture(s.exampleSentenceId));
      cutMedia.value[s.minedSentenceId] = { ...cutMedia.value[s.minedSentenceId], picture: url };
    } catch (e) {
      toast.add({ severity: 'warn', summary: 'No clip', detail: extractApiError(e, "Couldn't cut the clip"), life: 4000 });
    }
  }

  function clipOf(s: MinedSentence) {
    return cutMedia.value[s.minedSentenceId]?.video ?? null;
  }

  function pictureOf(s: MinedSentence) {
    return s.pictureUrl ?? cutMedia.value[s.minedSentenceId]?.picture ?? null;
  }

  /** Only Jiten sentences are cut from an episode; the others have their stored copies or nothing. */
  function canCut(s: MinedSentence): s is MinedSentence & { exampleSentenceId: number } {
    return s.origin === 'jiten' && !!s.exampleSentenceId;
  }

  function hasAudio(s: MinedSentence) {
    return !!s.audioUrl || canCut(s);
  }
</script>

<template>
  <div class="container mx-auto p-4 max-w-4xl">
    <h1 class="text-3xl font-bold mb-4">Mined words</h1>

    <Card class="shadow-md mb-6">
      <template #title>Import from Anki</template>
      <template #content>
        <p class="text-sm text-gray-600 dark:text-gray-300 mb-3">
          Reads notes through AnkiConnect (on this PC) and their note type's field roles, and stores each note's sentence under its word. Sync does this for
          you; this imports from any search. GSM's audio and screenshots are copied into Jiten; Jiten cards keep cutting theirs from the episode. Notes imported
          before are skipped.
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

    <MinedAnkiSync @changed="refresh" @check="checkWord" />

    <SentenceFlagList ref="flagList" @changed="refresh" />

    <div class="flex items-center gap-3 mb-3">
      <InputText v-model="search" placeholder="Search words" class="w-full sm:w-72" lang="ja" />
      <label class="flex items-center gap-2 text-sm shrink-0"><Checkbox v-model="guessedOnly" binary /> Guessed only</label>
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
            <div v-if="w.guessed" class="text-orange-600">guessed word</div>
          </div>
        </button>

        <div v-if="open[w.minedWordId]" class="border-t border-surface-200 dark:border-surface-700 p-3">
          <i v-if="open[w.minedWordId] === 'loading'" class="pi pi-spin pi-spinner text-gray-500" />
          <template v-else>
            <div class="flex flex-col gap-3">
              <div v-for="s in (open[w.minedWordId] as MinedWord).sentences" :key="s.minedSentenceId" class="flex gap-3">
                <video v-if="clipOf(s)" :src="clipOf(s)!" controls autoplay playsinline class="w-48 h-auto rounded shrink-0 bg-black" />
                <img v-else-if="pictureOf(s)" :src="pictureOf(s)!" alt="" class="w-32 h-auto rounded shrink-0 object-cover" />
                <button v-else-if="canCut(s)" class="w-32 h-18 rounded shrink-0 bg-surface-100 dark:bg-surface-800 text-xs text-gray-500" @click="showClip(s)">
                  <i class="pi pi-play text-xs mr-1" aria-hidden="true" />Play clip
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
                    <Tag v-if="s.flagged" value="Flagged" severity="danger" />
                    <button v-else-if="s.exampleSentenceId" class="underline hover:text-red-500" @click="flagging = s">Flag</button>
                    <a v-if="s.sourceUrl && s.origin === 'web'" :href="s.sourceUrl" target="_blank" rel="noopener" class="underline">{{ s.sourceTitle }}</a>
                    <span v-else-if="s.sourceTitle">{{ s.sourceTitle }}</span>
                    <span v-if="s.minedAt">{{ new Date(s.minedAt).toLocaleDateString() }}</span>
                  </div>
                </div>
              </div>
              <p v-if="!(open[w.minedWordId] as MinedWord).sentences.length" class="text-sm text-gray-500">No sentences.</p>
              <div class="text-xs text-gray-500">
                Anki notes:
                <div v-for="n in (open[w.minedWordId] as MinedWord).notes" :key="n.ankiNoteId" class="flex flex-wrap items-center gap-2 mt-1">
                  <span>{{ n.deckName }} ({{ n.ankiNoteId }})</span>
                  <Tag v-if="n.wordMatch === 'guessed'" value="Guessed word" severity="warn" class="text-xs" />
                  <button
                    v-if="n.wordMatch === 'guessed' && w.wordId != null"
                    class="underline hover:text-primary"
                    :disabled="settingWord"
                    @click="setWord(n, w.wordId, w.readingIndex ?? 0)"
                  >
                    Looks right
                  </button>
                  <button class="underline hover:text-primary" @click="changeWord(n, open[w.minedWordId] as MinedWord)">Change word</button>
                </div>
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

    <Dialog
      :visible="!!changing"
      modal
      :header="changing ? `Which ${changing.expression} is note ${changing.note.ankiNoteId}?` : ''"
      class="w-[36rem] max-w-[95vw]"
      @update:visible="(v: boolean) => !v && (changing = null)"
    >
      <template v-if="changing">
        <i v-if="!changing.candidates" class="pi pi-spin pi-spinner text-gray-500" />
        <p v-else-if="changing.candidates.length < 2" class="text-sm text-gray-500">No other dictionary entry is written this way.</p>
        <div v-else class="flex flex-col gap-2">
          <p class="text-sm text-gray-500">
            The note and the sentence that came from it move to the entry you pick. Sentences you mined onto the word later stay where they are.
          </p>
          <button
            v-for="c in changing.candidates"
            :key="c.wordId"
            class="text-left border rounded p-2 hover:border-primary disabled:opacity-50"
            :class="c.wordId === changing.current ? 'border-primary bg-primary-50 dark:bg-primary-950' : 'border-surface-200 dark:border-surface-700'"
            :disabled="settingWord"
            @click="setWord(changing.note, c.wordId, c.readingIndex)"
          >
            <div class="font-semibold" lang="ja">{{ c.forms.slice(0, 4).join('・') }}</div>
            <div class="text-sm">{{ c.glosses.slice(0, 6).join('; ') || 'No English' }}</div>
            <div class="text-xs text-gray-500">Entry {{ c.wordId }}<template v-if="c.wordId === changing.current"> · on now</template></div>
          </button>
        </div>
      </template>
    </Dialog>

    <SentenceFlagDialog v-if="flagging" :sentence-id="flagging.exampleSentenceId!" :text="flagging.text" @close="flagging = null" @saved="refresh" />
  </div>
</template>
