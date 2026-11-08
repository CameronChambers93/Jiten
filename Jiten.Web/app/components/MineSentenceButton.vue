<script setup lang="ts">
  import Button from 'primevue/button';
  import Dialog from 'primevue/dialog';
  import Menu from 'primevue/menu';
  import { useToast } from 'primevue/usetoast';
  import type { ExampleSentence, WordSummary } from '~/types';
  import type { MineOutcome } from '~/composables/useMineSentence';
  import type { ListSentenceState } from '~/composables/useSentenceList';

  /**
   * The sentence button (n1miner): saves the sentence to its word's list, then runs a pipeline. *Mine* adds it to Anki
   * as well; *Keep for Jiten*, on ▾, only saves it. The card is for the given word, or for one picked from the sentence.
   * When the word already has a note, the sentence can join that card instead of making another.
   */
  const props = defineProps<{
    sentence: ExampleSentence;
    /** The sentence's own word (its wordPosition/wordLength); without it the user picks one. */
    wordId?: number;
    readingIndex?: number;
    /** Round, padded, like the page's other icon buttons (the Mine page's row actions). */
    boxed?: boolean;
  }>();

  const emit = defineEmits<{
    /** Added to Anki, for the word given or picked. */
    mined: [word: WordSummary, outcome: MineOutcome];
  }>();

  type Pipeline = 'mine' | 'keep';

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

  const working = ref(false);
  const settingsVisible = ref(false);
  const picking = ref<{ word: ParsedWord; position: number }[] | null>(null);
  let pickingFor: Pipeline = 'mine';
  const menu = ref<InstanceType<typeof Menu> | null>(null);

  const hasOwnWord = computed(() => props.wordId != null && props.readingIndex != null && props.sentence.wordLength > 0);
  /** For a word picked from the sentence, what this button did to it (the row isn't tied to one word's list). */
  const pickedState = ref<ListSentenceState>();
  const listState = computed(() => (hasOwnWord.value ? list.stateOf(props.wordId, props.readingIndex, props.sentence.sentenceId) : pickedState.value));
  const canSave = computed(() => props.sentence.sentenceId > 0);

  // Rows are reused for other sentences (lists keyed by index), so this follows the sentence, not the mount.
  onMounted(() =>
    watch(
      () => [props.wordId, props.readingIndex, props.sentence.sentenceId, props.sentence.wordLength] as const,
      () => {
        pickedState.value = undefined;
        if (hasOwnWord.value && canSave.value) list.watch(props.wordId!, props.readingIndex!, props.sentence.sentenceId);
      },
      { immediate: true }
    )
  );

  const title = computed(() =>
    listState.value === 'card' ? 'In your list and on the Anki card' : listState.value === 'kept' ? 'Kept for Jiten: click to add to Anki' : 'Mine: save and add to Anki'
  );
  const icon = computed(() =>
    working.value ? 'pi-spin pi-spinner' : listState.value === 'card' ? 'pi-check' : listState.value === 'kept' ? 'pi-bookmark-fill' : 'pi-plus-circle'
  );

  const menuItems = computed(() => [
    { label: 'Mine', icon: 'pi pi-plus-circle', disabled: listState.value === 'card', command: () => start('mine') },
    { label: 'Keep for Jiten', icon: 'pi pi-bookmark', disabled: !canSave.value || listState.value != null, command: () => start('keep') },
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

  /** Runs a pipeline for the word at this place in the sentence: save to the word's list, then, for Mine, add to Anki. */
  async function run(pipeline: Pipeline, wordId: number, readingIndex: number, position: number, length: number) {
    picking.value = null;
    working.value = true;
    try {
      let state = list.stateOf(wordId, readingIndex, props.sentence.sentenceId);
      if (canSave.value && state == null) {
        try {
          state = await list.save(wordId, readingIndex, props.sentence.sentenceId, position, length);
        } catch (e) {
          failed(e, "Couldn't save the sentence");
          return;
        }
        if (!hasOwnWord.value) pickedState.value = state;
        if (pipeline === 'keep') toast.add({ severity: 'success', summary: 'Kept for Jiten', life: 2500, group: 'bottom' });
      }
      if (pipeline === 'keep' || state === 'card') return;

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
      const outcome = await mineSentence(word, { ...props.sentence, wordPosition: position, wordLength: length });
      if (outcome && canSave.value) list.setState(wordId, readingIndex, props.sentence.sentenceId, 'card');
      if (outcome && !hasOwnWord.value) pickedState.value = 'card';
      if (outcome) emit('mined', word, outcome);
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
      :disabled="working || listState === 'card'"
      :title="title"
      :aria-label="title"
      @click="start('mine')"
    >
      <i class="pi text-sm" :class="icon" />
    </button>
    <button
      class="inline-flex items-center justify-center text-surface-400 hover:text-primary-500 transition-colors cursor-pointer"
      :class="boxed ? 'w-5 h-8 rounded-full hover:bg-surface-100 dark:hover:bg-surface-800' : ''"
      :disabled="working"
      title="More ways to save"
      aria-label="More ways to save"
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
