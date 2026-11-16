<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';
  import type { ExampleSentence, WordSummary } from '~/types';
  import {
    MAX_RUN_SENTENCES_PER_WORD,
    WHEN_ASKED_LABELS,
    pipelineCanAsk,
    tallyLine,
    type MineSearchRun,
    type WhenAsked,
  } from '~/utils/mineSearches';
  import { hasAnkiStep } from '~/utils/pipelines';

  /**
   * "Run on all results" (GDR-059): a preview of what the Mine search would mine, then a run of one pipeline over the top
   * sentences of every word, in this page, with progress and a summary. A saved search keeps the run settings.
   */
  const props = defineProps<{
    /** The page's search as a `MineRequest` body, with the words read from Anki; null when it can't be built (Anki unreachable). */
    searchBody: () => Promise<Record<string, unknown> | null>;
    /** The saved search the page has chosen, if any; its run settings are used and saved back. */
    savedSearchId: string | null;
  }>();

  const visible = defineModel<boolean>('visible', { required: true });
  /** The run ended; `words` got a card in it. */
  const emit = defineEmits<{ finished: [words: WordSummary[]] }>();

  interface PlanItem {
    word: WordSummary;
    sentences: ExampleSentence[];
  }
  interface Plan {
    items: PlanItem[];
    totalWords: number;
    sentences: number;
    capped: boolean;
    rankSource: string;
  }

  const { $api } = useNuxtApp();
  const toast = useToast();
  const pipelines = usePipelines();
  const saved = useMineSearches();
  const runner = useMineRun();

  const ordered = computed(() => pipelines.forPage('mine'));
  const pipelineId = ref<string | null>(null);
  const perWord = ref(1);
  const whenAsked = ref<WhenAsked>('existing');
  const pipeline = computed(() => ordered.value.find((p) => p.id === pipelineId.value) ?? ordered.value[0] ?? null);
  const canAsk = computed(() => !!pipeline.value && pipelineCanAsk(pipeline.value.steps));
  // Any Anki step asks when only another entry of the word's spelling has a card (GDR-061).
  const showWhenAsked = computed(() => !!pipeline.value && hasAnkiStep(pipeline.value));
  const whenAskedOptions = (Object.keys(WHEN_ASKED_LABELS) as WhenAsked[]).map((value) => ({ value, label: WHEN_ASKED_LABELS[value] }));
  const perWordOptions = Array.from({ length: MAX_RUN_SENTENCES_PER_WORD }, (_, i) => ({ value: i + 1, label: String(i + 1) }));
  const savedSearch = computed(() => saved.searches.value.find((s) => s.id === props.savedSearchId) ?? null);
  /** The saved search names a pipeline that no longer exists. */
  const missingPipeline = ref(false);

  const plan = ref<Plan | null>(null);
  const planLoading = ref(false);
  const planError = ref<string | null>(null);
  const phase = ref<'preview' | 'running' | 'done'>('preview');
  let planRequest = 0;

  async function loadPlan() {
    const id = ++planRequest;
    planLoading.value = true;
    planError.value = null;
    try {
      const search = await props.searchBody();
      if (id !== planRequest) return;
      if (!search) {
        planError.value = "Couldn't read your Anki words for this search's Anki filters. Is Anki open?";
        plan.value = null;
        return;
      }
      const result = await $api<Plan>('mining/sentences/all', { method: 'POST', body: { search, perWord: perWord.value } });
      if (id === planRequest) plan.value = result;
    } catch (e) {
      if (id === planRequest) planError.value = (e as { data?: { message?: string } })?.data?.message ?? "Couldn't load the results.";
    } finally {
      if (id === planRequest) planLoading.value = false;
    }
  }

  // Opening sets perWord from the saved search; the open handler loads the plan once itself.
  let opening = false;

  watch(visible, async (open) => {
    if (!open) return;
    opening = true;
    phase.value = 'preview';
    plan.value = null;
    try {
      await pipelines.load();
    } catch {
      toast.add({ severity: 'error', summary: "Couldn't load your pipelines", life: 5000, group: 'bottom' });
    }
    const run = savedSearch.value?.run;
    missingPipeline.value = !!run?.pipelineId && !ordered.value.some((p) => p.id === run.pipelineId);
    pipelineId.value = run?.pipelineId && !missingPipeline.value ? run.pipelineId : (ordered.value[0]?.id ?? null);
    perWord.value = run?.perWord ?? 1;
    whenAsked.value = run?.whenAsked ?? 'existing';
    await nextTick();
    opening = false;
    loadPlan();
  });

  watch(perWord, () => !opening && visible.value && phase.value === 'preview' && loadPlan());

  /** Keeps the run settings on the chosen saved search, so running it again is two clicks. */
  async function saveRunSettings() {
    const search = savedSearch.value;
    if (!search || !pipeline.value) return;
    const run: MineSearchRun = { pipelineId: pipeline.value.id, perWord: perWord.value, whenAsked: whenAsked.value };
    const old = search.run;
    if (old && old.pipelineId === run.pipelineId && old.perWord === run.perWord && old.whenAsked === run.whenAsked) return;
    await saved.update((list) => list.map((s) => (s.id === search.id ? { ...s, run } : s))).catch(() => {});
  }

  async function start() {
    if (!plan.value || !pipeline.value) return;
    phase.value = 'running';
    saveRunSettings();
    await runner.run(plan.value.items, pipeline.value, whenAsked.value);
    phase.value = 'done';
    emit('finished', [...runner.cardedWords.value]);
  }

  const problemGroups = computed(() =>
    [
      { title: 'Failed', items: runner.tally.value.failed },
      { title: 'Fields left empty', items: runner.tally.value.warned },
      { title: 'Another word spelt the same has the card', items: runner.tally.value.otherEntry },
    ].filter((g) => g.items.length)
  );

  const percent = computed(() => (runner.total.value ? Math.round((runner.done.value / runner.total.value) * 100) : 0));

  // A run lives in this page (INV-002): leaving stops it, so ask first.
  function beforeUnload(e: BeforeUnloadEvent) {
    if (runner.running.value) e.preventDefault();
  }
  onMounted(() => window.addEventListener('beforeunload', beforeUnload));
  onBeforeUnmount(() => {
    window.removeEventListener('beforeunload', beforeUnload);
    runner.stop();
  });
  onBeforeRouteLeave(() => {
    if (!runner.running.value) return true;
    if (!window.confirm('A run is still going. Leave and stop it?')) return false;
    runner.stop();
    return true;
  });
</script>

<template>
  <Dialog
    v-model:visible="visible"
    header="Run on all results"
    modal
    :closable="phase !== 'running'"
    :close-on-escape="phase !== 'running'"
    :style="{ width: '36rem' }"
    :breakpoints="{ '640px': '96vw' }"
  >
    <div v-if="phase === 'preview'" class="flex flex-col gap-4">
      <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <label class="flex flex-col gap-1 sm:col-span-2">
          <span class="text-sm font-medium">Pipeline</span>
          <Select v-model="pipelineId" :options="ordered" option-label="name" option-value="id" placeholder="Loading…" class="w-full" />
          <small v-if="missingPipeline" class="text-amber-600 dark:text-amber-400">
            This search's pipeline no longer exists, so the Mine page's default is chosen.
          </small>
          <small v-else-if="pipeline && !hasAnkiStep(pipeline)" class="text-gray-500 dark:text-gray-400">
            No Anki step: the sentences are only saved to their words' lists.
          </small>
        </label>
        <label class="flex flex-col gap-1">
          <span class="text-sm font-medium">Sentences per word</span>
          <Select v-model="perWord" :options="perWordOptions" option-label="label" option-value="value" class="w-full" />
        </label>
        <label v-if="showWhenAsked" class="flex flex-col gap-1">
          <span class="text-sm font-medium">{{ canAsk ? 'If the word, or another word spelt the same, has a card' : 'If only another word spelt the same has a card' }}</span>
          <Select v-model="whenAsked" :options="whenAskedOptions" option-label="label" option-value="value" class="w-full" />
        </label>
      </div>

      <div class="rounded border border-surface-200 dark:border-surface-700 p-3 text-sm">
        <div v-if="planLoading" class="flex items-center gap-2 text-gray-500 dark:text-gray-400">
          <i class="pi pi-spin pi-spinner" /> Finding the results…
        </div>
        <p v-else-if="planError" class="text-red-600 dark:text-red-400">{{ planError }}</p>
        <template v-else-if="plan">
          <p class="font-medium">
            {{ plan.items.length.toLocaleString() }} {{ plan.items.length === 1 ? 'word' : 'words' }} · {{ plan.sentences.toLocaleString() }}
            {{ plan.sentences === 1 ? 'sentence' : 'sentences' }}
          </p>
          <p v-if="plan.capped" class="text-amber-600 dark:text-amber-400 mt-1">
            The search has {{ plan.totalWords.toLocaleString() }} words; a run takes the first {{ plan.items.length.toLocaleString() }} in its order.
          </p>
          <p v-if="plan.items.length" class="text-gray-600 dark:text-gray-300 mt-2" lang="ja">
            {{ plan.items.slice(0, 10).map((i) => i.word.reading).join('、') }}<span v-if="plan.items.length > 10">…</span>
          </p>
          <p class="text-gray-500 dark:text-gray-400 mt-2">
            Sentences already on their word's card are skipped. A word gets one note: its later sentences go to the card it got first.
          </p>
        </template>
      </div>

      <div class="flex justify-end gap-2">
        <Button label="Cancel" severity="secondary" text @click="visible = false" />
        <Button
          label="Run"
          icon="pi pi-play"
          :disabled="!plan || plan.items.length === 0 || !pipeline || planLoading"
          @click="start"
        />
      </div>
    </div>

    <div v-else class="flex flex-col gap-3">
      <ProgressBar :value="percent" :show-value="false" style="height: 0.5rem" />
      <p class="text-sm tabular-nums">
        {{ runner.done.value.toLocaleString() }} of {{ runner.total.value.toLocaleString() }} sentences
        <span v-if="runner.current.value" class="text-gray-500 dark:text-gray-400" lang="ja"> · {{ runner.current.value }}</span>
      </p>
      <p class="text-sm">{{ tallyLine(runner.tally.value) }}</p>
      <p v-if="runner.stoppedBy.value" class="text-sm text-amber-600 dark:text-amber-400">{{ runner.stoppedBy.value }}</p>
      <div v-for="group in problemGroups" :key="group.title" class="text-sm">
        <p class="font-medium">{{ group.title }}</p>
        <ul class="max-h-48 overflow-y-auto flex flex-col gap-1 mt-1">
          <li v-for="(f, i) in group.items" :key="i">
            <NuxtLink :to="`/vocabulary/${f.wordId}/${f.readingIndex}`" target="_blank" class="text-primary-600 dark:text-primary-400 hover:underline" lang="ja">
              {{ f.word }}
            </NuxtLink>
            <span class="text-gray-600 dark:text-gray-300">: {{ f.reason }}</span>
          </li>
        </ul>
      </div>
      <div class="flex justify-end gap-2">
        <Button v-if="phase === 'running'" label="Stop" severity="secondary" outlined @click="runner.stop()" />
        <Button v-else label="Close" @click="visible = false" />
      </div>
    </div>
  </Dialog>
</template>
