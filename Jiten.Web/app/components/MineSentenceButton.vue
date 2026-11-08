<script setup lang="ts">
  import Button from 'primevue/button';
  import Dialog from 'primevue/dialog';
  import Menu from 'primevue/menu';
  import { useToast } from 'primevue/usetoast';
  import type { ExampleSentence, WordSummary } from '~/types';
  import type { MineOutcome } from '~/composables/useMineSentence';
  import type { ListSentenceState } from '~/composables/useSentenceList';
  import { hasAnkiStep, type Pipeline, type PipelinePage } from '~/utils/pipelines';

  /**
   * The sentence button (n1miner): saves the sentence to its word's list, then runs a pipeline's steps. The button runs
   * the page's default pipeline; ▾ lists every pipeline (Settings › Pipelines). The card is for the given word, or for
   * one picked from the sentence.
   */
  const props = defineProps<{
    sentence: ExampleSentence;
    /** The sentence's own word (its wordPosition/wordLength); without it the user picks one. */
    wordId?: number;
    readingIndex?: number;
    /** Round, padded, like the page's other icon buttons (the Mine page's row actions). */
    boxed?: boolean;
    /** Which page's default pipeline the button runs. */
    page?: PipelinePage;
  }>();

  const emit = defineEmits<{
    /** Added to Anki, for the word given or picked. */
    mined: [word: WordSummary, outcome: MineOutcome];
  }>();

  interface ParsedWord {
    wordId: number;
    readingIndex: number;
    originalText: string;
  }

  const { $api } = useNuxtApp();
  const toast = useToast();
  const { configured } = useMiningCards();
  const { mine: mineSentence, choice, answer } = useMineSentence();
  const list = useSentenceList();
  const pipelines = usePipelines();
  const ordered = computed(() => pipelines.forPage(props.page ?? 'word'));
  const fallback: Pipeline = { id: 'keep', name: 'Keep for Jiten', steps: [] };

  const working = ref(false);
  const settingsVisible = ref(false);
  const picking = ref<{ word: ParsedWord; position: number }[] | null>(null);
  let pickingFor: Pipeline = fallback;
  const menu = ref<InstanceType<typeof Menu> | null>(null);

  const hasOwnWord = computed(() => props.wordId != null && props.readingIndex != null && props.sentence.wordLength > 0);
  /** For a word picked from the sentence, what this button did to it (the row isn't tied to one word's list). */
  const pickedState = ref<ListSentenceState>();
  const listState = computed(() => (hasOwnWord.value ? list.stateOf(props.wordId, props.readingIndex, props.sentence.sentenceId) : pickedState.value));
  const canSave = computed(() => props.sentence.sentenceId > 0);

  // Rows are reused for other sentences (lists keyed by index), so this follows the sentence, not the mount.
  onMounted(() => {
    pipelines.load().catch(() => {});
    watch(
      () => [props.wordId, props.readingIndex, props.sentence.sentenceId, props.sentence.wordLength] as const,
      () => {
        pickedState.value = undefined;
        if (hasOwnWord.value && canSave.value) list.watch(props.wordId!, props.readingIndex!, props.sentence.sentenceId);
      },
      { immediate: true }
    );
  });

  /** The pipeline the button runs; until the pipelines load, it only saves. */
  const defaultPipeline = computed(() => ordered.value[0] ?? fallback);
  /**
   * Whether running the pipeline can still do something for the row: not on a card yet, and either Anki steps or a line
   * of Jiten's not yet in the list to save.
   */
  function canRun(p: Pipeline) {
    return listState.value !== 'card' && (hasAnkiStep(p) || (canSave.value && listState.value == null));
  }

  const title = computed(() =>
    listState.value === 'card'
      ? 'In your list and on the Anki card'
      : listState.value === 'kept'
        ? `Kept for Jiten: click to run ${defaultPipeline.value.name}`
        : `${defaultPipeline.value.name}: save to the word's list${hasAnkiStep(defaultPipeline.value) ? ' and run its steps' : ''}`
  );
  const icon = computed(() =>
    working.value ? 'pi-spin pi-spinner' : listState.value === 'card' ? 'pi-check' : listState.value === 'kept' ? 'pi-bookmark-fill' : 'pi-plus-circle'
  );

  const menuItems = computed(() => [
    ...ordered.value.map((p, i) => ({
      label: i === 0 ? `${p.name} (default)` : p.name,
      icon: hasAnkiStep(p) ? 'pi pi-plus-circle' : 'pi pi-bookmark',
      disabled: !canRun(p),
      command: () => start(p),
    })),
    { separator: true },
    { label: 'Edit pipelines…', icon: 'pi pi-cog', command: () => navigateTo('/settings/pipelines') },
  ]);

  function failed(e: unknown, summary: string) {
    const err = e as { data?: { message?: string }; message?: string } | null;
    const detail = err?.data?.message ?? err?.message;
    toast.add({ severity: 'error', summary, detail, life: 6000, group: 'bottom' });
  }

  async function summaryOf(wordId: number, readingIndex: number): Promise<WordSummary> {
    const { words } = await $api<{ words: WordSummary[] }>('mined/word-summaries', { query: { words: `${wordId}:${readingIndex}` } });
    if (!words[0]) throw new Error('Jiten has no reading for that word.');
    return words[0];
  }

  /** The sentence's words in order, where each sits in its text. */
  async function sentenceWords(): Promise<{ word: ParsedWord; position: number }[]> {
    const parsed = await $api<ParsedWord[]>('vocabulary/parse', { query: { text: props.sentence.text } });
    const words: { word: ParsedWord; position: number }[] = [];
    let cursor = 0;
    for (const word of parsed) {
      const at = props.sentence.text.indexOf(word.originalText, cursor);
      if (at < 0) continue;
      cursor = at + word.originalText.length;
      if (word.wordId > 0) words.push({ word, position: at });
    }
    return words;
  }

  /** Runs the page's default, once the pipelines have loaded. */
  async function startDefault() {
    if (!ordered.value.length) {
      working.value = true;
      try {
        await pipelines.load();
      } catch (e) {
        failed(e, "Couldn't load your pipelines");
        return;
      } finally {
        working.value = false;
      }
    }
    return start(defaultPipeline.value);
  }

  async function start(pipeline: Pipeline) {
    const { wordId, readingIndex } = props;
    if (hasOwnWord.value) return run(pipeline, wordId!, readingIndex!, props.sentence.wordPosition, props.sentence.wordLength);

    working.value = true;
    try {
      pickingFor = pipeline;
      picking.value = await sentenceWords();
    } catch (e) {
      failed(e, "Couldn't read the sentence's words");
    } finally {
      working.value = false;
    }
  }

  function keptToast() {
    toast.add({ severity: 'success', summary: 'Kept for Jiten', life: 2500, group: 'bottom' });
  }

  /** Runs a pipeline for the word at this place in the sentence: save to the word's list, then the pipeline's steps. */
  async function run(pipeline: Pipeline, wordId: number, readingIndex: number, position: number, length: number) {
    picking.value = null;
    working.value = true;
    try {
      let state = list.stateOf(wordId, readingIndex, props.sentence.sentenceId);
      let saved = false;
      if (canSave.value && state == null) {
        try {
          state = await list.save(wordId, readingIndex, props.sentence.sentenceId, position, length);
          saved = true;
        } catch (e) {
          failed(e, "Couldn't save the sentence");
          return;
        }
        if (!hasOwnWord.value) pickedState.value = state;
      }
      if (state === 'card') return;
      if (!hasAnkiStep(pipeline)) {
        if (saved) keptToast();
        return;
      }

      // The sentence stays saved when Anki isn't set up yet.
      if (!configured.value) {
        settingsVisible.value = true;
        return;
      }
      let word: WordSummary;
      try {
        word = await summaryOf(wordId, readingIndex);
      } catch (e) {
        failed(e, "Couldn't add the card");
        return;
      }
      const outcome = await mineSentence(word, { ...props.sentence, wordPosition: position, wordLength: length }, pipeline);
      if (!outcome) return;
      if (outcome.kind === 'kept') {
        // No Anki step matched, or it skipped: the line stays kept for Jiten.
        if (saved) keptToast();
        return;
      }
      if (canSave.value) list.setState(wordId, readingIndex, props.sentence.sentenceId, 'card');
      if (!hasOwnWord.value) pickedState.value = 'card';
      emit('mined', word, outcome);
    } finally {
      working.value = false;
    }
  }
</script>

<template>
  <span class="inline-flex items-center" :class="boxed ? 'gap-0' : 'gap-0.5'">
    <button
      class="inline-flex items-center justify-center transition-colors"
      :class="[
        listState === 'card' ? 'text-green-500' : 'text-surface-400 hover:text-primary-500 cursor-pointer',
        boxed ? 'w-8 h-8 rounded-full hover:bg-surface-100 dark:hover:bg-surface-800' : '',
      ]"
      :disabled="working || listState === 'card' || (ordered.length > 0 && !canRun(defaultPipeline))"
      :title="title"
      :aria-label="title"
      @click="startDefault"
    >
      <i class="pi text-sm" :class="icon" />
    </button>
    <button
      class="inline-flex items-center justify-center text-surface-400 hover:text-primary-500 transition-colors cursor-pointer"
      :class="boxed ? 'w-5 h-8 rounded-full hover:bg-surface-100 dark:hover:bg-surface-800' : ''"
      :disabled="working"
      title="Other pipelines"
      aria-label="Other pipelines"
      aria-haspopup="true"
      @click="menu?.toggle($event)"
    >
      <i class="pi pi-chevron-down text-[0.6rem]" />
    </button>
    <Menu ref="menu" :model="menuItems" popup />
  </span>

  <Dialog :visible="picking != null" modal header="Which word is the sentence for?" :style="{ width: '28rem' }" @update:visible="picking = null">
    <p lang="ja" class="mb-3">{{ sentence.text }}</p>
    <div v-if="picking?.length" class="flex flex-wrap gap-2" lang="ja">
      <Button
        v-for="item in picking"
        :key="`${item.position}-${item.word.wordId}`"
        :label="item.word.originalText"
        severity="secondary"
        outlined
        size="small"
        @click="run(pickingFor, item.word.wordId, item.word.readingIndex, item.position, item.word.originalText.length)"
      />
    </div>
    <p v-else class="text-gray-500 dark:text-gray-400">Jiten found no dictionary words in this sentence.</p>
  </Dialog>

  <MineChoiceDialog :choice="choice" @answer="answer" />

  <MiningCardSettingsDialog v-model:visible="settingsVisible" />
</template>
